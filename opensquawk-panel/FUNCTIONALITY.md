# OpenSquawk Bridge – MSFS InGamePanel

## Overview

OpenSquawk Bridge is an MSFS Community Package that provides a **toolbar panel** (InGamePanel) for connecting Microsoft Flight Simulator to the OpenSquawk network. It runs independently of any aircraft – just drop it in the Community folder and it appears in the MSFS toolbar.

## Architecture

- **Type**: MSFS InGamePanel (toolbar panel, like VFR Map or ATC)
- **Tech**: HTML / CSS / JavaScript using MSFS Coherent GT runtime
- **SimVar Access**: `SimVar.GetSimVarValue()` / `SimVar.SetSimVarValue()`
- **Backend Communication**: `fetch()` REST calls to OpenSquawk API
- **No external dependencies**: No .NET, no SimConnect SDK, no separate app

## Features

### 1. Token-Based Authentication
- Generates a 6-character alphanumeric token on first use
- Token stored in MSFS `localStorage` (persists across sessions)
- User visits `opensquawk.de/bridge/connect?token=XXXXXX` to link account
- Panel polls `GET /api/bridge/me` to detect when user has connected
- Reset token button to re-generate and unlink

### 2. Real-Time Telemetry Collection
Reads the following SimVars every tick and sends to backend every 30 seconds:

| SimVar | Unit | Description |
|--------|------|-------------|
| PLANE LATITUDE | degrees | Position |
| PLANE LONGITUDE | degrees | Position |
| PLANE ALTITUDE | feet | Altitude MSL |
| INDICATED ALTITUDE | feet | Altitude indicated |
| AIRSPEED INDICATED | knots | IAS |
| AIRSPEED TRUE | knots | TAS |
| GROUND VELOCITY | knots | Ground speed |
| VERTICAL SPEED | feet per minute | VS |
| PLANE PITCH DEGREES | degrees | Pitch |
| TURB ENG N1:1 | percent | Engine 1 N1 |
| TURB ENG N1:2 | percent | Engine 2 N1 |
| GENERAL ENG COMBUSTION:1 | bool | Engine running |
| SIM ON GROUND | bool | On ground |
| TRANSPONDER CODE:1 | bco16 | Squawk code |
| GEAR HANDLE POSITION | bool | Gear |
| FLAPS HANDLE INDEX | number | Flaps position |
| BRAKE PARKING POSITION | bool | Parking brake |
| AUTOPILOT MASTER | bool | AP master |
| ADF ACTIVE FREQUENCY:1 | Hz | ADF active |
| ADF STANDBY FREQUENCY:1 | Hz | ADF standby |

### 3. Command Reception & Execution
Receives commands from the backend in the `/api/bridge/data` response and applies them:

| Command | SimVar Written | Description |
|---------|---------------|-------------|
| parking_brake | BRAKE PARKING POSITION | Set parking brake |
| gear_handle | GEAR HANDLE POSITION | Extend/retract gear |
| transponder_code | TRANSPONDER CODE:1 | Set squawk code |
| flaps_index | FLAPS HANDLE INDEX | Set flaps position |
| autopilot_master | AUTOPILOT MASTER | Toggle AP |

### 4. Status Display
- **Auth badge**: Shows connected user or "Not linked"
- **Sim badge**: Shows if SimVars are readable
- **Flight badge**: Shows if a flight is active (engine on, not paused)
- **User name**: Displays linked username

### 5. Debug Mode
- Toggle via small button in the panel header
- Shows scrollable log viewer with timestamped entries
- Logs all API calls, SimVar reads, command applications, errors
- Shows raw telemetry JSON
- Hidden by default for a clean UI

### 6. Idle Heartbeat
- When no flight is active, sends status heartbeat every 120 seconds
- Keeps connection alive on the backend

## API Endpoints Used

| Endpoint | Method | Purpose |
|----------|--------|---------|
| /api/bridge/me | GET | Check auth status, get user info |
| /api/bridge/data | POST | Send telemetry, receive commands |
| /api/bridge/status | POST | Update sim/flight connection state |

## Package Structure

```
opensquawk-panel/           → copy this to MSFS Community folder
├── manifest.json           → MSFS package manifest
├── layout.json             → file listing for MSFS
├── html_ui/
│   ├── InGamePanels/
│   │   └── OpenSquawkPanel/
│   │       ├── OpenSquawkPanel.html
│   │       ├── OpenSquawkPanel.js
│   │       └── OpenSquawkPanel.css
│   └── Textures/Menu/toolbar/
│       └── ICON_TOOLBAR_OPENSQUAWK.svg
└── FUNCTIONALITY.md
```

## Installation

1. Copy `opensquawk-panel` folder to MSFS Community folder
2. Start MSFS
3. Click the OpenSquawk icon in the toolbar
4. Copy the 6-char token shown in the panel
5. Visit the login URL in your browser to link your account
6. Fly – telemetry streams automatically
