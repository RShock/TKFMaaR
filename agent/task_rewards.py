"""Reconcile bounded reward requests with the current page, independently of navigation."""

import re
import time
from dataclasses import dataclass, field

from bootstrap import configure_runtime

configure_runtime()

from maa.agent.agent_server import AgentServer
from maa.custom_action import CustomAction
from maa.custom_recognition import CustomRecognition
from maa.pipeline import JActionType, JClick, JLongPress

from common import param_mode, safe_json_loads


DAILY_PAGE_CHECK = "__DailyTaskRewardsDailyPageReady"
TABS = ("Daily", "Weekly", "Monthly", "Personal", "Activity", "Guild")
CLAIM_TABS = {f"DailyTaskRewardsClaim{tab}Available": tab for tab in TABS}
RESPONSE_TIMEOUT = 20
HOME_ENTRY_CHECK = "__DailyTaskRewardsHomeEntry"
HOME_ENTRY_LIMIT = 3
HOME_ENTRY_RETRY_DELAY = 2.5
TAB_LABELS = dict(zip(TABS, ("每日", "每周", "每月", "个人", "活动", "协会")))
PAGE_BOX = (0, 0, 720, 1280)


@dataclass
class ClaimRequest:
    tab: str
    issued_at: float
    claimed_before: int | None
    response: str | None = None
    inventory_seen: bool = False
    observation: tuple | None = None
    observed_at: float = 0


@dataclass
class RewardSession:
    attempts: dict[str, int] = field(default_factory=dict)
    popup_closes: dict[tuple[str, str], int] = field(default_factory=dict)
    inventory_popups: int = 0
    last_daily_response_confirmed: bool = False
    pending: ClaimRequest | None = None
    resume_tab: str = "Daily"
    home_open_attempts: int = 0
    last_home_open_at: float = float("-inf")


_SESSIONS: dict[str, RewardSession] = {}


def _session(context):
    # Initialized only at the outer entry, never by a recovery or verification round.
    return _SESSIONS[context.tasker.controller.uuid]


def _miss(reason, **detail):
    return CustomRecognition.AnalyzeResult(box=None, detail={"reason": reason, **detail})


def _find(context, image, name):
    result = context.run_recognition(name, image)
    return result if result is not None and result.hit and result.box is not None else None


def _claim_limit(context, tab):
    node = context.get_node_data(f"DailyTaskRewardsClaim{tab}Available") or {}
    limit = node.get("max_hit", 3)
    return limit if type(limit) is int and 0 < limit <= 3 and node.get("enabled") is not False else 0


def _claimed_count(context, image):
    result = context.run_recognition("__DailyTaskRewardsClaimedText", image)
    if result is None:
        return None
    return sum(
        re.sub(r"\s+", "", getattr(item, "text", "")) in {"已领取", "已領取"}
        for item in (result.all_results or [])
    )


def _response_evidence(context, image, session):
    """A returned page permits reconciliation, never completion by itself."""
    pending = session.pending
    if pending is None or not _find(context, image, f"__DailyTaskRewards{pending.tab}PageReady"):
        return None
    if pending.response in {"reward", "inventory_full"}:
        return pending.response, None
    claimed = _claimed_count(context, image)
    if pending.claimed_before is not None and claimed is not None and claimed > pending.claimed_before:
        return "visible_progress", claimed
    claimable = context.run_recognition("__DailyTaskRewardsClaimable", image)
    if (
        claimable is not None and not claimable.hit
        and _find(context, image, "__DailyTaskRewardsClaimDisabled")
    ):
        return "page_complete", claimed
    # Claims are idempotent. Only a fully recognized, unobscured same page may
    # release an unacknowledged request after its original deadline. Its attempt
    # stays spent and provides no inventory-limit or completion evidence.
    if (
        time.monotonic() - pending.issued_at >= RESPONSE_TIMEOUT
        and session.attempts.get(pending.tab, 0) < _claim_limit(context, pending.tab)
    ):
        return "unconfirmed_retry", claimed
    return None


@AgentServer.custom_action("DailyTaskRewardsInitialize")
class DailyTaskRewardsInitialize(CustomAction):
    def run(self, context, argv):
        _SESSIONS[context.tasker.controller.uuid] = RewardSession()
        return True


@AgentServer.custom_recognition("DailyTaskRewardsClaimReady")
class DailyTaskRewardsClaimReady(CustomRecognition):
    def analyze(self, context, argv):
        try:
            session = _session(context)
            if session.pending:
                return _miss("waiting_for_claim_response")
            if argv.node_name not in CLAIM_TABS:
                return _miss("unknown_claim_node")
            tab = CLAIM_TABS[argv.node_name]
            limit = _claim_limit(context, tab)
            if session.attempts.get(tab, 0) >= limit:
                return _miss("claim_limit_reached", tab=tab)
            result = context.run_recognition("__" + argv.node_name + "Target", argv.image)
            if result is None or not result.hit:
                return _miss("claim_not_available")
            return CustomRecognition.AnalyzeResult(box=result.box, detail={"tab": CLAIM_TABS[argv.node_name]})
        except Exception as error:
            return _miss("claim_recognition_failed", error=str(error))


@AgentServer.custom_recognition("DailyTaskRewardsInventoryFull")
class DailyTaskRewardsInventoryFull(CustomRecognition):
    def analyze(self, context, argv):
        try:
            text = context.run_recognition("__DailyTaskRewardsInventoryFullText", argv.image)
            if text is None:
                return _miss("inventory_text_unavailable")
            original = " ".join(getattr(item, "text", "") for item in (text.all_results or []))
            joined = re.sub(r"[\W_]+", "", original)
            if not re.search(r"(?:堆叠|堆疊)上限", joined) or not re.search(
                r"可领取|可領取|物品|仓库|倉庫", joined
            ):
                return _miss("inventory_limit_not_found", recognized_text=original)
            button = context.run_recognition("__DailyTaskRewardsPopupConfirmButton", argv.image)
            if button is None or not button.hit:
                return _miss("inventory_confirm_not_found", recognized_text=original)
            return CustomRecognition.AnalyzeResult(
                box=button.box, detail={"kind": "inventory_full", "recognized_text": original}
            )
        except Exception as error:
            return _miss("inventory_popup_recognition_failed", error=str(error))


@AgentServer.custom_action("DailyTaskRewardsClaim")
class DailyTaskRewardsClaim(CustomAction):
    def run(self, context, argv):
        try:
            session = _session(context)
            tab = CLAIM_TABS.get(argv.node_name)
            if tab is None or session.pending:
                return False
            limit = _claim_limit(context, tab)
            used = session.attempts.get(tab, 0)
            if used >= limit:
                return False
            x, y, width, height = (int(value) for value in argv.box)
            if width <= 0 or height <= 0:
                return False
            # Reserve before input. An uncertain response cannot issue a new request.
            image = context.tasker.controller.cached_image
            if image is None:
                return False
            claimed_before = _claimed_count(context, image)
            session.attempts[tab] = used + 1
            session.pending = ClaimRequest(tab, time.monotonic(), claimed_before)
            session.resume_tab = tab
            if tab == "Daily":
                session.last_daily_response_confirmed = False
            point = (x + width // 2, y + height // 2)
            print(
                f"[奖励领取] {TAB_LABELS[tab]}页：检测到可领取及一键领取，点击按钮中心{point}，"
                f"第{used + 1}/{limit}次；等待奖励或仓库上限弹窗，不重复提交。",
                flush=True,
            )
            result = context.run_action_direct(JActionType.Click, JClick(target=point))
            return result is not None and result.success
        except Exception as error:
            print(f"[奖励领取] 领取输入失败，保留本次额度：{error}", flush=True)
            return False


@AgentServer.custom_action("DailyTaskRewardsCloseResponse")
class DailyTaskRewardsCloseResponse(CustomAction):
    def run(self, context, argv):
        try:
            param = argv.custom_action_param
            if isinstance(param, str):
                param = safe_json_loads(param, {})
            if not isinstance(param, dict):
                return False
            kind = param.get("kind")
            if kind not in {"reward", "inventory_full"}:
                return False
            session = _session(context)
            tab = session.pending.tab if session.pending else session.resume_tab
            key = (tab, kind)
            used = session.popup_closes.get(key, 0)
            if used >= 6:
                print(f"[奖励领取] {TAB_LABELS[tab]}页同类弹窗关闭已达6次，不再重复点击。", flush=True)
                return False
            x, y, width, height = (int(value) for value in argv.box)
            if width <= 0 or height <= 0:
                return False
            target = (x + width // 2, y + height // 2)
            session.popup_closes[key] = used + 1
            result = context.run_action_direct(JActionType.Click, JClick(target=tuple(target)))
            if result is None or not result.success:
                return False
            label = "奖励弹窗" if kind == "reward" else "仓库堆叠上限提示"
            print(f"[奖励领取] 检测到{label}，已点击确定按钮中点{tuple(target)}；返回当前页后核对领取结果。", flush=True)
            pending = session.pending
            if pending is not None:
                pending.response = kind
                pending.inventory_seen |= kind == "inventory_full"
                pending.observation = None
            return True
        except Exception as error:
            print(f"[奖励领取] 弹窗处理失败：{error}", flush=True)
            return False


def _home_entry(context, image, session):
    if (
        session.home_open_attempts >= HOME_ENTRY_LIMIT
        or time.monotonic() - session.last_home_open_at < HOME_ENTRY_RETRY_DELAY
    ):
        return None
    return _find(context, image, HOME_ENTRY_CHECK)


@AgentServer.custom_action("DailyTaskRewardsOpenPage")
class DailyTaskRewardsOpenPage(CustomAction):
    def run(self, context, argv):
        try:
            session = _session(context)
            controller = context.tasker.controller
            if not controller.post_screencap().wait().succeeded:
                print("[奖励领取] 打开任务页前取图失败。", flush=True)
                return False
            image = controller.cached_image
            icon = _home_entry(context, image, session) if image is not None else None
            if icon is None:
                # The page may have opened during the native stability wait.
                return True
            x, y, width, height = map(int, icon.box)
            point = (x + width // 2, y + height // 2)
            session.home_open_attempts += 1
            attempt = session.home_open_attempts
            session.last_home_open_at = time.monotonic()
            if session.pending:
                session.resume_tab = session.pending.tab
                session.pending.observation = None
            if attempt == 1:
                result = context.run_action_direct(JActionType.Click, JClick(target=point))
                gesture = "点击"
            else:
                result = context.run_action_direct(JActionType.LongPress, JLongPress(target=point, duration=120))
                gesture = "按住120毫秒"
            sent = bool(result and result.success)
            print(
                f"[奖励领取] 第{attempt}/{HOME_ENTRY_LIMIT}次打开任务页。"
                f"识别到主页任务图标，{gesture}中心{point}，输入{'已发送' if sent else '失败'}。"
                f"等待确认任务页，再核对{TAB_LABELS[session.resume_tab]}页。",
                flush=True,
            )
            return True
        except Exception as error:
            print(f"[奖励领取] 打开任务页失败：{error}", flush=True)
            return False


@AgentServer.custom_recognition("DailyTaskRewardsResponseState")
class DailyTaskRewardsResponseState(CustomRecognition):
    def analyze(self, context, argv):
        mode = None
        try:
            mode = param_mode(argv.custom_recognition_param)
            if mode not in {"pending", "wait", "timeout", "observe", "open", *TABS}:
                return _miss("unknown_response_mode")
            session = _session(context)
            if mode in TABS:
                return CustomRecognition.AnalyzeResult(
                    box=PAGE_BOX if session.resume_tab == mode else None, detail={"resume_tab": session.resume_tab}
                )
            if mode == "open":
                icon = _home_entry(context, argv.image, session)
                return CustomRecognition.AnalyzeResult(
                    box=icon.box if icon else None, detail={"home_open_attempts": session.home_open_attempts}
                )
            pending = session.pending
            if pending is None:
                return _miss("no_pending_claim")
            elapsed = time.monotonic() - pending.issued_at
            if mode == "observe":
                evidence = _response_evidence(context, argv.image, session)
                return CustomRecognition.AnalyzeResult(
                    box=PAGE_BOX if evidence else None,
                    detail={"tab": pending.tab, "evidence": evidence, "elapsed": round(elapsed, 1)},
                )
            hit = mode == "pending" or (elapsed < RESPONSE_TIMEOUT if mode == "wait" else elapsed >= RESPONSE_TIMEOUT)
            detail = {"tab": pending.tab, "response": pending.response, "elapsed": round(elapsed, 1)}
            return CustomRecognition.AnalyzeResult(box=PAGE_BOX if hit else None, detail=detail)
        except Exception as error:
            # Missing bookkeeping is not permission to claim again or finish a page.
            return CustomRecognition.AnalyzeResult(
                box=(0, 0, 720, 1280) if mode in {"pending", "timeout"} else None,
                detail={"reason": "claim_response_state_failed", "error": str(error)},
            )


@AgentServer.custom_action("DailyTaskRewardsReconcile")
class DailyTaskRewardsReconcile(CustomAction):
    def run(self, context, argv):
        try:
            session = _session(context)
            image = context.tasker.controller.cached_image
            if image is None:
                return False
            evidence = _response_evidence(context, image, session)
            pending = session.pending
            if evidence is None or pending is None:
                return False
            now = time.monotonic()
            # Recognition is read-only. Only this explicit action can settle a
            # request, after two page observations at least 500ms apart.
            if pending.observation != evidence:
                pending.observation, pending.observed_at = evidence, now
                return True
            if now - pending.observed_at < 0.5:
                return True
            kind = evidence[0]
            if pending.tab == "Daily":
                session.last_daily_response_confirmed = kind != "unconfirmed_retry"
                if pending.inventory_seen:
                    session.inventory_popups += 1
            labels = {
                "reward": "奖励弹窗已关闭，并两次确认回到当前页",
                "inventory_full": "仓库上限提示已关闭，并两次确认回到当前页",
                "visible_progress": "两次确认列表中已领取条目增加，领取已有实际进展",
                "page_complete": "两次确认领取按钮不可用且无可领取条目",
                "unconfirmed_retry": "20秒未确认响应，但两次确认当前页无遮挡；保留已用次数，重新识别剩余奖励，不记为领取成功",
            }
            print(f"[奖励领取] {TAB_LABELS[pending.tab]}页：{labels[kind]}。", flush=True)
            session.pending = None
            return True
        except Exception as error:
            print(f"[奖励领取] 响应核对失败，保留请求账目：{error}", flush=True)
            return False


@AgentServer.custom_recognition("DailyTaskRewardsInventorySkip")
class DailyTaskRewardsInventorySkip(CustomRecognition):
    def analyze(self, context, argv):
        limit = _claim_limit(context, "Daily")
        if not limit:
            return self._miss("invalid_claim_limit")

        try:
            session = _session(context)
        except KeyError:
            return self._miss("unavailable_reward_session")
        evidence = {
            "attempts": session.attempts.get("Daily", 0),
            "inventory_popups": session.inventory_popups,
            "last_response_confirmed": session.last_daily_response_confirmed,
        }
        if session.pending:
            return self._miss("pending_claim_response", evidence)
        if evidence["attempts"] < limit:
            return self._miss("remaining_attempts", evidence)
        if not evidence["inventory_popups"]:
            return self._miss("no_daily_inventory_limit", evidence)
        if not evidence["last_response_confirmed"]:
            return self._miss("unconfirmed_last_response", evidence)

        page = context.get_node_data(DAILY_PAGE_CHECK)
        if not isinstance(page, dict) or not page or page.get("enabled") is False:
            return self._miss("unavailable_page_check", evidence)
        result = context.run_recognition(DAILY_PAGE_CHECK, argv.image)
        if result is None or not result.hit or result.box is None:
            return self._miss("daily_page_not_ready", evidence)
        return CustomRecognition.AnalyzeResult(
            box=result.box,
            detail={"status": "partial", "reason": "daily_inventory_limit", "limit": limit, **evidence},
        )

    @staticmethod
    def _miss(reason, evidence=None):
        return CustomRecognition.AnalyzeResult(
            box=None,
            detail={"skip": False, "reason": reason, **(evidence or {})},
        )
