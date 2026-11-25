"""Plan bounded training inputs and reconcile their effects; Pipeline owns navigation."""

import math
import re
import time
from dataclasses import dataclass, field
from decimal import Decimal, InvalidOperation

from bootstrap import configure_runtime

configure_runtime()

from maa.agent.agent_server import AgentServer
from maa.custom_action import CustomAction
from maa.custom_recognition import CustomRecognition
from maa.pipeline import JActionType, JClick, JClickKey, JOCR

from common import safe_json_loads


PREFIX = "__DailyTraining"
CENTERS = (188, 302, 417, 532)
SCENE_POINT = (75, 450)  # Observed input outside the purchase/energy modal.
MAX_BATCH = 10
INPUT_INTERVAL = 0.12
RESPONSE_TIMEOUT = 5.0
TIER_VALUES = {3: (500, 600), 2: (200, 240)}


@dataclass
class TrainingSession:
    mode: str
    limit: int | None
    tabs: tuple[str, str]
    confirmed: int = 0
    issued: int = 0
    pending: dict | None = None
    stop: str = ""
    room: int = 3
    capacity: tuple[int, int] | None = None
    empty_tabs: set[str] = field(default_factory=set)
    item_tier: int | None = None
    tier_has_stock_that_would_overflow: bool = False
    item_read_failures: int = 0
    selection: tuple | None = None
    selection_tries: int = 0
    zero_batches: int = 0
    room_complete: bool = False
    read_failures: int = 0
    last_log: str = ""


_SESSIONS: dict[str, TrainingSession] = {}


def _session(context):
    return _SESSIONS[context.tasker.controller.uuid]


def _param(raw):
    value = safe_json_loads(raw, {}) if isinstance(raw, str) else raw
    return value if isinstance(value, dict) else {}


def _log(session, message):
    if message != session.last_log:
        print(f"[调教] {message}", flush=True)
        session.last_log = message


def _find(context, image, suffix, roi=None):
    name = PREFIX + suffix
    override = {name: {"recognition": {"param": {"roi": list(roi)}}}} if roi else None
    detail = context.run_recognition(name, image, override)
    if detail is None:
        raise RuntimeError(f"识别未能启动：{name}")
    return detail if detail.hit and detail.box is not None else None


def _click(context, point):
    result = context.run_action_direct(JActionType.Click, JClick(target=tuple(point)))
    if result is None or not result.success:
        raise RuntimeError(f"输入未确认：{point}")


def _center(box):
    x, y, width, height = map(int, box)
    if width <= 0 or height <= 0:
        raise ValueError("识别框为空")
    return x + width // 2, y + height // 2


def _ocr(context, image, roi):
    detail = context.run_recognition_direct("OCR", JOCR(roi=tuple(roi), only_rec=False), image)
    if detail is None:
        raise RuntimeError("数字OCR未能启动")
    return [(str(r.text), tuple(map(int, r.box))) for r in detail.all_results if getattr(r, "text", "")]


def _integer(text):
    normalized = re.sub(r"\s+", "", text).replace("，", ",")
    match = re.fullmatch(r"[xX×]?([0-9]+|[0-9]{1,3}(?:,[0-9]{3})+)", normalized)
    return int(match[1].replace(",", "")) if match else None


def _percent(text):
    normalized = re.sub(r"\s+", "", text).replace("％", "%")
    match = re.fullmatch(r"[（(]?(\d+(?:\.\d{1,2})?)%[）)]?", normalized)
    if not match:
        return None
    try:
        value = Decimal(match[1]) * 100
        return int(value) if value == int(value) and 0 <= value <= 10000 else None
    except (InvalidOperation, ValueError):
        return None


def _page(context, image):
    if _find(context, image, "PromptTitle"):
        return None
    common = _find(context, image, "CommonSelected")
    event = _find(context, image, "EventSelected")
    if bool(common) == bool(event):
        return None
    # The title is translucent over the animated character. The selected tab
    # has a stable plate and is also the anchor for the item rows below it.
    selected = common if common else event
    return ("common" if common else "event"), int(selected.box[1]) - 950


def _snapshot(context, image, tab, shift):
    energy_matches = []
    for text, _ in _ocr(context, image, (555, 60, 140, 46)):
        match = re.fullmatch(r"(\d+)\s*/\s*(\d+)", text.strip())
        if match and 0 <= int(match[1]) <= 5000 and int(match[2]) > 0:
            energy_matches.append(int(match[1]))
    percentages = [_percent(text) for text, _ in _ocr(context, image, (580, 355, 120, 60))]
    percentages = [value for value in percentages if value is not None]
    if len(energy_matches) != 1 or len(percentages) != 1:
        return None
    rows = _ocr(context, image, (130, 1008 + shift, 450, 162))
    items = []
    for index, x in enumerate(CENTERS):
        data = {"index": index, "value": None, "percent": None, "count": None, "selected": False}
        values, percents, counts = [], [], []
        for text, box in rows:
            cx, cy = _center(box)
            if abs(cx - x) > 54:
                continue
            normalized = re.sub(r"\s+", "", text).replace("，", ",")
            if 1005 + shift <= cy < 1033 + shift:
                value = _percent(normalized)
                if value is not None:
                    percents.append(value)
            elif 1033 + shift <= cy < 1070 + shift:
                match = re.fullmatch(r"[+＋]([\d,]+)", normalized)
                value = _integer(match[1]) if match else None
                if value is not None and 0 < value <= 100000:
                    values.append(value)
            elif 1122 + shift <= cy <= 1165 + shift and cx >= x + 8:
                value = _integer(normalized)
                if value is not None:
                    counts.append(value)
        for name, candidates in (("value", values), ("percent", percents), ("count", counts)):
            if len(candidates) == 1:
                data[name] = candidates[0]
        data["selected"] = bool(_find(context, image, "SelectedCorner", (x - 54, 1060 + shift, 108, 40)))
        # Legacy allow-rare preferences never override this hard prohibition.
        data["forbidden"] = (
            (tab == "common" and index == 3)
            or (data["value"] is not None and data["value"] >= 2000)
            or bool(_find(context, image, "ForbiddenFruit", (x - 54, 1070 + shift, 108, 98)))
        )
        items.append(data)
    return {"tab": tab, "energy": energy_matches[0], "progress": percentages[0], "items": items, "rows": rows}


def _capacity(snapshot, previous=None):
    """Keep all integer limits consistent with rounded item percentages.

    b-.5 <= 10000*value/limit < b+.5. A plausible round-number estimate
    must never authorize an additional resource input.
    """
    low, high = previous or (1, 10000000)
    evidence = 0
    for item in snapshot["items"]:
        value, bps = item["value"], item["percent"]
        if value is None or bps is None or bps <= 0:
            continue
        low = max(low, 20000 * value // (2 * bps + 1) + 1)
        high = min(high, 20000 * value // (2 * bps - 1))
        evidence += 1
    return (low, high) if evidence and low <= high else None


def _estimate(bounds):
    low, high = bounds
    for step in (100000, 10000, 1000, 100, 10, 1):
        first, last = math.ceil(low / step), high // step
        if first == last:
            return first * step
    return (low + high) // 2


def _safe_remaining(progress, bounds):
    if progress >= 10000:
        return 0
    # Worst case of largest current integer and smallest possible room limit.
    return max(0, (19999 - 2 * progress) * bounds[0] // 20000 + 1)


def _remaining_text(progress, bounds, planned=0):
    if progress >= 10000:
        return "调教完成"
    low = _safe_remaining(progress, bounds) - planned
    # The lower edge of the displayed percentage gives the largest possible gap.
    high = min(bounds[1], (20001 - 2 * progress) * bounds[1] // 20000) - planned
    if high == 0:
        return "调教完成"
    points = str(low) if low == high else f"{low}–{high}"
    return f"离调教完成还差{points}点"


def _settle(session, snapshot):
    pending = session.pending
    item = snapshot["items"][pending["index"]]
    elapsed = time.monotonic() - pending["time"]
    if snapshot["tab"] != pending["tab"] or item["value"] != pending["value"] or item["count"] is None:
        if elapsed >= RESPONSE_TIMEOUT:
            raise RuntimeError("连点后道具或库存无法确认，保留未结清账目，停止消耗")
        return
    consumed = pending["stock"] - item["count"]
    if not 0 <= consumed <= pending["sent"]:
        raise RuntimeError("库存变化超出本批发送次数，停止消耗")
    observed = snapshot["progress"] - pending["progress"]
    low, high = pending["capacity"]
    gain = consumed * pending["value"] * 10000
    # Two rounded progress observations can differ by one basis point.
    if gain < (observed - 1) * low or gain > (observed + 1) * high:
        if elapsed >= RESPONSE_TIMEOUT:
            raise RuntimeError("库存减少与进度增长不一致，保留本批账目，不盲目补点")
        return
    if snapshot["energy"] < pending["energy"] - consumed:
        raise RuntimeError("调教点减少超过已核实道具数，停止消耗")
    if pending.get("last_count") != consumed:
        pending["last_count"] = consumed
        pending["stable_since"] = time.monotonic()
        pending["observations"] = 1
    else:
        pending["observations"] += 1
    complete = consumed == pending["sent"]
    stable_partial = elapsed >= 0.8 and pending["observations"] >= 2 and time.monotonic() - pending["stable_since"] >= 0.3
    if not complete and not stable_partial:
        if elapsed >= RESPONSE_TIMEOUT:
            raise RuntimeError("本批响应未能稳定，不再补点")
        return
    session.confirmed += consumed
    session.zero_batches = session.zero_batches + 1 if consumed == 0 else 0
    session.pending = None
    _log(
        session,
        f"核对本批：发出{pending['sent']}次，库存{pending['stock']}→{item['count']}，实际使用{consumed}个，"
        f"进度{pending['progress'] / 100:.2f}%→{snapshot['progress'] / 100:.2f}%；"
        f"{_remaining_text(snapshot['progress'], pending['capacity'])}；"
        f"漏点{pending['sent'] - consumed}次，本轮已确认{session.confirmed}次。",
    )
    if session.zero_batches >= 3:
        session.stop = "连续三批没有实际消耗，停止补点"


def _step(context, image, session):
    page = _page(context, image)
    if page is None:
        return
    tab, shift = page
    snapshot = _snapshot(context, image, tab, shift)
    if snapshot is None:
        session.read_failures += 1
        if session.pending and time.monotonic() - session.pending["time"] >= RESPONSE_TIMEOUT:
            raise RuntimeError("连点后无法读取点数或进度，保留账目并停止")
        if session.read_failures >= 6:
            raise RuntimeError("连续六次无法明确读取调教数字，停止消耗")
        return
    session.read_failures = 0
    if session.pending:
        _settle(session, snapshot)
        return
    if session.stop:
        return
    if session.limit is None:
        session.limit = snapshot["energy"]
        _log(
            session,
            f"全部点数模式：本轮现有调教点{session.limit}，只处理当前房间，分批核对消耗后继续。",
        )
    if snapshot["energy"] == 0:
        session.stop = "调教点已为0；不补充能量、不购买道具"
    elif session.confirmed >= session.limit:
        session.stop = (
            f"已确认使用{session.confirmed}次，达到本轮初始{session.limit}点额度，不使用之后新增的点数"
            if session.mode == "all"
            else f"已确认使用{session.confirmed}次，达到指定{session.limit}次额度"
        )
    elif snapshot["progress"] >= 10000 or session.room_complete:
        session.stop = "当前房间已完成"
    if session.stop:
        _log(
            session,
            f"{session.stop}；当前剩余调教点{snapshot['energy']}，房间进度{snapshot['progress'] / 100:.2f}%。",
        )
        return
    preferred = next((name for name in session.tabs if name not in session.empty_tabs), None)
    if preferred is None:
        if session.item_tier == 3 and not session.tier_has_stock_that_would_overflow:
            session.item_tier = 2
            session.empty_tabs.clear()
            _log(session, "两栏三级道具已用完，改用200／240分的二级道具。")
        else:
            session.stop = (
                "三级道具仍有库存，剩余进度无法安全容纳，停止调教"
                if session.item_tier == 3 else "允许的两栏均无可明确使用且不会溢出的道具"
            )
            _log(session, session.stop)
        return

    items = [item for item in snapshot["items"] if not item["forbidden"]]
    tier_values = TIER_VALUES.get(session.item_tier)
    if tier_values is not None and tab == preferred:
        uncertain = any(
            (item["value"] in tier_values and item["count"] is None)
            or (item["value"] is None and item["count"] != 0
                and (item["count"] is not None or item["percent"] is not None))
            for item in items
        )
        if uncertain:
            session.item_read_failures += 1
            if session.item_read_failures >= 6:
                session.stop = f"无法确认{session.item_tier}级道具的分数或库存，停止调教"
                _log(session, session.stop)
            return
        session.item_read_failures = 0
    stocked = [
        item for item in items
        if item["value"] and item["count"] is not None and item["count"] > 0
        and (tier_values is None or item["value"] in tier_values)
    ]
    bounds = session.capacity
    if tier_values is None or (tab == preferred and stocked):
        bounds = _capacity(snapshot, bounds)
        if bounds is None:
            session.stop = "无法用实际加成和百分比约束房间上限，停止消耗"
            _log(session, session.stop)
            return
        session.capacity = bounds
    if tab != preferred:
        result = _find(context, image, "CommonTab" if preferred == "common" else "EventTab")
        if result:
            _click(context, _center(result.box))
        return
    remaining = _safe_remaining(snapshot["progress"], bounds) if bounds is not None else 0
    eligible = [item for item in stocked if item["value"] <= remaining]
    if not eligible:
        session.empty_tabs.add(tab)
        if session.item_tier == 3 and stocked:
            session.tier_has_stock_that_would_overflow = True
        tab_label = "通用" if tab == "common" else "活动"
        tier_label = f"{session.item_tier}级" if session.item_tier is not None else ""
        _log(session, f"{tab_label}栏无安全可用的{tier_label}道具，尝试备用栏。")
        return
    item = min(eligible, key=lambda data: (data["value"], data["index"]))
    selected = [data["index"] for data in snapshot["items"] if data["selected"]]
    selection = (tab, item["index"])
    if selected != [item["index"]]:
        if selection != session.selection:
            session.selection, session.selection_tries = selection, 0
        session.selection_tries += 1
        if session.selection_tries > 3:
            session.stop = "三次选物后仍无法确认选中标记，未执行调教"
            _log(session, session.stop)
            return
        _click(context, (CENTERS[item["index"]], 1120 + shift))
        return
    session.selection_tries = 0
    count = min(MAX_BATCH, session.limit - session.confirmed, snapshot["energy"], item["count"], remaining // item["value"])
    if count <= 0 or session.issued + count > 3 * session.limit + 30:
        session.stop = "达到安全点击边界，停止消耗"
        _log(session, session.stop)
        return
    pending = {
        "tab": tab, "index": item["index"], "value": item["value"], "stock": item["count"],
        "energy": snapshot["energy"], "progress": snapshot["progress"], "capacity": bounds,
        "time": time.monotonic(), "sent": 0,
    }
    session.pending = pending
    gain = count * item["value"]
    item_label = f"{'通用' if tab == 'common' else '活动'}第{item['index'] + 1}格道具"
    after = _remaining_text(snapshot["progress"], bounds, gain)
    _log(
        session,
        f"第{session.room}房计划用{count}个{item_label}（单个+{item['value']}，合计+{gain}点）。"
        f"按计划用完后{after}。当前进度{snapshot['progress'] / 100:.2f}%，"
        f"上限估计{_estimate(bounds)}（整数范围{bounds[0]}–{bounds[1]}），"
        f"库存{item['count']}，调教点{snapshot['energy']}。",
    )
    # One bounded resource-input batch, never a page-navigation loop. This point
    # stays outside observed modal controls if a prompt arrives during the batch.
    for index in range(count):
        if context.tasker.stopping:
            break
        pending["sent"] += 1
        session.issued += 1
        _click(context, SCENE_POINT)
        if index + 1 < count:
            time.sleep(INPUT_INTERVAL)
    pending["time"] = time.monotonic()


@AgentServer.custom_action("DailyTrainingInitialize")
class DailyTrainingInitialize(CustomAction):
    def run(self, context, argv):
        try:
            start = context.get_node_data("DailyTrainingClickCenterStart")
            if not isinstance(start, dict) or not isinstance(start.get("next"), list):
                raise ValueError("无法读取调教目标，禁止回退为默认次数")
            # Native GetNodeData serializes next entries as node-attribute objects,
            # even when Interface originally supplied simple node-name strings.
            targets = {
                entry.get("name") if isinstance(entry, dict) else entry
                for entry in start["next"] if isinstance(entry, (str, dict))
            }
            selected = targets & {"DailyTrainingClickCenterAllPoints", "DailyTrainingClickCenterSlow"}
            if len(selected) != 1:
                raise ValueError("调教目标缺失或冲突，禁止回退为默认次数")
            all_points = "DailyTrainingClickCenterAllPoints" in selected
            limit = None
            if not all_points:
                slow = context.get_node_data("DailyTrainingClickCenterSlow") or {}
                limit = slow.get("max_hit")
                if type(limit) is not int or not 1 <= limit <= 100:
                    raise ValueError("指定次数必须为1–100")
            scope = context.get_node_data("DailyTrainingConfigItemScope") or {}
            expected = scope.get("recognition", {}).get("param", {}).get("expected", [])
            common_first = bool(expected and expected[0] == "优先通用道具")
            preference = context.get_node_data("DailyTrainingConfigPreferTierThree") or {}
            prefer_tier_three = preference.get("attach", {}).get("prefer_tier_three") is True
            session = TrainingSession(
                mode="all" if all_points else "count", limit=limit,
                tabs=("common", "event") if common_first else ("event", "common"),
                item_tier=3 if prefer_tier_three else None,
            )
            _SESSIONS[context.tasker.controller.uuid] = session
            goal = "全部点数（按首次读取的现有点数确定额度）" if all_points else f"指定{limit}次"
            _log(
                session,
                f"本轮目标：{goal}；只处理当前房间。按画面计算安全连点批次，核对实际消耗后补漏点；"
                "禁用桃子与高价值道具，禁止购买及补充能量。"
                + ("优先使用500／600分三级道具，耗尽后改用200／240分二级道具。" if prefer_tier_three else ""),
            )
            return True
        except Exception as error:
            print(f"[调教] 初始化失败：{error}", flush=True)
            return False


@AgentServer.custom_recognition("DailyTrainingState")
class DailyTrainingState(CustomRecognition):
    def analyze(self, context, argv):
        try:
            session = _session(context)
            mode = _param(argv.custom_recognition_param).get("mode", "active")
            if mode == "finished":
                hit = bool(session.stop) and session.pending is None
            elif mode == "uncertain":
                hit = bool(session.stop) and session.pending is not None
            elif mode == "active":
                hit = not session.stop or session.pending is not None
            else:
                hit = False
            return CustomRecognition.AnalyzeResult(
                box=(0, 0, 720, 1280) if hit else None,
                detail={
                    "mode": mode, "goal": session.mode, "limit": session.limit, "reason": session.stop,
                    "confirmed": session.confirmed, "issued": session.issued,
                },
            )
        except Exception as error:
            return CustomRecognition.AnalyzeResult(box=None, detail={"error": str(error)})


@AgentServer.custom_action("DailyTrainingStep")
class DailyTrainingStep(CustomAction):
    def run(self, context, argv):
        session = _session(context)
        try:
            image = context.tasker.controller.post_screencap().wait().get()
            if image is None:
                raise RuntimeError("截图失败")
            _step(context, image, session)
            return True
        except Exception as error:
            session.stop = str(error)
            _log(session, f"停止调教：{error}；不再发出资源点击，保留已有消耗账目。")
            return False


@AgentServer.custom_action("DailyTrainingEnterRoom")
class DailyTrainingEnterRoom(CustomAction):
    def run(self, context, argv):
        try:
            session = _session(context)
            if session.pending or session.stop:
                return False
            _, y = _center(argv.box)
            session.room = min(3, max(1, (y - 120 + 85) // 170 + 1))
            session.capacity = None
            session.empty_tabs.clear()
            session.selection = None
            session.selection_tries = 0
            session.room_complete = False
            _click(context, (360, min(570, y + 65)))
            return True
        except Exception as error:
            print(f"[调教] 进入房间失败：{error}", flush=True)
            return False


@AgentServer.custom_action("DailyTrainingFilterArrow")
class DailyTrainingFilterArrow(CustomAction):
    def run(self, context, argv):
        try:
            _, y = _center(argv.box)
            if not 720 <= y <= 970:
                return False
            _click(context, (585, y))
            return True
        except Exception as error:
            print(f"[调教] 切换筛选失败：{error}", flush=True)
            return False


@AgentServer.custom_action("DailyTrainingClosePrompt")
class DailyTrainingClosePrompt(CustomAction):
    def run(self, context, argv):
        try:
            session = _session(context)
            kind = _param(argv.custom_action_param).get("kind")
            if kind in {"energy", "shortage"}:
                session.stop = "点数不足" if kind == "energy" else "道具不足或出现购买提示"
                image = context.tasker.controller.post_screencap().wait().get()
                close = _find(context, image, "SafeModalClose")
                if close:
                    _click(context, _center(close.box))
                else:
                    result = context.run_action_direct(JActionType.ClickKey, JClickKey(key=[4]))
                    if result is None or not result.success:
                        return False
                _log(session, f"检测到{session.stop}，仅关闭弹窗；不购买、不补充能量。")
            elif kind in {"story", "limit"}:
                session.room_complete = session.room_complete or kind == "limit"
                _click(context, _center(argv.box))
                _log(session, "检测到剧情邀请，选择取消，复查房间状态。" if kind == "story" else "检测到已达到调教上限，关闭提示，复查本批消耗。")
            else:
                return False
            return True
        except Exception as error:
            print(f"[调教] 弹窗处理失败：{error}", flush=True)
            return False
