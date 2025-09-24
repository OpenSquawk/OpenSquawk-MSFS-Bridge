# OpenSquawk MSFS Bridge

This repository contains the Windows bridge application that connects Microsoft Flight Simulator with the OpenSquawk backend. The bridge creates a token on first launch, lets the pilot log in via opensquawk.de, and streams simulator telemetry once the account is linked.

## Prerequisites

- Windows 10/11 with the Desktop runtime.
- [.NET 8.0 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) (required for building, running, and publishing).
- Optional: Microsoft Flight Simulator (MSFS) with the `Microsoft.FlightSimulator.SimConnect.dll` runtime if you plan to connect to the simulator on the same machine.

## One-time setup




0. `choco install dotnet-sdk --version=8.0.100`
1. Clone the repository.
2. Copy `.env.example` to `.env` and adjust any variables you need. The defaults point to the production OpenSquawk endpoints. Set `BRIDGE_IGNORE_SIMCONNECT_LOAD_ERRORS=true` if you want the GUI to launch even when the SimConnect DLL cannot be loaded.
3. No manual token creation is required. On first launch the bridge will create `bridge-config.json` next to the executable, open `https://opensquawk.de/bridge/connect?token=...` in your browser, and keep polling the backend until the login completes.

## Building

From the repository root run:

```powershell
# Restore and build all projects in the solution
 dotnet build OpensquawkBridge-msfs.sln
```

You can also build the main project directly:

```powershell
 dotnet build OpensquawkBridge-msfs/OpensquawkBridge-msfs.csproj
```

Both commands emit debug binaries to `OpensquawkBridge-msfs/bin/Debug/net8.0-windows/win-x64/`.

## Running the bridge locally

Use `dotnet run` from the repository root or start the compiled executable from the build output folder:

```powershell
# Run with the dotnet CLI
 dotnet run --project OpensquawkBridge-msfs/OpensquawkBridge-msfs.csproj

# Or launch the compiled binary (after a build or publish)
 .\OpensquawkBridge-msfs\bin\Debug\net8.0-windows\win-x64\OpensquawkBridge-msfs.exe
```

The bridge window shows:

- Login / logout controls and the connected OpenSquawk user (fetched via `/bridge/me`).
- SimConnect connection state with two stages (sim detected, active flight).
- Recent log output.

If SimConnect fails to load with a *BadImageFormatException*, set `BRIDGE_IGNORE_SIMCONNECT_LOAD_ERRORS=true` in your `.env` file to keep the GUI running offline.

## Publishing a release build

To create a distributable folder use `dotnet publish`. Run the command either from the project directory or pass the project path explicitly. Ensure `--self-contained` is followed by a space and the desired value (`false` or `true`)—additional characters (such as the stray `#` in `false#`) make MSBuild interpret the value as a file path and result in `MSB1009: The project file does not exist`.

```powershell
# From the repository root
 dotnet publish OpensquawkBridge-msfs/OpensquawkBridge-msfs.csproj -c Release -r win-x64 --self-contained false

# Or from the project directory
 cd OpensquawkBridge-msfs
 dotnet publish -c Release -r win-x64 --self-contained false
```

The publish output is written to `OpensquawkBridge-msfs/bin/Release/net8.0-windows/win-x64/publish/`.

## Configuration files

- `.env` (optional): environment overrides for the bridge runtime.
- `bridge-config.json`: automatically created token file stored next to the executable.

Keep `bridge-config.json` with the executable when you move or copy the bridge so the stored token persists.
