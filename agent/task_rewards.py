"""Keep bounded reward requests pending until their visible response is closed."""

import re
import time
from dataclasses import dataclass, field

from bootstrap import configure_runtime

configure_runtime()

from maa.agent.agent_server import AgentServer
from maa.custom_action import CustomAction
from maa.custom_recognition import CustomRecognition
from maa.pipeline import JActionType, JClick

from common import safe_json_loads


DAILY_CLAIM = "DailyTaskRewardsClaimDailyAvailable"
DAILY_PAGE_CHECK = "__DailyTaskRewardsDailyPageReady"
TABS = ("Daily", "Weekly", "Monthly", "Personal", "Activity", "Guild")
CLAIM_TABS = {f"DailyTaskRewardsClaim{tab}Available": tab for tab in TABS}
RESPONSE_TIMEOUT = 20
TAB_LABELS = dict(zip(TABS, ("每日", "每周", "每月", "个人", "活动", "协会")))


@dataclass
class RewardSession:
    attempts: dict[str, int] = field(default_factory=dict)
    inventory_popups: int = 0
    last_daily_response_confirmed: bool = False
    pending: dict | None = None


_SESSIONS: dict[str, RewardSession] = {}


def _session(context):
    # Initialized only at the outer entry, never by a recovery or verification round.
    return _SESSIONS[context.tasker.controller.uuid]


def _miss(reason, **detail):
    return CustomRecognition.AnalyzeResult(box=None, detail={"reason": reason, **detail})


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
            node = context.get_node_data(argv.node_name) or {}
            limit = node.get("max_hit", 3)
            if type(limit) is not int or not 0 < limit <= 3 or session.attempts.get(tab, 0) >= limit:
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
            node = context.get_node_data(argv.node_name) or {}
            limit = node.get("max_hit", 3)
            used = session.attempts.get(tab, 0)
            if type(limit) is not int or not 0 < limit <= 3 or used >= limit:
                return False
            x, y, width, height = (int(value) for value in argv.box)
            if width <= 0 or height <= 0:
                return False
            # Reserve before input. An uncertain response cannot issue a new request.
            session.attempts[tab] = used + 1
            session.pending = {"tab": tab, "time": time.monotonic(), "response": None}
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
            target, kind = param.get("target"), param.get("kind")
            if kind not in {"reward", "inventory_full", "unknown"}:
                return False
            if target is None:
                x, y, width, height = (int(value) for value in argv.box)
                if width <= 0 or height <= 0:
                    return False
                target = (x + width // 2, y + height // 2)
            if len(target) != 2:
                return False
            result = context.run_action_direct(JActionType.Click, JClick(target=tuple(target)))
            if result is None or not result.success:
                return False
            if kind == "inventory_full":
                print("[奖励领取] 检测到仓库堆叠上限，已点击确定中点；返回当前页后核对领取额度，不进入登录恢复。", flush=True)
            session = _session(context)
            pending = session.pending
            if pending is not None:
                # A generic confirm is not proof that a reward was processed.
                pending["response"] = kind
            elif kind == "unknown":
                session.last_daily_response_confirmed = False
            return True
        except Exception as error:
            print(f"[奖励领取] 弹窗处理失败：{error}", flush=True)
            return False


@AgentServer.custom_recognition("DailyTaskRewardsResponseState")
class DailyTaskRewardsResponseState(CustomRecognition):
    def analyze(self, context, argv):
        mode = None
        try:
            param = argv.custom_recognition_param
            if isinstance(param, str):
                param = safe_json_loads(param, {})
            mode = param.get("mode") if isinstance(param, dict) else None
            if mode not in {"pending", "wait", "timeout"}:
                return _miss("unknown_response_mode")
            session = _session(context)
            pending = session.pending
            if pending is None:
                return _miss("no_pending_claim")
            if mode == "wait" and pending["response"] in {"reward", "inventory_full"}:
                result = context.run_recognition(
                    f"__DailyTaskRewards{pending['tab']}PageReady", argv.image
                )
                if result is not None and result.hit:
                    if pending["tab"] == "Daily":
                        session.last_daily_response_confirmed = True
                        if pending["response"] == "inventory_full":
                            session.inventory_popups += 1
                    kind = "奖励弹窗" if pending["response"] == "reward" else "仓库上限提示"
                    print(
                        f"[奖励领取] {TAB_LABELS[pending['tab']]}页：已关闭{kind}并重新识别到当前页，继续检查剩余奖励。",
                        flush=True,
                    )
                    session.pending = None
                    return _miss("claim_response_confirmed")
            elapsed = time.monotonic() - pending["time"]
            hit = mode == "pending" or (elapsed < RESPONSE_TIMEOUT if mode == "wait" else elapsed >= RESPONSE_TIMEOUT)
            detail = {"tab": pending["tab"], "response": pending["response"], "elapsed": round(elapsed, 1)}
            return CustomRecognition.AnalyzeResult(box=(0, 0, 720, 1280) if hit else None, detail=detail)
        except Exception as error:
            # Missing bookkeeping is not permission to claim again or finish a page.
            return CustomRecognition.AnalyzeResult(
                box=(0, 0, 720, 1280) if mode in {"pending", "timeout"} else None,
                detail={"reason": "claim_response_state_failed", "error": str(error)},
            )


@AgentServer.custom_recognition("DailyTaskRewardsInventorySkip")
class DailyTaskRewardsInventorySkip(CustomRecognition):
    def analyze(self, context, argv):
        claim = context.get_node_data(DAILY_CLAIM)
        limit = claim.get("max_hit") if isinstance(claim, dict) else None
        if (
            not isinstance(claim, dict)
            or claim.get("enabled") is False
            or type(limit) is not int
            or not 0 < limit <= 3
        ):
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
