# Debug Runbook

This instrument is intentionally verbose. Use this runbook to identify where and why it fails.

## 1. Startup sanity checks
Look for log events in this order:
1. `runtime.boot`
2. `storage.backend.selected`
3. `token.loaded` or `token.generated`
4. `login.poll...`
5. `telemetry.read.ok` or `telemetry.read.failed`

If `runtime.boot` exists but telemetry never appears, SimVar API is likely unavailable in current panel context.

## 2. Login problems
Symptoms:
- User stays `disconnected`.

Look for:
- `http.request.start` with `url=<me endpoint>`
- `login.poll.failed` (network issue)
- `login.poll...` with non-success status (endpoint mismatch/auth failure)

Checks:
- Confirm endpoint values in UI.
- Confirm token in query string is the same as shown in token field.
- Confirm backend accepts your optional bearer token if configured.

## 3. Telemetry not sending
Symptoms:
- `telemetry_post_skipped` grows.

Look for gating logs:
- `telemetry.post.gated` with reasons:
  - disconnected user/sim/flight
  - missing telemetry
  - stale telemetry
  - invalid coordinates

Checks:
- Ensure user is connected first.
- Ensure telemetry reads are successful.
- Ensure lat/lon values are finite and in range.

## 4. Telemetry endpoint failures
Symptoms:
- `telemetry_post_failures` grows.

Look for:
- `telemetry.post.failed` (network/timeout)
- `telemetry.post.non_success` (HTTP 4xx/5xx)

Checks:
- Verify endpoint URL and auth.
- Verify backend accepts payload shape.
- Review `payloadPreview` in `telemetry.post.prepare`.

## 5. Command application failures
Symptoms:
- `command_failed` grows.

Look for:
- `command.parse.found` confirms parsing happened.
- `command.apply.failed` contains exact command and all attempted SimVar writes.

Checks:
- Ensure `Apply simulator commands` toggle is enabled.
- Confirm command keys match accepted aliases.
- Confirm command values match expected type/range.

## 6. Remote debug sink
If remote debug upload is configured:
- enable `remoteDebugEnabled`
- set `remoteDebugUrl`

Expected logs:
- `debug.push.start`
- `debug.push.success` or `debug.push.failed`

If remote debug fails, use `Export Logs JSON` and inspect console output.

## 7. High-signal counters
Watch these first:
- `telemetry_read_failures`
- `login_poll_failures`
- `status_post_failures`
- `telemetry_post_failures`
- `telemetry_post_skipped`
- `command_failed`

A growing counter with a stable matching event name in logs gives the fastest root-cause path.
