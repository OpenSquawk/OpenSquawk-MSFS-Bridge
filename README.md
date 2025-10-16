# OpenSquawk MSFS Bridge

Windows bridge that links Microsoft Flight Simulator with the OpenSquawk backend. On first launch the app creates a login token, opens the browser for authentication and then streams simulator telemetry once the account is paired.

## Requirements
- Windows 10/11 with the .NET 8 SDK for building (runtime is bundled in publish output).
- Git LFS so the bundled SimConnect binaries download correctly.
- Optional: Microsoft Flight Simulator if you want to exercise the SimConnect integration locally.

## Build & publish
Use the helper script to restore, build and publish the bridge:

```powershell
pwsh ./tools/Publish-Bridge.ps1 [-OpenExplorer] [-RunExecutable]
```

The script performs `dotnet restore`, `dotnet build` and `dotnet publish` (`-c Release -r win-x64 --self-contained true`). It then copies the DLLs from `libs/` into `OpensquawkBridge-msfs/bin/Release/net8.0-windows/win-x64`, keeps `OpensquawkBridge-msfs.exe` in the publish folder root and moves all other dependencies into `publish/deps`. Use `-OpenExplorer` to open the publish folder in Windows Explorer and `-RunExecutable` to launch the freshly built executable.

## Run locally
- Development: `dotnet run --project OpensquawkBridge-msfs/OpensquawkBridge-msfs.csproj`
- Published build: launch `OpensquawkBridge-msfs.exe` from the publish output (dependencies live in the `deps` subfolder and must stay beside the executable).

## Configuration
- `.env` (auto-created from `.env.example` on first build) controls runtime overrides such as `BRIDGE_IGNORE_SIMCONNECT_LOAD_ERRORS`.
- `bridge-config.json` stores the generated token next to the executable—keep it when moving the publish folder.
