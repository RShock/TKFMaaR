"""Execute recognition-based clicks at a deterministic center point."""

from bootstrap import configure_runtime

configure_runtime()

from maa.agent.agent_server import AgentServer
from maa.custom_action import CustomAction
from maa.pipeline import JActionType, JClick, JLongPress

from common import safe_json_loads


@AgentServer.custom_action("DailyShopClickCenter")
@AgentServer.custom_action("ClickRecognitionCenter")
class ClickRecognitionCenter(CustomAction):
    def run(self, context, argv):
        try:
            x, y, width, height = (int(value) for value in argv.box)
            if width <= 0 or height <= 0:
                return False
            param = argv.custom_action_param
            if isinstance(param, str):
                param = safe_json_loads(param, {})
            if param is None:
                param = {}
            if not isinstance(param, dict):
                return False
            hold_ms = param.get("hold_ms", 0)
            if type(hold_ms) is not int or not 0 <= hold_ms <= 300:
                return False
            # A two-coordinate target is an exact point, not a random rectangle.
            point = (x + width // 2, y + height // 2)
            result = context.run_action_direct(
                JActionType.LongPress if hold_ms else JActionType.Click,
                JLongPress(target=point, duration=hold_ms) if hold_ms else JClick(target=point),
            )
            return result is not None and result.success
        except Exception as error:
            print(f"[ClickRecognitionCenter] {argv.node_name}: {error}", flush=True)
            return False


@AgentServer.custom_action("CustomStageEnterExpedition")
class CustomStageEnterExpedition(CustomAction):
    def run(self, context, argv):
        try:
            # Only an observed home-entry action can renew per-visit tab navigation.
            # Route/home-entry limits still bound the whole task; resource quotas stay intact.
            if not ClickRecognitionCenter().run(context, argv):
                return False
            node = "CustomStageOpenLimitedActivities"
            if context.get_hit_count(node) > 0 and not context.clear_hit_count(node):
                return False
            print("[自定义关卡] 已点击主页出征中点；本次进入重新允许尝试打开限时活动最多三次，体力、购买和列表滚动额度不变。", flush=True)
            return True
        except Exception as error:
            print(f"[自定义关卡] 进入出征失败：{error}", flush=True)
            return False
