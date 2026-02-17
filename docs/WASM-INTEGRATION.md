# OpenSquawk WASM Bridge Integration

This module provides SimConnect-backed read/write access to the legacy telemetry fields and exposes a minimal C ABI for JavaScript/Coherent to call.

## Build
- Requires MSFS SDK SimConnect headers in include path.
- Example CMake usage:

```bash
cmake -S wasm/opensquawk_bridge -B build/wasm -DCMAKE_TOOLCHAIN_FILE=<EMSCRIPTEN_TOOLCHAIN>
cmake --build build/wasm
```

## Exports (C ABI)
- `osb_init()`
- `osb_tick()`
- `osb_is_connected()`
- `osb_get_snapshot_json()`
- `osb_get_snapshot_age_ms()`
- `osb_set_transponder_code(int)`
- `osb_set_adf_active_khz(double)`
- `osb_set_adf_standby_khz(double)`
- `osb_set_gear_handle(int)`
- `osb_set_flaps_index(int)`
- `osb_set_parking_brake(int)`
- `osb_set_autopilot_master(int)`

## Snapshot JSON
The snapshot JSON contains the following fields (all numbers):
`latitude`, `longitude`, `altitude_ft_true`, `altitude_ft_indicated`, `ias_kt`, `tas_kt`, `ground_velocity_mps`, `turbine_n1_pct`, `on_ground`, `engine_combustion`, `transponder_code`, `adf_active_freq_khz`, `adf_standby_freq_khz`, `vertical_speed_fpm`, `pitch_deg`, `turbine_n1_pct_2`, `gear_handle`, `flaps_index`, `parking_brake`, `autopilot_master`.

## JS Host Responsibilities
The JS host is responsible for:
- Token generation & login polling.
- Telemetry POSTs to OpenSquawk.
- Parsing command response and calling WASM setters.

See `html_ui/Pages/VCockpit/Instruments/OpenSquawkBridge/OpenSquawkBridge.js` for the initial implementation.
