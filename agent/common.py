import json


def safe_json_loads(text, default):
    if not text:
        return default
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        return default


def param_mode(raw):
    param = safe_json_loads(raw, {}) if isinstance(raw, str) else raw
    return param.get("mode") if isinstance(param, dict) else None
