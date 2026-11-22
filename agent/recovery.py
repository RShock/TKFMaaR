"""Recognize interruptions and bound login recovery independently of popups."""

import time

from bootstrap import configure_runtime

configure_runtime()

from maa.agent.agent_server import AgentServer
from maa.custom_action import CustomAction
from maa.custom_recognition import CustomRecognition
import numpy as np

from common import param_mode, safe_json_loads


POPUP_NODES = (
    "TkfmRecoveryReturnTitle",
    "TkfmRecoveryAdvertisement",
    "TkfmRecoveryAnnouncement",
    "TkfmRecoveryEventSignIn",
    "TkfmRecoverySignIn",
    "GameCloseSignIn",
    "TkfmRecoveryBackButton",
    "GameLoginBackFallback",
    "GameLoginExistingGiftFallback",
)

_LOGIN_DEADLINES: dict[str, float] = {}
_RESTORED_RECRUITMENT_PENDING: dict[str, dict] = {}
LOGIN_RECOVERY_LIMIT = 2
LOGIN_RECOVERY_SECONDS = 90


def recovered_recruitment_pending(context, pending):
    evidence = _RESTORED_RECRUITMENT_PENDING.get(context.tasker.controller.uuid, {})
    return bool(evidence.get("complete") and evidence.get("pending") == pending)


def clear_recovered_recruitment_pending(context):
    _RESTORED_RECRUITMENT_PENDING.pop(context.tasker.controller.uuid, None)


@AgentServer.custom_recognition("TkfmRecoveryLoginState")
class TkfmRecoveryLoginState(CustomRecognition):
    def analyze(self, context, argv):
        mode = None
        try:
            mode = param_mode(argv.custom_recognition_param)
            used = context.get_hit_count("TkfmRecoveryLoginBegin")
            deadline = _LOGIN_DEADLINES.get(context.tasker.controller.uuid)
            if mode == "available":
                hit = used < LOGIN_RECOVERY_LIMIT
            elif mode == "expired":
                # Missing initialization must fail closed inside a login subtask.
                hit = deadline is None or time.monotonic() >= deadline
            elif mode == "active":
                hit = deadline is not None and time.monotonic() < deadline
            else:
                return CustomRecognition.AnalyzeResult(box=None, detail={"reason": "invalid_login_mode"})
            return CustomRecognition.AnalyzeResult(
                box=(0, 0, 720, 1280) if hit else None,
                detail={"mode": mode, "attempts": used, "limit": LOGIN_RECOVERY_LIMIT,
                        "remaining_seconds": max(0, deadline - time.monotonic()) if deadline else 0},
            )
        except Exception as error:
            return CustomRecognition.AnalyzeResult(
                box=(0, 0, 720, 1280) if mode == "expired" else None,
                detail={"reason": "login_state_error", "error": str(error)},
            )


@AgentServer.custom_action("TkfmRecoveryLoginBookkeeping")
class TkfmRecoveryLoginBookkeeping(CustomAction):
    def run(self, context, argv):
        try:
            mode = param_mode(argv.custom_action_param)
            key = context.tasker.controller.uuid
            if mode == "begin":
                used = context.get_hit_count("TkfmRecoveryLoginBegin")
                if used >= LOGIN_RECOVERY_LIMIT:
                    return False
                _LOGIN_DEADLINES[key] = time.monotonic() + LOGIN_RECOVERY_SECONDS
                _RESTORED_RECRUITMENT_PENDING[key] = {
                    "pending": context.get_anchor("TkfmRecruitmentPending"), "complete": False
                }
                print(f"[异常恢复] 接管登录，第{used + 1}/{LOGIN_RECOVERY_LIMIT}次，"
                      f"本次最多{LOGIN_RECOVERY_SECONDS}秒；保留当前任务及资源账目。", flush=True)
                return True
            if mode == "finish":
                deadline = _LOGIN_DEADLINES.get(key)
                if deadline is None or time.monotonic() >= deadline:
                    return False
                _LOGIN_DEADLINES.pop(key, None)
                evidence = _RESTORED_RECRUITMENT_PENDING.get(key)
                if evidence is not None:
                    evidence["complete"] = True
                print("[异常恢复] 已确认无遮挡主页，登录恢复完成；交回中断任务继续，回主页不代表任务完成。", flush=True)
                return True
            return False
        except Exception as error:
            print(f"[异常恢复] 登录恢复账目处理失败：{error}", flush=True)
            return False


@AgentServer.custom_recognition("TkfmRecoveryWebPageUnavailable")
class TkfmRecoveryWebPageUnavailable(CustomRecognition):
    """Require a solid white square before OCR on the same controller frame."""

    def analyze(self, context, argv):
        try:
            image = argv.image
            if image is None or image.ndim != 3 or image.shape[2] < 3:
                return self._miss("invalid_image")
            size = 64
            if min(image.shape[:2]) < size:
                return self._miss("image_too_small")
            white = np.all(image[:, :, :3] == 255, axis=2)
            if np.count_nonzero(white) < size * size:
                return self._miss("no_solid_white_square")
            # Integral sums check every contiguous square, rather than counting
            # scattered white pixels or checking only a grid of sample points.
            integral = np.pad(white, ((1, 0), (1, 0))).cumsum(axis=0, dtype=np.int32).cumsum(
                axis=1, dtype=np.int32
            )
            squares = (
                integral[size:, size:]
                - integral[:-size, size:]
                - integral[size:, :-size]
                + integral[:-size, :-size]
            ) == size * size
            if not squares.any():
                return self._miss("no_solid_white_square")
            y, x = np.unravel_index(squares.argmax(), squares.shape)
            white_box = [int(x), int(y), size, size]
            result = context.run_recognition("TkfmRecoveryWebPageUnavailableText", image)
            if result is None or not result.hit:
                return self._miss("web_error_text_not_found", white_box)
            return CustomRecognition.AnalyzeResult(
                box=result.box,
                detail={
                    "white_box": white_box,
                    "expected_text": "网页无法打开",
                    "recognized_text": [getattr(item, "text", "") for item in (result.all_results or [])],
                    "recovery": "restart_game",
                },
            )
        except Exception as error:
            return CustomRecognition.AnalyzeResult(
                box=None, detail={"reason": "web_error_recognition_failed", "error": str(error)}
            )

    @staticmethod
    def _miss(reason, white_box=None):
        return CustomRecognition.AnalyzeResult(box=None, detail={"reason": reason, "white_box": white_box})


@AgentServer.custom_recognition("TkfmRecoveryPopup")
class TkfmRecoveryPopup(CustomRecognition):
    def analyze(self, context, argv):
        try:
            param = argv.custom_recognition_param
            if isinstance(param, str):
                param = safe_json_loads(param, {})
            target = param.get("target") if isinstance(param, dict) else None
            if target not in (
                "TkfmRecoveryReturnTitleTarget",
                "TkfmRecoverySessionExpiredTarget",
                "GameCloseGift",
                "GameCloseAnnouncement",
                "GameCloseEventSignIn",
                "TkfmRecoveryMonthlyCardTarget",
                "TkfmRecoveryBackButtonTarget",
            ):
                return self._miss("invalid_popup_target")
            used = sum(context.get_hit_count(name) for name in POPUP_NODES)
            if target == "TkfmRecoveryMonthlyCardTarget" and (
                context.get_hit_count("TkfmRecoverySignIn") + context.get_hit_count("GameCloseSignIn") >= 1
            ):
                return self._miss("monthly_card_already_clicked_once", used)
            if target == "TkfmRecoverySessionExpiredTarget":
                used = context.get_hit_count("TkfmRecoveryLoginBegin")
                if used >= LOGIN_RECOVERY_LIMIT:
                    return self._miss("login_recovery_limit_reached", used)
            elif used >= 2:
                return self._miss("popup_recovery_limit_reached", used)
            # GameCloseGift positively requires the advertisement tabs and its
            # arrow on one frame. A full back plate also appears on real ads,
            # so it cannot distinguish an ad from an ordinary business page.
            result = context.run_recognition(target, argv.image)
            if result is None or not result.hit:
                return self._miss("popup_not_found" if result is not None else "capture_or_recognition_failed", used)
            return CustomRecognition.AnalyzeResult(
                box=result.box,
                detail={"popup": target, "attempt": used + 1, "limit": 2},
            )
        except Exception as error:
            return CustomRecognition.AnalyzeResult(
                box=None, detail={"reason": "popup_recognition_error", "error": str(error)}
            )

    @staticmethod
    def _miss(reason, used=0):
        return CustomRecognition.AnalyzeResult(box=None, detail={"reason": reason, "used": used, "limit": 2})
