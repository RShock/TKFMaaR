"""Recognize daily ticket stages and reconcile one bounded sweep per category.

Pipeline owns navigation. This module only selects an observed control and keeps
the resource ledger shared by recovery attempts on the same controller.
"""

import hashlib
import json
import os
import re
import time
from dataclasses import dataclass, field
from datetime import date, timedelta
from pathlib import Path
from tempfile import NamedTemporaryFile

from bootstrap import configure_runtime

configure_runtime()

from maa.agent.agent_server import AgentServer
from maa.custom_action import CustomAction
from maa.custom_recognition import CustomRecognition
from maa.pipeline import JActionType, JClick, JOCR, JSwipe

from common import param_mode, safe_json_loads


KINDS = ("War", "Intel", "Commerce")
LABELS = {"War": "战争演习-特IV", "Intel": "情报搜集-高", "Commerce": "商业贸易-特IV"}
PREFIX = "__DailyAffairs"
PAGE_BOX = (0, 0, 720, 1280)
SWEEP_INTERVAL = timedelta(days=3)
CONFIRMED = "已确认门票按预期扣除，扫荡完成"
CONFIRMED_LINES = {f"[每日政务] {label}：{CONFIRMED}。": kind for kind, label in LABELS.items()}
LOG_ENTRY = re.compile(r"^\[(?P<day>\d{4}-\d{2}-\d{2}) [^\]]+\]\[INF\] ")


@dataclass
class AffairsSession:
    selected: tuple[str, ...]
    history_path: Path | None = None
    last_sweeps: dict[str, date] = field(default_factory=dict)
    done: dict[str, str] = field(default_factory=dict)
    reserved: dict[str, int] = field(default_factory=dict)
    pending: dict | None = None
    opened: str | None = None
    maximized: int = 0
    return_home: bool = False
    handed_off: bool = False
    actions: dict[tuple[str, str], int] = field(default_factory=dict)
    cached_key: bytes | None = None
    cached_at: float = 0
    cached_decision: tuple | None = None
    first_changed_frame: float | None = None


_SESSIONS: dict[str, AffairsSession] = {}


def _session(context):
    return _SESSIONS[context.tasker.controller.uuid]


def _write_sweep_history(path: Path, last_sweeps: dict[str, date]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temp_path = None
    try:
        with NamedTemporaryFile(mode="w", encoding="utf-8", dir=path.parent, delete=False) as stream:
            temp_path = Path(stream.name)
            json.dump({kind: day.isoformat() for kind, day in last_sweeps.items()}, stream)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        temp_path.replace(path)
    finally:
        if temp_path is not None:
            temp_path.unlink(missing_ok=True)


def _read_sweep_history(path: Path, logs_dir: Path, instance_id: str, today: date) -> dict[str, date]:
    try:
        stored = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        stored = {}
    if not isinstance(stored, dict):
        raise ValueError("扫荡日期记录格式无效")
    last_sweeps = {kind: date.fromisoformat(stored[kind]) for kind in KINDS if kind in stored}
    previous = last_sweeps.copy()

    # Older releases confirmed consumption in the frontend log. Only recent
    # dates can affect the interval, including size-rotated files for those days.
    for days_ago in range(SWEEP_INTERVAL.days):
        day = today - timedelta(days=days_ago)
        prefix = f"log-{day:%Y%m%d}"
        for log in logs_dir.glob(f"{prefix}*.log"):
            with log.open(encoding="utf-8-sig", errors="replace") as stream:
                for line in stream:
                    if CONFIRMED not in line:
                        continue
                    match = LOG_ENTRY.match(line)
                    if match is None or match["day"] != day.isoformat():
                        continue
                    tags, _, message = line[match.end():].rstrip().partition("] [每日政务] ")
                    kind = CONFIRMED_LINES.get("[每日政务] " + message)
                    if kind is None or last_sweeps.get(kind, date.min) >= day:
                        continue
                    tags = dict(re.findall(r"\[([^=\[\]]+)=([^\]]*)", tags))
                    instance = tags.get("inst", "")
                    if tags.get("src") == "Agent" and (instance == instance_id or instance.endswith("/" + instance_id)):
                        last_sweeps[kind] = day
    if last_sweeps != previous:
        _write_sweep_history(path, last_sweeps)
    return last_sweeps


def _find(context, image, name, roi=None):
    node = PREFIX + name
    override = {node: {"recognition": {"param": {"roi": list(roi)}}}} if roi else None
    result = context.run_recognition(node, image, override)
    return result if result is not None and result.hit and result.box is not None else None


def _integer(context, image, roi):
    result = context.run_recognition_direct(
        "OCR", JOCR(expected=[], roi=tuple(roi), only_rec=True, threshold=0.3), image
    )
    if result is None:
        return None
    text = "".join(item.text for item in (result.all_results or []) if getattr(item, "text", ""))
    text = re.sub(r"\s+", "", text).replace("，", ",")
    if not re.fullmatch(r"\d+|\d{1,3}(?:,\d{3})+", text):
        return None
    return int(text.replace(",", ""))


def _highest_row(context, image, kind):
    level = _find(context, image, kind + "Level")
    titles = _find(context, image, kind + "Stage") if level else None
    if not titles:
        return None
    # A level belongs to the title immediately below it, not a similar row elsewhere.
    matches = [row for row in titles.filtered_results if any(
        39 <= tuple(row.box)[1] - tuple(header.box)[1] <= 57
        for header in level.filtered_results
    )]
    return matches[0] if len(matches) == 1 else None


def _tickets(context, image):
    result = context.run_recognition_direct(
        "OCR", JOCR(expected=[], roi=(369, 0, 75, 42), only_rec=True, threshold=0.3), image
    )
    if result is None:
        return None
    text = "".join(item.text for item in (result.all_results or []) if getattr(item, "text", ""))
    match = re.fullmatch(r"\s*([0-3])\s*[/／]\s*3\s*", text)
    return int(match[1]) if match else None


def _stamina(context, image):
    result = context.run_recognition_direct(
        "OCR", JOCR(expected=[], roi=(500, 0, 125, 42), only_rec=True, threshold=0.3), image
    )
    if result is None:
        return None
    text = "".join(item.text for item in (result.all_results or []) if getattr(item, "text", ""))
    match = re.fullmatch(r"\s*(\d+)\s*[/／]\s*(\d+)\s*", text)
    return int(match[1]) if match else None


def _snapshot(context, image, kind, title, start):
    _, y, _, h = title.box
    top, bottom = y + h, start.box[1]
    width = image.shape[1]
    if bottom - top < 90:
        return None
    values, rows = {}, []
    for field, icon in (("ticket", kind), ("coupon", "Coupon"), ("stamina", "Stamina")):
        matches = _find(context, image, icon + "PreviewIcon", (0, top, width, bottom - top))
        boxes = sorted((tuple(r.box) for r in matches.filtered_results), key=lambda b: b[1]) if matches else []
        if len(boxes) != 2:
            return None
        for suffix, (x, iy, iw, ih) in zip(("", "_after"), boxes):
            # The number belongs to the pill immediately to the right of this icon.
            left = x + iw
            right = x + 121
            if right > width:
                return None
            values[field + suffix] = _integer(context, image, (left, iy + ih // 2 - 16, right - left, 33))
        rows.append(tuple(b[1] + b[3] // 2 for b in boxes))
    before, after = zip(*rows)
    if (max(before) - min(before) > 10 or max(after) - min(after) > 10
            or min(after) - max(before) < 44 or any(v is None for v in values.values())):
        return None
    gap = min(before) - top
    if 30 <= gap <= 95:
        # Compact panel: the resource row follows the title without a selector.
        return values | {"count": 1}, True, None
    if gap <= 95:
        return None
    selector = (0, top, width, gap - 20)
    diamond = _find(context, image, "MaxDiamond", selector)
    if not diamond:
        return None
    result = context.run_recognition_direct(
        "OCR", JOCR(expected=[], roi=selector, threshold=0.3), image
    )
    counts = [m[1] for item in (result.all_results or [])
              if (m := re.fullmatch(r"扫荡([1-3])次", re.sub(r"\s+", "", item.text or "")))] if result else []
    return (values | {"count": int(counts[0])}, False, diamond) if len(counts) == 1 else None


def _choice(mode, target=PAGE_BOX, **detail):
    return mode, tuple(target), detail


def _decide(context, image, session):
    # Native alternatives share a frame. Reuse its observations for this short
    # round only; action invalidation and the clock prevent stale pending timers.
    key = hashlib.blake2b(image.tobytes(), digest_size=16).digest()
    if session.cached_key == key and time.monotonic() - session.cached_at < 1:
        return session.cached_decision
    decision = _observe(context, image, session)
    if decision:
        mode, _, detail = decision
        kind = next((k for k in session.selected if k not in session.done), None)
        navigation = {"expedition", "current_tab", "select_affairs", "enter_affairs", "category", "stage", "open_sweep"}
        if kind and (mode in navigation or detail.get("wrong_stage")) and session.actions.get((kind, mode), 0) >= 3:
            status = "；上次提交未确认，不再提交" if session.pending else ""
            decision = _choice("skip", reason=f"同一项导航已尝试3次仍未推进，跳过本项{status}")
    session.cached_key, session.cached_decision = key, decision
    session.cached_at = time.monotonic()
    return decision


def _observe(context, image, session):
    kind = next((item for item in session.selected if item not in session.done), None)
    # Do not see navigation through a mask. Returning no business match gives
    # the wrapper's parallel known-interruption candidates this same frame.
    for name in (
        "TkfmRecoveryReturnTitleTarget", "TkfmRecoverySessionExpiredTarget",
        "GameCloseGift", "GameCloseAnnouncement", "GameCloseEventSignIn",
        "TkfmRecoveryMonthlyCardTarget", "TkfmRecoveryExitGameTarget",
    ):
        interruption = context.run_recognition(name, image)
        if interruption is None or interruption.hit:
            return None
    result_title = _find(context, image, "ResultTitle")
    if result_title:
        button = _find(context, image, "ResultDone")
        return _choice("result_done", button.box) if button else None

    settings = _find(context, image, "SettingsTitle")
    if settings:
        close = _find(context, image, "SettingsClose")
        if kind is None or session.return_home or session.opened != kind:
            return _choice("close_sweep", close.box) if close else None
        if session.pending:
            if time.monotonic() - session.pending["time"] >= 20:
                return _choice("close_sweep", close.box, skip="提交后20秒仍停在设置页，保留额度，不重复提交") if close else None
            return _choice("pending_wait")
        start = _find(context, image, "SweepStart")
        preview = _snapshot(context, image, kind, settings, start) if start else None
        if preview is None:
            return None
        snapshot, single, diamond = preview
        remaining = 3 - session.reserved.get(kind, 0)
        maximum = min(snapshot["ticket"], snapshot["coupon"], snapshot["stamina"], remaining)
        if maximum <= 0:
            return _choice("close_sweep", close.box, skip="门票、扫荡券、体力或本轮额度不足") if close else None
        if single and (maximum != 1 or snapshot["stamina"] - snapshot["stamina_after"] != 1):
            return None
        if not single and (session.maximized == 0 or snapshot["count"] != maximum):
            if session.maximized >= 3:
                return _choice("close_sweep", close.box, skip="最大次数未能正确选中") if close else None
            return _choice("maximize", diamond.box)
        count = snapshot["count"]
        if not (
            1 <= count <= 3 and 0 <= snapshot["ticket_after"] < snapshot["ticket"] <= 3
            and snapshot["ticket"] - snapshot["ticket_after"] == count
            and snapshot["coupon"] - snapshot["coupon_after"] == count
            and snapshot["coupon_after"] >= 0 and snapshot["stamina_after"] >= 0
        ):
            return None
        return _choice("submit", start.box, snapshot=snapshot, single=single)

    home = context.run_recognition("CommonMainReady", image)
    if home and home.hit:
        if kind is None:
            return _choice("handoff")
        button = _find(context, image, "Expedition")
        return _choice("expedition", button.box) if button else None

    castle = _find(context, image, "Castle")
    if session.return_home or kind is None:
        if castle:
            return _choice("return_home", castle.box)
        back = _find(context, image, "Back")
        return _choice("back", back.box) if back else None

    detail = _find(context, image, kind + "Detail")
    ticket_icon = _find(context, image, kind + "Ticket")
    sweep = _find(context, image, "OpenSweep") if ticket_icon else None
    if detail and ticket_icon and sweep and _find(context, image, kind + "DetailRank"):
        tickets = _tickets(context, image)
        if tickets is None:
            return None
        if session.pending:
            pending = session.pending
            expected = pending["snapshot"]["ticket_after"]
            if tickets == expected:
                return _choice("settled", tickets=tickets)
            if tickets < pending["snapshot"]["ticket"]:
                return _choice("skip", reason="门票扣除与预期不一致，停止本项追加消耗")
            if time.monotonic() - pending["time"] >= 20:
                return _choice("skip", reason="提交后未确认门票扣除，保留已提交额度")
            return _choice("pending_wait")
        if tickets == 0:
            return _choice("skip", reason="门票为0，正常跳过")
        if session.reserved.get(kind, 0):
            return _choice("skip", reason="本项已有提交记录，不重复消耗")
        stamina = _stamina(context, image)
        if stamina is None:
            return None
        if stamina == 0:
            return _choice("skip", reason="当前体力为0，不用药、不购买，正常跳过")
        return _choice("open_sweep", sweep.box)

    # A complete list row is distinct from the large stage title. Never fall
    # back to the ordinary high-cost levels when the ticket stage is missing.
    if ticket_icon and sweep and castle:
        back = _find(context, image, "Back")
        return _choice("back", back.box, wrong_stage=True) if back else None
    if ticket_icon and castle and _find(context, image, kind + "ListMarker"):
        if not session.pending and _tickets(context, image) == 0:
            return _choice("skip", reason="列表显示本类门票0/3，直接跳过")
        stage = _highest_row(context, image, kind)
        if stage:
            return _choice("stage", stage.box, score=round(stage.score, 3))
        if session.actions.get((kind, "scroll"), 0) < 6:
            return _choice("scroll")
        return _choice("skip", reason="最高门票关卡未找到或未解锁，不降级消耗")

    category = _find(context, image, kind + "Category")
    if category and castle:
        return _choice("category", category.box)
    if castle and any(_find(context, image, item + "Category") for item in KINDS):
        if session.actions.get((kind, "scroll"), 0) < 6:
            return _choice("scroll")
        return _choice("skip", reason="未找到所选日常分类")

    enter = _find(context, image, "Enter")
    if enter:
        if _find(context, image, "ActivitySelected"):
            return _choice("enter_affairs", enter.box)
        thumb = _find(context, image, "ActivityThumb")
        if thumb:
            return _choice("select_affairs", thumb.box)
    tab = _find(context, image, "CurrentTab")
    if tab:
        return _choice("current_tab", tab.box)
    if castle:
        return _choice("return_home", castle.box)
    return None


@AgentServer.custom_action("DailyAffairsInitialize")
class DailyAffairsInitialize(CustomAction):
    def run(self, context, argv):
        selected = tuple(
            kind for kind in KINDS
            if (context.get_node_data("DailyAffairs" + kind + "Config") or {}).get("attach", {}).get("selected", True) is True
        )
        session = AffairsSession(selected=selected, handed_off=not selected)
        _SESSIONS[context.tasker.controller.uuid] = session
        if not selected:
            print("[每日政务] 三项扫荡均已关闭。", flush=True)
            return True

        raw = argv.custom_action_param
        params = safe_json_loads(raw, {}) if isinstance(raw, str) else raw
        today = date.today()
        try:
            if not isinstance(params, dict) or not all(params.get(key) for key in ("instance_id", "config_dir", "logs_dir")):
                raise ValueError("缺少当前实例的扫荡记录位置")
            instance_id = params["instance_id"]
            key = hashlib.sha256(instance_id.encode("utf-8")).hexdigest()
            session.history_path = Path(params["config_dir"]) / "daily-affairs" / f"{key}.json"
            session.last_sweeps = _read_sweep_history(session.history_path, Path(params["logs_dir"]), instance_id, today)
        except (OSError, TypeError, ValueError) as error:
            session.handed_off = True
            print(f"[每日政务] 无法读取扫荡日期记录，本轮跳过三个前置扫荡。{error}", flush=True)
            return True

        eligible = []
        for kind in selected:
            last = session.last_sweeps.get(kind)
            if last is not None and today - last < SWEEP_INTERVAL:
                due = last + SWEEP_INTERVAL
                print(f"[每日政务] {LABELS[kind]}上次耗券日期为 {last:%Y-%m-%d}，最早 {due:%Y-%m-%d} 再扫荡。", flush=True)
            else:
                eligible.append(kind)
        session.selected = tuple(eligible)
        session.handed_off = not eligible
        if eligible:
            names = "、".join(LABELS[kind] for kind in eligible)
            print(f"[每日政务] 本轮可扫荡：{names}。每项最多3次。", flush=True)
        return True


@AgentServer.custom_recognition("DailyAffairsState")
class DailyAffairsState(CustomRecognition):
    def analyze(self, context, argv):
        try:
            session = _session(context)
            mode = param_mode(argv.custom_recognition_param)
            if mode == "needed":
                return CustomRecognition.AnalyzeResult(
                    box=None if session.handed_off else PAGE_BOX, detail={"handed_off": session.handed_off}
                )
            decision = _decide(context, argv.image, session)
            if decision and decision[0] == mode:
                return CustomRecognition.AnalyzeResult(box=decision[1], detail={"mode": mode, **decision[2]})
            return CustomRecognition.AnalyzeResult(box=None, detail={"mode": mode, "observed": decision[0] if decision else "unknown"})
        except Exception as error:
            return CustomRecognition.AnalyzeResult(box=None, detail={"error": str(error)})


@AgentServer.custom_action("DailyAffairsAction")
class DailyAffairsAction(CustomAction):
    def run(self, context, argv):
        try:
            session = _session(context)
            mode = param_mode(argv.custom_action_param)
            decision = _decide(context, context.tasker.controller.cached_image, session)
            if not decision or decision[0] != mode:
                # All edges except handoff re-enter the observer. A changed
                # frame needs another decision, not an immediate system back.
                now = time.monotonic()
                if session.first_changed_frame is None:
                    session.first_changed_frame = now
                return mode != "handoff" and now - session.first_changed_frame < 20
            session.first_changed_frame = None
            _, box, detail = decision
            session.cached_key = None
            kind = next((item for item in session.selected if item not in session.done), None)
            if mode == "pending_wait":
                return True
            if mode == "handoff":
                session.handed_off = True
                print("[每日政务] 前置流程结束，已回主页，继续原配置的体力关卡。", flush=True)
                return True
            if mode in {"settled", "skip"}:
                reason = detail.get("reason", CONFIRMED)
                if mode == "settled":
                    session.last_sweeps[kind] = date.today()
                    try:
                        _write_sweep_history(session.history_path, session.last_sweeps)
                    except OSError as error:
                        print(f"[每日政务] {LABELS[kind]}的扫荡日期未能写入。{error}", flush=True)
                session.done[kind] = reason
                print(f"[每日政务] {LABELS[kind]}：{reason}。", flush=True)
                session.pending = None
                session.opened = None
                session.return_home = True
                return True
            if mode == "close_sweep" and detail.get("skip") and kind:
                session.done[kind] = detail["skip"]
                print(f"[每日政务] {LABELS[kind]}：{detail['skip']}，跳过本项。", flush=True)
                session.pending = None
                session.return_home = True
            if mode == "submit":
                snapshot = detail["snapshot"]
                session.reserved[kind] = session.reserved.get(kind, 0) + snapshot["count"]
                session.pending = {"snapshot": snapshot, "time": time.monotonic(), "result_seen": False}
                print(
                    f"[每日政务] {LABELS[kind]}：{'单次扫荡，无需选择次数' if detail['single'] else '最大次数' + str(snapshot['count'])}；"
                    f"门票{snapshot['ticket']}→{snapshot['ticket_after']}，"
                    f"扫荡券{snapshot['coupon']}→{snapshot['coupon_after']}，提交扫荡。", flush=True
                )
            if mode == "result_done" and session.pending:
                session.pending["result_seen"] = True
            key = (kind or "Done", mode)
            limit = 6 if mode == "scroll" else 12
            if session.actions.get(key, 0) >= limit:
                return False
            session.actions[key] = session.actions.get(key, 0) + 1
            if mode == "scroll":
                result = context.run_action_direct(
                    JActionType.Swipe, JSwipe(begin=(360, 1080), end=[(360, 420)], duration=[450])
                )
            else:
                x, y, width, height = box
                result = context.run_action_direct(
                    JActionType.Click, JClick(target=(x + width // 2, y + height // 2))
                )
            if result is None or not result.success:
                return False
            if mode == "stage":
                print(f"[每日政务] {LABELS[kind]}：同一行的等级和名称相符，匹配分{detail['score']}，进入详情核对。", flush=True)
            if detail.get("wrong_stage"):
                print(f"[每日政务] 当前详情与{LABELS[kind]}不符，只退回关卡列表重新选行。", flush=True)
            if mode == "open_sweep":
                session.opened = kind
                session.maximized = 0
            elif mode == "maximize":
                session.maximized += 1
                print(f"[每日政务] {LABELS[kind]}：已点击右侧菱形最大次数按钮，重新读取次数及消耗预览。", flush=True)
            elif mode == "close_sweep":
                session.opened = None
            elif mode == "expedition":
                session.return_home = False
            return True
        except Exception as error:
            print(f"[每日政务] 动作失败：{error}；保留本轮资源账目。", flush=True)
            return False
