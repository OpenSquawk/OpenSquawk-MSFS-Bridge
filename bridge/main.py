import json
import math
import os
import secrets
import time
import urllib.error
import urllib.parse
import urllib.request
import webbrowser
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from SimConnect import AircraftEvents, AircraftRequests, SimConnect


CONFIG_PATH = Path(__file__).resolve().parent.parent / "bridge-config.json"
HTTP_TIMEOUT_SECONDS = 10
LOGIN_POLL_INTERVAL_SECONDS = 10
TELEMETRY_INTERVAL_SECONDS = 2
SIMCONNECT_RETRY_SECONDS = 2

TRUE_LITERALS = {"1", "true", "yes", "on"}
FALSE_LITERALS = {"0", "false", "no", "off"}

TRANSPONDER_KEYS = {"transponder_code", "transponder", "xpdr", "squawk"}
ADF_ACTIVE_KEYS = {"adf_active_freq", "adf_active_frequency"}
ADF_STANDBY_KEYS = {"adf_standby_freq_hz", "adf_standby_frequency_hz", "adf_standby_freq"}
FLAPS_KEYS = {"flaps_index", "flaps_handle_index"}
AUTO_ACK_MASTER_WARN_DEFAULT = -1

_bridge_token: str | None = None
_sm: SimConnect | None = None
_aq: AircraftRequests | None = None
_ae: AircraftEvents | None = None
_auto_ack_master_warn: float = AUTO_ACK_MASTER_WARN_DEFAULT
_master_caution_seen = False
_master_warning_seen = False
_master_caution_ack_deadline: float | None = None
_master_warning_ack_deadline: float | None = None


def _log_bridge(message: str) -> None:
    timestamp = datetime.now(timezone.utc).isoformat()
    print(f"[{timestamp}] [bridge] {message}", flush=True)


def _bridge_base_url() -> str:
    return os.getenv("BRIDGE_BASE_URL", "https://opensquawk.de").rstrip("/")


def _me_url() -> str:
    return os.getenv("BRIDGE_ME_URL", f"{_bridge_base_url()}/api/bridge/me")


def _telemetry_url() -> str:
    return (
        os.getenv("SERVER_URL")
        or os.getenv("BRIDGE_TELEMETRY_URL")
        or os.getenv("BRIDGE_DATA_URL")
        or f"{_bridge_base_url()}/api/bridge/data"
    )


def _build_headers(token: str | None) -> dict[str, str]:
    headers: dict[str, str] = {"Accept": "application/json"}
    if token:
        headers["X-Bridge-Token"] = token

    auth_token = os.getenv("AUTH_TOKEN")
    if auth_token:
        headers["Authorization"] = f"Bearer {auth_token}"

    return headers


def _generate_token() -> str:
    alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ"
    return "".join(secrets.choice(alphabet) for _ in range(6))


def _is_valid_pairing_code(token: str) -> bool:
    return len(token) == 6 and token.isalpha()


def _load_config() -> dict[str, Any]:
    if not CONFIG_PATH.exists():
        return {}

    try:
        with CONFIG_PATH.open("r", encoding="utf-8") as file:
            payload = json.load(file)
    except (OSError, json.JSONDecodeError):
        return {}

    if not isinstance(payload, dict):
        return {}

    return payload


def _save_config(payload: dict[str, Any]) -> None:
    try:
        with CONFIG_PATH.open("w", encoding="utf-8") as file:
            json.dump(payload, file, indent=2)
    except OSError:
        pass


def _load_or_create_token() -> tuple[str, bool]:
    config = _load_config()
    token = config.get("token")
    if isinstance(token, str):
        token = token.strip()
        if _is_valid_pairing_code(token):
            return token, False

    token = _generate_token()
    config["token"] = token
    config["createdAt"] = datetime.now(timezone.utc).isoformat()
    _save_config(config)
    return token, True


def _request_json(
    method: str,
    url: str,
    headers: dict[str, str],
    payload: dict[str, Any] | None = None,
) -> tuple[int, Any]:
    body = None
    request_headers = dict(headers)

    if payload is not None:
        request_headers["Content-Type"] = "application/json"
        body = json.dumps(payload).encode("utf-8")

    request = urllib.request.Request(url=url, data=body, headers=request_headers, method=method)

    with urllib.request.urlopen(request, timeout=HTTP_TIMEOUT_SECONDS) as response:
        raw = response.read().decode("utf-8").strip()
        if not raw:
            return response.status, None
        try:
            return response.status, json.loads(raw)
        except json.JSONDecodeError:
            return response.status, None


def _log_telemetry_send(url: str, payload: dict[str, Any]) -> None:
    latitude = payload.get("latitude")
    longitude = payload.get("longitude")
    _log_bridge(f"telemetry_send url={url} lat={latitude} lon={longitude}")


def _extract_username(payload: Any) -> str | None:
    if not isinstance(payload, dict):
        return None

    for key in ("username", "userName", "name", "displayName", "email"):
        value = payload.get(key)
        if isinstance(value, str) and value.strip():
            return value.strip()

    user_payload = payload.get("user")
    if isinstance(user_payload, dict):
        for key in ("username", "userName", "name", "displayName", "email"):
            value = user_payload.get(key)
            if isinstance(value, str) and value.strip():
                return value.strip()

    return None


def _ensure_simconnect() -> bool:
    global _sm, _aq, _ae

    if _sm is not None and _aq is not None and _ae is not None:
        return True

    try:
        _sm = SimConnect()
        _aq = AircraftRequests(_sm, _time=2000)
        _ae = AircraftEvents(_sm)
        return True
    except Exception:
        _sm = None
        _aq = None
        _ae = None
        return False


def _to_float(value: Any) -> float | None:
    if isinstance(value, bool):
        return float(int(value))

    if isinstance(value, (int, float)):
        if math.isfinite(float(value)):
            return float(value)
        return None

    if isinstance(value, str):
        text = value.strip()
        if not text:
            return None
        try:
            parsed = float(text)
        except ValueError:
            return None
        if not math.isfinite(parsed):
            return None
        return parsed

    return None


def _to_int(value: Any) -> int | None:
    if isinstance(value, bool):
        return int(value)

    if isinstance(value, int):
        return value

    if isinstance(value, float):
        if math.isfinite(value) and value.is_integer():
            return int(value)
        return None

    if isinstance(value, str):
        text = value.strip()
        if not text:
            return None
        try:
            return int(text, 10)
        except ValueError:
            try:
                parsed = float(text)
            except ValueError:
                return None
            if math.isfinite(parsed) and parsed.is_integer():
                return int(parsed)

    return None


def _to_toggle(value: Any) -> bool | None:
    if isinstance(value, bool):
        return value

    number = _to_float(value)
    if number is not None:
        return number >= 0.5

    if isinstance(value, str):
        text = value.strip().lower()
        if text in TRUE_LITERALS:
            return True
        if text in FALSE_LITERALS:
            return False

    return None


def _sim_ready_for_commands() -> bool:
    if _sm is None or _aq is None or _ae is None:
        return False

    return bool(getattr(_sm, "ok", False)) and bool(getattr(_sm, "running", False))


def _force_set_simvar(key: str, value: float) -> bool:
    if _sm is None or _aq is None:
        return False

    request = _aq.find(key)
    if request is None:
        return False

    try:
        if not request._deff_test():
            return False
        request.outData = value
        return bool(_sm.set_data(request))
    except Exception:
        return False


def _send_event(name: str, value: int | None = None) -> bool:
    if _ae is None:
        return False

    event = _ae.find(name)
    if event is None:
        return False

    try:
        if value is None:
            event()
        else:
            event(int(value))
        return True
    except Exception:
        return False


def _reset_auto_ack_master_warn_state() -> None:
    global _master_caution_seen, _master_warning_seen, _master_caution_ack_deadline, _master_warning_ack_deadline
    _master_caution_seen = False
    _master_warning_seen = False
    _master_caution_ack_deadline = None
    _master_warning_ack_deadline = None
    _log_bridge("auto_ack_master_warn state_reset=true")


def _read_master_alerts() -> tuple[bool | None, bool | None]:
    if _aq is None:
        return None, None

    caution = _to_float(_aq.get("MASTER_CAUTION"))
    warning = _to_float(_aq.get("MASTER_WARNING"))
    caution_active = None if caution is None else caution >= 0.5
    warning_active = None if warning is None else warning >= 0.5
    _log_bridge("caut/warn: " + caution_active + "/" + warning_active)
    return caution_active, warning_active


def _ack_master_caution() -> None:
    if _send_event("MASTER_CAUTION_ACKNOWLEDGE"):
        _log_bridge("auto_ack_master_warn ack=master_caution")


def _ack_master_warning() -> None:
    if _send_event("MASTER_WARNING_ACKNOWLEDGE"):
        _log_bridge("auto_ack_master_warn ack=master_warning")


def _tick_auto_ack_master_warn() -> None:
    global _master_caution_seen, _master_warning_seen, _master_caution_ack_deadline, _master_warning_ack_deadline

    if _auto_ack_master_warn < 0:
        return

    if not _sim_ready_for_commands():
        return

    caution_active, warning_active = _read_master_alerts()
    now = time.time()

    if caution_active is True:
        if not _master_caution_seen:
            if _auto_ack_master_warn <= 0:
                _ack_master_caution()
            else:
                _master_caution_ack_deadline = now + _auto_ack_master_warn
                _log_bridge(
                    f"auto_ack_master_warn schedule=master_caution delay_sec={_auto_ack_master_warn}"
                )
            _master_caution_seen = True
    elif caution_active is False:
        _master_caution_seen = False
        _master_caution_ack_deadline = None

    if warning_active is True:
        if not _master_warning_seen:
            if _auto_ack_master_warn <= 0:
                _ack_master_warning()
            else:
                _master_warning_ack_deadline = now + _auto_ack_master_warn
                _log_bridge(
                    f"auto_ack_master_warn schedule=master_warning delay_sec={_auto_ack_master_warn}"
                )
            _master_warning_seen = True
    elif warning_active is False:
        _master_warning_seen = False
        _master_warning_ack_deadline = None

    if _master_caution_ack_deadline is not None and now >= _master_caution_ack_deadline:
        if caution_active is True:
            _ack_master_caution()
        _master_caution_ack_deadline = None

    if _master_warning_ack_deadline is not None and now >= _master_warning_ack_deadline:
        if warning_active is True:
            _ack_master_warning()
        _master_warning_ack_deadline = None


def _encode_transponder_bcd(code: int) -> int | None:
    if code < 0:
        return None

    digits = f"{code:04d}"
    if len(digits) != 4:
        return None

    if any(char not in "01234567" for char in digits):
        return None

    result = 0
    for char in digits:
        result = (result << 4) | int(char)
    return result


def _encode_decimal_bcd(value: int) -> int | None:
    if value < 0:
        return None

    text = str(value)
    if not text.isdigit():
        return None

    result = 0
    for char in text:
        result = (result << 4) | int(char)
    return result


def _normalize_key(key: Any) -> str:
    return str(key).strip().lower().replace("-", "_")


def _collect_commands(payload: dict[str, Any]) -> dict[str, Any]:
    sources: list[dict[str, Any]] = [payload]
    for nested_key in ("keys", "commands", "sim", "simvars", "controls"):
        nested_payload = payload.get(nested_key)
        if isinstance(nested_payload, dict):
            sources.append(nested_payload)

    commands: dict[str, Any] = {}
    for source in sources:
        for key, value in source.items():
            commands[_normalize_key(key)] = value

    return commands


def _build_telemetry_payload(token: str) -> dict[str, Any] | None:
    if _sm is None or _aq is None:
        _log_bridge("build_telemetry_skip reason=simconnect_objects_missing")
        return None

    sim_ok = bool(getattr(_sm, "ok", False))
    sim_running = bool(getattr(_sm, "running", False))
    if not sim_ok:
        _log_bridge(
            f"build_telemetry_skip reason=sim_not_ready sim_ok={sim_ok} sim_running={sim_running}"
        )
        return None

    latitude = _to_float(_aq.get("PLANE_LATITUDE"))
    longitude = _to_float(_aq.get("PLANE_LONGITUDE"))
    if latitude is None or longitude is None:
        _log_bridge(
            f"build_telemetry_skip reason=missing_coordinates lat={latitude} lon={longitude}"
        )
        return None

    if latitude < -90 or latitude > 90 or longitude < -180 or longitude > 180:
        _log_bridge(
            f"build_telemetry_skip reason=invalid_coordinates lat={latitude} lon={longitude}"
        )
        return None

    altitude_true = _to_float(_aq.get("PLANE_ALTITUDE")) or 0.0
    altitude_indicated = _to_float(_aq.get("INDICATED_ALTITUDE")) or 0.0
    ias = _to_float(_aq.get("AIRSPEED_INDICATED")) or 0.0
    tas = _to_float(_aq.get("AIRSPEED_TRUE")) or 0.0
    ground_velocity = _to_float(_aq.get("GROUND_VELOCITY")) or 0.0
    on_ground = (_to_float(_aq.get("SIM_ON_GROUND")) or 0.0) >= 0.5

    engine_combustion = _to_float(_aq.get("ENG_COMBUSTION"))
    if engine_combustion is None:
        engine_combustion = _to_float(_aq.get("GENERAL_ENG_COMBUSTION:1")) or 0.0

    n1_1 = _to_float(_aq.get("TURB_ENG_N1:1")) or 0.0
    n1_2 = _to_float(_aq.get("TURB_ENG_N1:2")) or 0.0

    transponder_code = _to_int(_aq.get("TRANSPONDER_CODE:1")) or 0
    adf_active = _to_int(_aq.get("ADF_ACTIVE_FREQUENCY:1")) or 0
    adf_standby = _to_float(_aq.get("ADF_STANDBY_FREQUENCY:1")) or 0.0
    vertical_speed = _to_float(_aq.get("VERTICAL_SPEED")) or 0.0

    pitch_rad = _to_float(_aq.get("PLANE_PITCH_DEGREES")) or 0.0
    pitch_deg = -math.degrees(pitch_rad)

    gear_handle = (_to_float(_aq.get("GEAR_HANDLE_POSITION")) or 0.0) >= 0.5
    flaps_index = _to_float(_aq.get("FLAPS_HANDLE_INDEX")) or 0.0
    parking_brake = (_to_float(_aq.get("BRAKE_PARKING_POSITION")) or 0.0) >= 0.5
    autopilot_master = (_to_float(_aq.get("AUTOPILOT_MASTER")) or 0.0) >= 0.5

    payload: dict[str, Any] = {
        "token": token,
        "status": "active",
        "ts": int(time.time()),
        "latitude": round(latitude, 6),
        "longitude": round(longitude, 6),
        "altitude_ft_true": int(round(altitude_true)),
        "altitude_ft_indicated": int(round(altitude_indicated)),
        "ias_kt": round(ias, 1),
        "tas_kt": round(tas, 1),
        "groundspeed_kt": round(ground_velocity * 1.943844, 1),
        "on_ground": on_ground,
        "eng_on": (engine_combustion >= 0.5) or (n1_1 > 5.0),
        "n1_pct": round(n1_1, 1),
        "transponder_code": transponder_code,
        "adf_active_freq": adf_active,
        "adf_standby_freq_hz": int(round(adf_standby)),
        "vertical_speed_fpm": int(round(vertical_speed)),
        "pitch_deg": round(pitch_deg, 1),
        "n1_pct_2": round(n1_2, 1),
        "gear_handle": gear_handle,
        "flaps_index": int(round(flaps_index)),
        "parking_brake": parking_brake,
        "autopilot_master": autopilot_master,
    }

    return payload


def register():
    global _bridge_token

    token, is_new_token = _load_or_create_token()
    _bridge_token = token
    _log_bridge(f"register_start me_url={_me_url()} token={token}")

    if is_new_token:
        _log_bridge("register_token_created new_token=true")
        login_url = f"{_bridge_base_url()}/bridge/connect?token={urllib.parse.quote(token, safe='')}"
        try:
            webbrowser.open(login_url, new=2)
            _log_bridge(f"register_login_opened url={login_url}")
        except Exception:
            _log_bridge("register_login_open_failed")

    me_url = _me_url()
    attempt = 0
    while True:
        attempt += 1
        try:
            url = f"{me_url}?token={urllib.parse.quote(token, safe='')}"
            status, payload = _request_json("GET", url, _build_headers(token))
            if 200 <= status < 300 and not (isinstance(payload, dict) and payload.get("connected") is False):
                _ensure_simconnect()
                username = _extract_username(payload)
                _log_bridge(f"register_success status={status} attempt={attempt} username={username}")
                return {"token": token, "username": username}
            _log_bridge(
                f"register_pending status={status} attempt={attempt} retry_in_sec={LOGIN_POLL_INTERVAL_SECONDS}"
            )
        except (urllib.error.HTTPError, urllib.error.URLError, TimeoutError):
            _log_bridge(
                f"register_error attempt={attempt} retry_in_sec={LOGIN_POLL_INTERVAL_SECONDS}"
            )
        except Exception as exc:
            _log_bridge(
                f"register_exception attempt={attempt} error={type(exc).__name__} retry_in_sec={LOGIN_POLL_INTERVAL_SECONDS}"
            )

        time.sleep(LOGIN_POLL_INTERVAL_SECONDS)


def send_telemetry() -> int:
    global _bridge_token, _sm, _aq, _ae
    _log_bridge("telemetry tick!")

    if not _bridge_token:
        _log_bridge("send_telemetry_no_token register_call=true")
        register()

    token = _bridge_token
    if not token:
        _log_bridge("send_telemetry_skip reason=no_token")
        return SIMCONNECT_RETRY_SECONDS

    if not _ensure_simconnect():
        _log_bridge(f"send_telemetry_skip reason=simconnect_not_ready retry_in_sec={SIMCONNECT_RETRY_SECONDS}")
        return SIMCONNECT_RETRY_SECONDS

    _tick_auto_ack_master_warn()

    try:
        payload = _build_telemetry_payload(token)
        _log_bridge(payload)
    except Exception:
        _sm = None
        _aq = None
        _ae = None
        _log_bridge(f"send_telemetry_error reason=payload_build_failed retry_in_sec={SIMCONNECT_RETRY_SECONDS}")
        return SIMCONNECT_RETRY_SECONDS

    if payload is not None:
        telemetry_url = _telemetry_url()
        _log_telemetry_send(telemetry_url, payload)
        try:
            status, response_payload = _request_json(
                "POST",
                telemetry_url,
                _build_headers(token),
                payload=payload,
            )
            if 200 <= status < 300 and isinstance(response_payload, dict):
                set_values(response_payload)
        except (urllib.error.HTTPError, urllib.error.URLError, TimeoutError):
            _log_bridge("send_telemetry_error reason=request_failed")
        except Exception as exc:
            _log_bridge(f"send_telemetry_exception error={type(exc).__name__}")
    else:
        _log_bridge("telemetry payload is none")
    return TELEMETRY_INTERVAL_SECONDS


def telemetry_loop():
    _log_bridge(f"telemetry_loop_start interval_sec={TELEMETRY_INTERVAL_SECONDS}")
    while True:
        delay = send_telemetry()
        if not isinstance(delay, (int, float)) or delay < 0:
            delay = TELEMETRY_INTERVAL_SECONDS
            _log_bridge(f"telemetry_loop_delay_invalid fallback_sec={delay}")
        time.sleep(delay)


def set_values(payload):
    global _auto_ack_master_warn
    if not isinstance(payload, dict):
        return

    commands = _collect_commands(payload)

    for key, value in commands.items():
        if key == "auto_ack_master_warn":
            parsed = _to_float(value)
            if parsed is None:
                continue
            if parsed < 0:
                parsed = -1
            _auto_ack_master_warn = parsed
            _log_bridge(f"auto_ack_master_warn set value={_auto_ack_master_warn}")
            _reset_auto_ack_master_warn_state()
            continue

    if not _sim_ready_for_commands():
        return

    for key, value in commands.items():
        if key == "auto_ack_master_warn":
            continue

        if key in TRANSPONDER_KEYS:
            parsed = _to_int(value)
            if parsed is None:
                continue
            if _force_set_simvar("TRANSPONDER_CODE:1", float(parsed)):
                continue
            bcd_value = _encode_transponder_bcd(parsed)
            if bcd_value is not None:
                _send_event("XPNDR_SET", bcd_value)
            continue

        if key in ADF_ACTIVE_KEYS:
            parsed = _to_int(value)
            if parsed is None or parsed < 0:
                continue
            if _force_set_simvar("ADF_ACTIVE_FREQUENCY:1", float(parsed)):
                continue
            bcd_value = _encode_decimal_bcd(parsed)
            if bcd_value is not None:
                _send_event("ADF_COMPLETE_SET", bcd_value)
            continue

        if key in ADF_STANDBY_KEYS:
            parsed = _to_float(value)
            if parsed is None or parsed < 0:
                continue
            _force_set_simvar("ADF_STANDBY_FREQUENCY:1", float(round(parsed)))
            continue

        if key == "gear_handle":
            parsed = _to_toggle(value)
            if parsed is None:
                continue
            numeric = 1 if parsed else 0
            if not _force_set_simvar("GEAR_HANDLE_POSITION", float(numeric)):
                _send_event("GEAR_SET", numeric)
            continue

        if key in FLAPS_KEYS:
            parsed = _to_float(value)
            if parsed is None:
                continue
            flaps_index = int(round(parsed))
            if flaps_index < 0:
                continue
            if not _force_set_simvar("FLAPS_HANDLE_INDEX", float(flaps_index)):
                _send_event("FLAPS_SET", max(0, min(flaps_index, 16383)))
            continue

        if key == "parking_brake":
            parsed = _to_toggle(value)
            if parsed is None:
                continue
            numeric = 1 if parsed else 0
            _force_set_simvar("BRAKE_PARKING_POSITION", float(numeric))
            continue

        if key == "autopilot_master":
            parsed = _to_toggle(value)
            if parsed is None:
                continue
            numeric = 1 if parsed else 0
            if not _force_set_simvar("AUTOPILOT_MASTER", float(numeric)):
                _send_event("AUTOPILOT_ON" if parsed else "AUTOPILOT_OFF")
