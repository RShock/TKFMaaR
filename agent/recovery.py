"""Recognize supported interruptions within a shared, per-task recovery budget."""

from bootstrap import configure_runtime

configure_runtime()

from maa.agent.agent_server import AgentServer
from maa.custom_recognition import CustomRecognition
import numpy as np

from common import safe_json_loads


POPUP_NODES = (
    "TkfmRecoverySessionExpired",
    "TkfmRecoveryAdvertisement",
    "TkfmRecoveryAnnouncement",
    "TkfmRecoveryEventSignIn",
    "TkfmRecoverySignIn",
    "GameCloseSignIn",
    "TkfmRecoveryBackButton",
    "GameLoginBackFallback",
    "GameLoginExistingGiftFallback",
)


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
            if used >= 2:
                return self._miss("popup_recovery_limit_reached", used)
            if target == "GameCloseGift" and param.get("reject_full_back") is True:
                back = context.run_recognition("TkfmRecoveryBackButtonTarget", argv.image)
                if back is None or back.hit:
                    return self._miss("ordinary_back_button_or_check_unavailable", used)
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
