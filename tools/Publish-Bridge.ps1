[CmdletBinding()]
param(
    [switch]$OpenExplorer,
    [switch]$RunExecutable
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectPath = Join-Path $repoRoot 'OpensquawkBridge-msfs/OpensquawkBridge-msfs.csproj'
$publishRuntime = 'win-x64'
$configuration = 'Release'
$framework = 'net8.0-windows'
$publishFolder = Join-Path $repoRoot "OpensquawkBridge-msfs/bin/$configuration/$framework/$publishRuntime/publish"
$exeName = 'OpensquawkBridge-msfs.exe'
$exePath = Join-Path $publishFolder $exeName
$depsFolder = Join-Path $publishFolder 'deps'
$libsSource = Join-Path $repoRoot 'libs'
$nativeLibsToKeep = @('SimConnect.dll', 'Microsoft.FlightSimulator.SimConnect.dll')
$filesToKeepInRoot = @('.env', '.env.example')

function Invoke-DotNetCommand {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    Write-Host "Running: dotnet $($Arguments -join ' ')" -ForegroundColor Cyan
    & dotnet @Arguments
}

Push-Location $repoRoot
try {
    Invoke-DotNetCommand -Arguments @('restore')
    Invoke-DotNetCommand -Arguments @('build')
    Invoke-DotNetCommand -Arguments @('publish', $projectPath, '-c', $configuration, '-r', $publishRuntime, '--self-contained', 'true')

    if (-not (Test-Path $publishFolder)) {
        throw "Publish output folder '$publishFolder' was not created."
    }

    if (Test-Path $libsSource) {
        Write-Host "Copying libraries from '$libsSource' to '$publishFolder'" -ForegroundColor Cyan
        Get-ChildItem -Path $libsSource -Filter '*.dll' | ForEach-Object {
            Copy-Item -Path $_.FullName -Destination $publishFolder -Force
        }
    } else {
        Write-Warning "Library source folder '$libsSource' not found."
    }

    if (-not (Test-Path $depsFolder)) {
        New-Item -ItemType Directory -Path $depsFolder | Out-Null
    }

    Get-ChildItem -Path $publishFolder | Where-Object {
        $_.Name -ne $exeName -and
        $_.Name -ne 'deps' -and
        ($nativeLibsToKeep -notcontains $_.Name) -and
        ($filesToKeepInRoot -notcontains $_.Name)
    } | ForEach-Object {
        $destination = Join-Path $depsFolder $_.Name
        if (Test-Path $destination) {
            Remove-Item -Path $destination -Recurse -Force
        }
        Move-Item -Path $_.FullName -Destination $destination
    }

    Write-Host "Publish layout prepared at '$publishFolder'." -ForegroundColor Green

    if ($OpenExplorer) {
        Write-Host "Opening explorer.exe at '$publishFolder'" -ForegroundColor Cyan
        Start-Process -FilePath 'explorer.exe' -ArgumentList $publishFolder
    }

    if ($RunExecutable) {
        if (-not (Test-Path $exePath)) {
            throw "Executable '$exePath' not found."
        }

        Write-Host "Launching '$exeName'" -ForegroundColor Cyan
        Start-Process -FilePath $exePath -WorkingDirectory $publishFolder
    }
} finally {
    Pop-Location
}
