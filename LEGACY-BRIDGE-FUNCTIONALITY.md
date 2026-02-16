# Legacy OpenSquawk MSFS Bridge: Complete Functional Spec

This document captures the full behavior of the old desktop bridge so it can be reimplemented as an MSFS instrument/plugin.

## 1. Product Purpose
- Run as a local bridge between Microsoft Flight Simulator and OpenSquawk backend APIs.
- Handle account linking via browser token flow.
- Report simulator status and telemetry to backend.
- Accept control commands from backend response and push them back into the simulator.

## 2. Runtime Components (Old Implementation)
- Windows desktop app (`WinForms`) with one main window.
- `BridgeManager` as app orchestrator.
- `SimConnectAdapter` loaded dynamically from separate assembly.
- Shared contracts for telemetry and simulator control commands.

## 3. Configuration and Persistence

### 3.1 Files
- `bridge-config.json` next to executable.
  - Fields:
    - `token` (string)
    - `createdAt` (ISO timestamp)
- `.env` loaded at startup (if present).

### 3.2 Environment Variables and Defaults
- `BRIDGE_BASE_URL` (default: `https://opensquawk.de`)
- `BRIDGE_ME_URL` (default: `${BRIDGE_BASE_URL}/api/bridge/me`)
- `BRIDGE_STATUS_URL` (default: `${BRIDGE_BASE_URL}/api/bridge/status`)
- `SERVER_URL` (highest priority telemetry endpoint override)
- `BRIDGE_TELEMETRY_URL` (telemetry endpoint override)
- `BRIDGE_DATA_URL` (telemetry endpoint override)
- Telemetry endpoint fallback default: `${BRIDGE_BASE_URL}/api/bridge/data`
- `AUTH_TOKEN` optional bearer token for HTTP requests
- `ACTIVE_INTERVAL_SEC` (default: `30`)
- `IDLE_INTERVAL_SEC` (default: `120`)
- `BRIDGE_IGNORE_SIMCONNECT_LOAD_ERRORS` (`true/1/yes/on/enabled` accepted)

### 3.3 HTTP Headers
- Always set `X-Bridge-Token` when token exists.
- Set `Authorization: Bearer <AUTH_TOKEN>` only when `AUTH_TOKEN` is present.

## 4. Token and Login Flow
- On startup:
  - If no token exists, generate one:
    - 32 random bytes
    - base64url format (`+` -> `-`, `/` -> `_`, no trailing `=`)
  - Save token and `createdAt` to `bridge-config.json`.
  - Open browser to:
    - `${BRIDGE_BASE_URL}/bridge/connect?token=<urlencoded token>`
- Login status polling:
  - Poll every 10 seconds.
  - Call `GET ${ME_URL}?token=<urlencoded token>`.
  - If response is success and not explicitly `{"connected": false}`, treat user as connected.
  - Extract username from `username | userName | name | displayName | email`, also from nested `user` object.

## 5. Connection/State Machine

### 5.1 User State
- If user becomes connected:
  - Start simulator adapter.
- If user becomes disconnected:
  - Stop simulator adapter.

### 5.2 Simulator State Inputs
- Adapter emits:
  - `IsConnected`
  - `IsFlightLoaded`
- Bridge mirrors these into backend status payloads and UI badges.

### 5.3 Timers
- Active timer: one-shot repeating every `ACTIVE_INTERVAL_SEC` while flight active.
- Idle timer: one-shot repeating every `IDLE_INTERVAL_SEC` whenever not in active flight mode.
- On flight activation:
  - Stop idle timer.
  - Start active timer.
  - Send immediate active telemetry tick.
- On flight deactivation:
  - Stop active timer.
  - Start idle timer.
  - Send immediate idle status heartbeat.

## 6. Simulator Adapter Lifecycle (Legacy SimConnect Behavior)
- Adapter loaded dynamically by reflection:
  - Assembly name: `OpensquawkBridge.SimConnectAdapter`
  - Type: `OpensquawkBridge.SimConnectAdapter.SimConnectAdapter`
- Load strategy:
  - First `Assembly.Load(simpleName)`.
  - Fallback `Assembly.LoadFrom(<baseDir>/OpensquawkBridge.SimConnectAdapter.dll)`.
- Load failure classified as SimConnect load issue when:
  - `BadImageFormatException`
  - `DllNotFoundException`
  - `FileLoadException` with HRESULT `0x8007000B`
  - nested inner exception matches above
- If load fails and ignore flag disabled:
  - App remains running but no simulator connection.

## 7. Telemetry Read Set (Legacy Fields)
Collected from simulator once per second (legacy SimConnect implementation), then published as current snapshot.

- `Latitude`
- `Longitude`
- `Altitude` (true altitude)
- `IndicatedAltitude`
- `AirspeedIndicated`
- `AirspeedTrue`
- `GroundVelocity` (m/s)
- `TurbineN1`
- `OnGround`
- `EngineCombustion`
- `TransponderCode`
- `AdfActiveFrequency`
- `AdfStandbyFrequency`
- `VerticalSpeed`
- `PlanePitchDegrees`
- `TurbineN1Engine2`
- `GearHandlePosition`
- `FlapsHandleIndex`
- `BrakeParkingPosition`
- `AutopilotMaster`

## 8. Outbound Backend Calls

### 8.1 Status Heartbeat (`BRIDGE_STATUS_URL`)
JSON payload:
- `token`
- `simConnected`
- `flightActive`

Called:
- periodically in idle mode
- on sim state changes
- before sending telemetry tick

### 8.2 Active Telemetry Tick (`SERVER_URL`/`BRIDGE_TELEMETRY_URL`/`BRIDGE_DATA_URL`)
Preconditions:
- simulator connected
- flight loaded
- token available
- telemetry snapshot available
- snapshot age <= 10s
- latitude in `[-90, 90]`, longitude in `[-180, 180]`

JSON payload fields:
- `token`
- `status` = `"active"`
- `ts` (unix seconds)
- `latitude` (6 decimals)
- `longitude` (6 decimals)
- `altitude_ft_true` (rounded)
- `altitude_ft_indicated` (rounded)
- `ias_kt` (1 decimal)
- `tas_kt` (1 decimal)
- `groundspeed_kt` (1 decimal, from `GroundVelocity * 1.943844`)
- `on_ground`
- `eng_on` (`EngineCombustion || TurbineN1 > 5`)
- `n1_pct` (1 decimal)
- `transponder_code`
- `adf_active_freq`
- `adf_standby_freq_hz` (rounded)
- `vertical_speed_fpm` (rounded)
- `pitch_deg` (1 decimal)
- `n1_pct_2` (1 decimal)
- `gear_handle`
- `flaps_index`
- `parking_brake`
- `autopilot_master`

## 9. Inbound Simulator Control Commands (from Telemetry Response)

### 9.1 Where commands are read from
- Only from successful telemetry POST response body.
- Parse JSON object at root and optionally nested objects under keys:
  - `keys`
  - `commands`
  - `sim`
  - `simvars`
  - `controls`

### 9.2 Accepted command names (case-insensitive, `-` normalized to `_`)
- Transponder:
  - `transponder_code`, `transponder`, `xpdr`, `squawk`
- ADF:
  - `adf_active_freq`, `adf_active_frequency`
  - `adf_standby_freq_hz`, `adf_standby_frequency_hz`, `adf_standby_freq`
- Airframe/systems:
  - `gear_handle`
  - `flaps_index`, `flaps_handle_index`
  - `parking_brake`
  - `autopilot_master`

### 9.3 Parsing rules
- Boolean targets accept:
  - booleans
  - `0/1` numbers
  - strings: `1/0/true/false/yes/no/on/off`
- Integer targets accept:
  - integer numbers
  - whole-number doubles
  - numeric strings
  - booleans (`true=1`, `false=0`)
- `flaps_index` must be >= 0.
- `adf_standby_freq_hz` must be finite and >= 0.
- Invalid values are ignored.

### 9.4 Apply behavior
- Commands are queued.
- If sim not ready (`not connected`, `no flight`, `adapter not registered`), queued commands are dropped.
- Supported write targets:
  - transponder code
  - ADF active frequency
  - ADF standby frequency
  - gear handle (bool threshold: `>=0.5` => `1`)
  - flaps handle index (rounded)
  - parking brake (bool threshold: `>=0.5` => `1`)
  - autopilot master (bool threshold: `>=0.5` => `1`)

## 10. Error Handling and Retries
- Simulator init failure: retry every 2 seconds.
- Simulator receive failure: cleanup and retry after 1 second.
- HTTP timeout: 10 seconds.
- Non-success HTTP response: logged, no crash.
- JSON parse error in telemetry response: ignored.
- App designed to continue running even with simulator unavailable.

## 11. UI Behavior (Legacy Desktop App)
- Main UI elements:
  - connection badge
  - simulator badge
  - flight badge
  - user status label
  - read-only token box
  - log console
  - buttons: open login, reset login, copy token
- Button actions:
  - Open login: launches browser login URL.
  - Reset login:
    - stop simulator
    - generate new token
    - save config
    - update UI state
    - open login URL
  - Copy token: copy to clipboard.
- Log stream shows timestamped runtime events.

## 12. Functional Parity Checklist for MSFS Instrument Rewrite
A new instrument/plugin version is functionally equivalent only if it preserves:
- token generation, persistence, and login URL flow
- login polling and user connection detection
- simulator-connected and flight-active state tracking
- status heartbeat behavior (including timing/state triggers)
- active telemetry payload fields and transformations
- backend-driven simulator command parsing and application rules
- resilience behavior (non-fatal errors, retries, degraded mode)

