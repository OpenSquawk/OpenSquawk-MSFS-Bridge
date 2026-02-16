# OpenSquawk MSFS Bridge In-Game Panel

This package replaces the old external Windows bridge with an in-sim HTML/JS bridge that runs as a global MSFS toolbar panel (not tied to any aircraft panel.cfg).

## What is implemented
- Token generation and local persistence (`opensquawk.bridge.token.v1`).
- Login flow URL generation (`/bridge/connect?token=...`).
- Login polling against bridge user endpoint.
- Simulator telemetry sampling from SimVars.
- Active telemetry upload and idle status heartbeats.
- Command parsing from telemetry response and write-back to simulator.
- Deep tracing and diagnostics (structured logs, counters, runtime state panel).

## Package layout
- `manifest.json`
- `layout.json`
- `InGamePanels/maximus-ingamepanels-custom.spb`
- `html_ui/InGamePanels/CustomPanel/CustomPanel.html`
- `html_ui/InGamePanels/CustomPanel/CustomPanel.css`
- `html_ui/InGamePanels/CustomPanel/CustomPanel.js`
- `html_ui/Textures/Menu/toolbar/ICON_TOOLBAR_MAXIMUS_CUSTOM_PANEL.svg`
- `html_ui/Pages/VCockpit/Instruments/OpenSquawkBridge/OpenSquawkBridge.html`
- `html_ui/Pages/VCockpit/Instruments/OpenSquawkBridge/OpenSquawkBridge.css`
- `html_ui/Pages/VCockpit/Instruments/OpenSquawkBridge/bridge_shared.js`
- `html_ui/Pages/VCockpit/Instruments/OpenSquawkBridge/OpenSquawkBridge.js`

## Local tooling
- `npm test` runs logic tests for command parsing/value coercion/payload transforms.
- `npm run update:layout` regenerates `layout.json` and updates `manifest.json.total_package_size`.

## Install into Community folder
1. Copy this project directory into your MSFS `Community` folder.
2. Ensure `layout.json` and `manifest.json` are present at package root.
3. Start MSFS and open the toolbar panel for OpenSquawk Bridge (no aircraft panel reconfiguration required).
4. If the panel is not visible, follow `/docs/AIRCRAFT-INTEGRATION.md` troubleshooting notes.

## Runtime configuration UI
Open the toolbar panel and configure:
- Base URL, user/status/telemetry endpoints.
- Optional bearer auth token.
- Active/idle intervals.
- Request timeout.
- Optional remote debug endpoint.

All config fields are persisted under `opensquawk.bridge.config.v1`.

## First-run validation
1. Confirm a token appears in the UI.
2. Confirm login URL is shown.
3. Trigger `Force Login Poll`.
4. Verify `User` state changes to connected after backend pairing.
5. Verify telemetry counters increase and telemetry posts succeed.

Use `/docs/DEBUG-RUNBOOK.md` for detailed failure diagnosis.
