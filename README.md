# OpenSquawk MSFS Bridge

A Windows-only companion app that links Microsoft Flight Simulator telemetry with OpenSquawk.
The bridge ships as a WinForms GUI styled like opensquawk.de and delivers simulator state
through the bundled SimConnect adapter when a user is linked to the current machine.

## Repository layout

- `OpensquawkBridge-msfs/` – WinForms UI, bridge orchestration, token management, and the
  executable entry point.
- `OpensquawkBridge.SimConnectAdapter/` – dynamically loaded SimConnect plugin that streams
  telemetry and reports simulator status changes.
- `OpensquawkBridge.Abstractions/` – shared contracts used by the UI and adapter assemblies.
- `.env.example` – template with optional environment variables consumed by the bridge.

## Prerequisites

- Windows 10/11 x64.
- [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) for building or publishing.
- Microsoft Flight Simulator + SimConnect runtime (only required on the machine that will send
  telemetry).

## First-time setup

1. Clone the repository and open a terminal in the project root.
2. Restore dependencies:
   ```powershell
   dotnet restore OpensquawkBridge-msfs.sln
   ```
3. (Optional) copy the environment template to configure runtime overrides:
   ```powershell
   copy .env.example .env   # PowerShell
   # or
   cp .env.example .env     # Command Prompt / Git Bash
   ```
   Available keys are documented inside `.env.example`; for instance,
   `BRIDGE_IGNORE_SIMCONNECT_LOAD_ERRORS=1` keeps the UI running when the native
   SimConnect DLL cannot be loaded.

## Building & running for development

- Build the complete solution:
  ```powershell
  dotnet build OpensquawkBridge-msfs.sln
  ```
- Launch the bridge in debug mode:
  ```powershell
  dotnet run --project OpensquawkBridge-msfs/OpensquawkBridge-msfs.csproj
  ```
  The app will appear with login controls, simulator status badges, and a live log window.

### Runtime behaviour

- On startup the bridge reads `bridge-config.json` from the executable directory. If the
  file is missing, a new token is generated, persisted, and the default browser is opened to
  `https://opensquawk.de/bridge/connect?token=...` for user login.
- The UI polls `/bridge/me` every 10 seconds while the token is unlinked. Once a user logs in,
  the window updates with the account name, starts the SimConnect adapter, and begins sending
  telemetry tagged with the same token.
- The "Open login in browser", "Log out & new token", and "Copy token" buttons let you manage
  the local token from the GUI. Connection, simulator, and flight activity badges mirror
  real-time state updates from the adapter.

## Publishing a release build

Run one of the following from the repository root:

- Produce the default self-contained single-file package defined in the project:
  ```powershell
  dotnet publish OpensquawkBridge-msfs/OpensquawkBridge-msfs.csproj -c Release -r win-x64
  ```
- Publish a framework-dependent build (smaller download) by overriding the project setting:
  ```powershell
  dotnet publish OpensquawkBridge-msfs/OpensquawkBridge-msfs.csproj -c Release -r win-x64 --self-contained false
  ```

The output lands under
`OpensquawkBridge-msfs/bin/Release/net8.0-windows/win-x64/publish/OpensquawkBridge-msfs.exe`
alongside `bridge-config.json` once the app is started. Ship the entire publish folder so the
SimConnect adapter DLL stays next to the executable.

## Troubleshooting

- If `dotnet publish` reports `MSB1009: The project file does not exist`, ensure you either
  invoke the command from inside `OpensquawkBridge-msfs/` or pass the project path as shown
  above.
- When running without Microsoft Flight Simulator installed, enable
  `BRIDGE_IGNORE_SIMCONNECT_LOAD_ERRORS` to suppress native DLL load errors and operate the
  bridge in offline mode.
