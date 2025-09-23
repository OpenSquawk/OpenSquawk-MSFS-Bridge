#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using STimer = System.Timers.Timer;
using DotNetEnv;
using Microsoft.FlightSimulator.SimConnect;

internal sealed class BridgeManager : IDisposable
{
    private enum Defs
    {
        Latitude = 50000,
        Longitude = 50001,
        Altitude = 50002,
        AirspeedIndicated = 50003,
        AirspeedTrue = 50004,
        GroundVelocity = 50005,
        TurbineN1 = 50006,
        OnGround = 50007,
        EngineCombustion = 50008,
        IndicatedAltitude = 50009,
        TransponderCode = 50010,
        AdfActiveFreq = 50011,
        AdfStandbyFreq = 50012
    }

    private enum Reqs
    {
        ReqLatitude = 51000,
        ReqLongitude = 51001,
        ReqAltitude = 51002,
        ReqAirspeedIndicated = 51003,
        ReqAirspeedTrue = 51004,
        ReqGroundVelocity = 51005,
        ReqTurbineN1 = 51006,
        ReqOnGround = 51007,
        ReqEngineCombustion = 51008,
        ReqIndicatedAltitude = 51009,
        ReqTransponderCode = 51010,
        ReqAdfActiveFreq = 51011,
        ReqAdfStandbyFreq = 51012
    }

    private enum Events
    {
        SimStart = 52000,
        SimStop = 52001
    }

    private readonly HttpClient _http = new();
    private SimConnect? _sim;
    private STimer? _activeTimer;
    private STimer? _idleTimer;

    private CancellationTokenSource? _simLoopCts;
    private Task? _simLoopTask;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;

    private readonly string _configPath;
    private BridgeConfig _config;

    private readonly string _bridgeBaseUrl;
    private readonly string _telemetryUrl;
    private readonly string? _authToken;
    private readonly int _activeIntervalSec;
    private readonly int _idleIntervalSec;

    private bool _streamActive;
    private bool _simConnected;
    private bool _flightLoaded;
    private bool _initializedOnce;
    private bool _isUserConnected;
    private string? _connectedUserName;

    private DateTime _lastTs = DateTime.MinValue;

    private double _latitude;
    private double _longitude;
    private double _altitude;
    private double _airspeedIndicated;
    private double _airspeedTrue;
    private double _groundVelocity;
    private double _turbineN1;
    private int _onGround;
    private int _engineCombustion;
    private double _indicatedAltitude;
    private int _transponderCode;
    private int _adfActiveFreq;
    private double _adfStandbyFreq;

    private readonly Dictionary<uint, string> _requestNames = new();

    public event EventHandler<UserStatusChangedEventArgs>? UserStatusChanged;
    public event EventHandler<SimStatusChangedEventArgs>? SimStatusChanged;
    public event EventHandler<BridgeLogEventArgs>? Log;
    public event EventHandler? TokenChanged;

    public BridgeManager()
    {
        try { Env.Load(); } catch { }

        _configPath = Path.Combine(AppContext.BaseDirectory, BridgeConfigService.ConfigFileName);
        _config = BridgeConfigService.Load(_configPath);

        _bridgeBaseUrl = (Environment.GetEnvironmentVariable("BRIDGE_BASE_URL") ?? "https://opensquawk.de").TrimEnd('/');
        _telemetryUrl = Environment.GetEnvironmentVariable("SERVER_URL")
                        ?? Environment.GetEnvironmentVariable("BRIDGE_TELEMETRY_URL")
                        ?? $"{_bridgeBaseUrl}/api/msfs/telemetry";
        _authToken = Environment.GetEnvironmentVariable("AUTH_TOKEN");
        _activeIntervalSec = int.TryParse(Environment.GetEnvironmentVariable("ACTIVE_INTERVAL_SEC"), out var a) ? a : 30;
        _idleIntervalSec = int.TryParse(Environment.GetEnvironmentVariable("IDLE_INTERVAL_SEC"), out var b) ? b : 120;

        _http.Timeout = TimeSpan.FromSeconds(10);
        if (!string.IsNullOrWhiteSpace(_authToken))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
        }

        ApplyTokenHeader();

        _activeTimer = new STimer(_activeIntervalSec * 1000) { AutoReset = true };
        _activeTimer.Elapsed += async (_, __) => await SendActiveTick();

        _idleTimer = new STimer(_idleIntervalSec * 1000) { AutoReset = true };
        _idleTimer.Elapsed += async (_, __) => await SendIdleHeartbeat();

        _requestNames[(uint)Reqs.ReqLatitude] = "Latitude";
        _requestNames[(uint)Reqs.ReqLongitude] = "Longitude";
        _requestNames[(uint)Reqs.ReqAltitude] = "Altitude";
        _requestNames[(uint)Reqs.ReqAirspeedIndicated] = "IAS";
        _requestNames[(uint)Reqs.ReqAirspeedTrue] = "TAS";
        _requestNames[(uint)Reqs.ReqGroundVelocity] = "GS";
        _requestNames[(uint)Reqs.ReqTurbineN1] = "N1";
        _requestNames[(uint)Reqs.ReqOnGround] = "OnGround";
        _requestNames[(uint)Reqs.ReqEngineCombustion] = "EngComb";
        _requestNames[(uint)Reqs.ReqIndicatedAltitude] = "IndAlt";
        _requestNames[(uint)Reqs.ReqTransponderCode] = "Xpdr";
        _requestNames[(uint)Reqs.ReqAdfActiveFreq] = "ADF_Act";
        _requestNames[(uint)Reqs.ReqAdfStandbyFreq] = "ADF_Stby";
    }

    public string Token => _config.Token;
    public bool IsUserConnected => _isUserConnected;
    public string? ConnectedUserName => _connectedUserName;
    public bool SimConnected => _simConnected;
    public bool FlightLoaded => _flightLoaded;
    public string TelemetryUrl => _telemetryUrl;
    public string BridgeBaseUrl => _bridgeBaseUrl;

    public async Task InitializeAsync()
    {
        LogMessage("OpenSquawk Bridge – MSFS Telemetrie Uploader");
        LogMessage($"Server: {_telemetryUrl}");
        LogMessage($"Active Interval: {_activeIntervalSec}s, Idle Interval: {_idleIntervalSec}s");

        if (string.IsNullOrWhiteSpace(_config.Token))
        {
            _config.Token = GenerateToken();
            BridgeConfigService.Save(_configPath, _config);
            ApplyTokenHeader();
            LogMessage("🔐 Kein Token gefunden – neuer Token generiert.");
            TokenChanged?.Invoke(this, EventArgs.Empty);
            OpenLoginPage();
        }
        else
        {
            TokenChanged?.Invoke(this, EventArgs.Empty);
        }

        StartLoginPolling();
        await CheckConnectionAsync(force: true);
    }

    public void OpenLoginPage()
    {
        if (string.IsNullOrWhiteSpace(_config.Token))
        {
            LogMessage("⚠️ Kein Token verfügbar, bitte zuerst generieren.");
            return;
        }

        var url = $"{_bridgeBaseUrl}/bridge/connect?token={Uri.EscapeDataString(_config.Token)}";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            LogMessage("🌐 Bridge-Connect-Seite im Browser geöffnet.");
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Konnte Browser nicht öffnen: {ex.Message}");
        }
    }

    public async Task ResetTokenAsync()
    {
        await StopSimLoopAsync();

        _config.Token = GenerateToken();
        _config.CreatedAt = DateTimeOffset.UtcNow;
        BridgeConfigService.Save(_configPath, _config);
        ApplyTokenHeader();

        _isUserConnected = false;
        _connectedUserName = null;
        UserStatusChanged?.Invoke(this, new UserStatusChangedEventArgs(false, null, _config.Token));

        LogMessage("🔁 Token zurückgesetzt. Bitte im Browser neu verbinden.");
        TokenChanged?.Invoke(this, EventArgs.Empty);
        OpenLoginPage();
        await CheckConnectionAsync(force: true);
    }

    public void Dispose()
    {
        try { _simLoopCts?.Cancel(); } catch { }
        try { _pollCts?.Cancel(); } catch { }

        try { _simLoopTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _pollTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }

        _simLoopCts?.Dispose();
        _pollCts?.Dispose();

        _activeTimer?.Stop();
        _idleTimer?.Stop();
        _activeTimer?.Dispose();
        _idleTimer?.Dispose();

        _http.Dispose();
        try { _sim?.Dispose(); } catch { }
    }

    private void ApplyTokenHeader()
    {
        try
        {
            _http.DefaultRequestHeaders.Remove("X-Bridge-Token");
        }
        catch
        {
        }

        if (!string.IsNullOrWhiteSpace(_config.Token))
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Bridge-Token", _config.Token);
        }
    }

    private void StartLoginPolling()
    {
        if (_pollCts != null)
        {
            return;
        }

        _pollCts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoginLoopAsync(_pollCts.Token));
    }

    private async Task PollLoginLoopAsync(CancellationToken token)
    {
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                await CheckConnectionAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            timer.Dispose();
        }
    }

    private async Task CheckConnectionAsync(bool force = false)
    {
        if (string.IsNullOrWhiteSpace(_config.Token))
        {
            return;
        }

        try
        {
            var user = await FetchUserAsync();
            var connected = user != null;
            var userName = user?.UserName;

            var changed = connected != _isUserConnected || !string.Equals(userName, _connectedUserName, StringComparison.OrdinalIgnoreCase);

            if (changed || force)
            {
                _isUserConnected = connected;
                _connectedUserName = userName;

                UserStatusChanged?.Invoke(this, new UserStatusChangedEventArgs(connected, userName, _config.Token));

                if (connected)
                {
                    LogMessage(userName != null ? $"✅ Benutzer verbunden: {userName}" : "✅ Benutzer verbunden");
                    StartSimLoop();
                }
                else
                {
                    LogMessage("ℹ️ Kein Benutzer verbunden");
                    await StopSimLoopAsync();
                }
            }
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Fehler beim Prüfen des Login-Status: {ex.Message}");
        }
    }

    private void StartSimLoop()
    {
        if (_simLoopCts != null)
        {
            return;
        }

        _simLoopCts = new CancellationTokenSource();
        _simLoopTask = Task.Run(() => RunSimLoopAsync(_simLoopCts.Token));
    }

    private async Task StopSimLoopAsync()
    {
        if (_simLoopCts == null)
        {
            return;
        }

        _simLoopCts.Cancel();
        try
        {
            if (_simLoopTask != null)
            {
                await _simLoopTask;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Fehler beim Stoppen der SimConnect-Loop: {ex.Message}");
        }
        finally
        {
            _simLoopTask = null;
            _simLoopCts.Dispose();
            _simLoopCts = null;
            CleanupSim();
        }
    }

    private void CleanupSim()
    {
        try { _activeTimer?.Stop(); } catch { }
        try { _idleTimer?.Stop(); } catch { }
        StopStream();
        SetSimConnected(false);
        SetFlightLoaded(false);

        if (_sim != null)
        {
            try { _sim.Dispose(); } catch { }
            _sim = null;
        }

        _initializedOnce = false;
    }

    private async Task RunSimLoopAsync(CancellationToken token)
    {
        try
        {
            await InitializeSimConnect();
            await SendIdleHeartbeat();
            _idleTimer?.Start();
            LogMessage("Warte auf SimConnect-Ereignisse...");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    _sim?.ReceiveMessage();
                }
                catch (Exception ex)
                {
                    LogMessage($"❌ ReceiveMessage Fehler: {ex.Message}");
                    await Task.Delay(2000, token);

                    if (!_simConnected)
                    {
                        try
                        {
                            await ReconnectSimConnect();
                        }
                        catch (Exception initEx)
                        {
                            LogMessage($"❌ Reconnection fehlgeschlagen: {initEx.Message}");
                            await Task.Delay(5000, token);
                        }
                    }
                }

                await Task.Delay(50, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogMessage($"❌ SimConnect Initialisierung fehlgeschlagen: {ex.Message}");
            LogMessage("Läuft im Offline-Modus...");

            try
            {
                await SendIdleHeartbeat();
                _idleTimer?.Start();

                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(1000, token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }
        finally
        {
            try { _idleTimer?.Stop(); } catch { }
            try { _activeTimer?.Stop(); } catch { }
        }
    }

    private Task InitializeSimConnect()
    {
        try
        {
            string connectionName = $"OpenSquawkBridge_{Environment.ProcessId}";
            _sim = new SimConnect(connectionName, IntPtr.Zero, 0, null, 0);

            _sim.OnRecvOpen += OnRecvOpen;
            _sim.OnRecvQuit += OnRecvQuit;
            _sim.OnRecvEvent += OnRecvEvent;
            _sim.OnRecvSimobjectData += OnRecvSimobjectData;
            _sim.OnRecvException += OnRecvException;

            LogMessage($"SimConnect initialisiert: {connectionName}");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            LogMessage($"❌ SimConnect Initialisierung Fehler: {ex.Message}");
            SetSimConnected(false);
            throw;
        }
    }

    private async Task ReconnectSimConnect()
    {
        try
        {
            if (_sim != null)
            {
                try { _sim.Dispose(); } catch { }
                _sim = null;
            }

            SetSimConnected(false);
            await Task.Delay(1000);

            await InitializeSimConnect();
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Reconnect Fehler: {ex.Message}");
            throw;
        }
    }

    private void RegisterDataDefinitionsOnce()
    {
        if (_sim == null || _initializedOnce)
        {
            return;
        }

        try
        {
            LogMessage("Registriere einzelne Datendefinitionen...");

            _sim.AddToDataDefinition(Defs.Latitude, "PLANE LATITUDE", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0, 0);
            _sim.RegisterDataDefineStruct<double>(Defs.Latitude);

            _sim.AddToDataDefinition(Defs.Longitude, "PLANE LONGITUDE", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0, 0);
            _sim.RegisterDataDefineStruct<double>(Defs.Longitude);

            _sim.AddToDataDefinition(Defs.Altitude, "PLANE ALTITUDE", "feet", SIMCONNECT_DATATYPE.FLOAT64, 0, 0);
            _sim.RegisterDataDefineStruct<double>(Defs.Altitude);

            _sim.AddToDataDefinition(Defs.AirspeedIndicated, "AIRSPEED INDICATED", "knots", SIMCONNECT_DATATYPE.FLOAT64, 0, 0);
            _sim.RegisterDataDefineStruct<double>(Defs.AirspeedIndicated);

            _sim.AddToDataDefinition(Defs.AirspeedTrue, "AIRSPEED TRUE", "knots", SIMCONNECT_DATATYPE.FLOAT64, 0, 0);
            _sim.RegisterDataDefineStruct<double>(Defs.AirspeedTrue);

            _sim.AddToDataDefinition(Defs.GroundVelocity, "GROUND VELOCITY", "meters per second", SIMCONNECT_DATATYPE.FLOAT64, 0, 0);
            _sim.RegisterDataDefineStruct<double>(Defs.GroundVelocity);

            _sim.AddToDataDefinition(Defs.TurbineN1, "TURB ENG N1:1", "percent", SIMCONNECT_DATATYPE.FLOAT64, 0, 0);
            _sim.RegisterDataDefineStruct<double>(Defs.TurbineN1);

            _sim.AddToDataDefinition(Defs.OnGround, "SIM ON GROUND", "Bool", SIMCONNECT_DATATYPE.INT32, 0, 0);
            _sim.RegisterDataDefineStruct<int>(Defs.OnGround);

            _sim.AddToDataDefinition(Defs.EngineCombustion, "GENERAL ENG COMBUSTION:1", "Bool", SIMCONNECT_DATATYPE.INT32, 0, 0);
            _sim.RegisterDataDefineStruct<int>(Defs.EngineCombustion);

            _sim.AddToDataDefinition(Defs.IndicatedAltitude, "INDICATED ALTITUDE", "feet", SIMCONNECT_DATATYPE.FLOAT64, 0, 0);
            _sim.RegisterDataDefineStruct<double>(Defs.IndicatedAltitude);

            _sim.AddToDataDefinition(Defs.TransponderCode, "TRANSPONDER CODE:2", "BCD16", SIMCONNECT_DATATYPE.INT32, 0, 0);
            _sim.RegisterDataDefineStruct<int>(Defs.TransponderCode);

            _sim.AddToDataDefinition(Defs.AdfActiveFreq, "ADF ACTIVE FREQUENCY:1", "Frequency ADF BCD32", SIMCONNECT_DATATYPE.INT32, 0, 0);
            _sim.RegisterDataDefineStruct<int>(Defs.AdfActiveFreq);

            _sim.AddToDataDefinition(Defs.AdfStandbyFreq, "ADF STANDBY FREQUENCY:1", "Hz", SIMCONNECT_DATATYPE.FLOAT64, 0, 0);
            _sim.RegisterDataDefineStruct<double>(Defs.AdfStandbyFreq);

            _sim.SubscribeToSystemEvent(Events.SimStart, "SimStart");
            _sim.SubscribeToSystemEvent(Events.SimStop, "SimStop");

            _initializedOnce = true;
            LogMessage("✓ Alle Datendefinitionen einzeln registriert");
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Fehler beim Registrieren der Datendefinitionen: {ex.Message}");
        }
    }

    private void OnRecvOpen(SimConnect sender, SIMCONNECT_RECV_OPEN data)
    {
        LogMessage("✓ SimConnect verbunden");
        SetSimConnected(true);
        RegisterDataDefinitionsOnce();
    }

    private async void OnRecvQuit(SimConnect sender, SIMCONNECT_RECV data)
    {
        LogMessage("❌ Simulator beendet");
        SetSimConnected(false);
        SetFlightLoaded(false);
        StopStream();
        ToIdleMode();
        await SendIdleHeartbeat();
    }

    private async void OnRecvEvent(SimConnect sender, SIMCONNECT_RECV_EVENT data)
    {
        try
        {
            if (data.uEventID == (uint)Events.SimStart)
            {
                LogMessage("🛫 Flug gestartet → Aktiver Modus");
                SetFlightLoaded(true);
                StartStream();
                _idleTimer?.Stop();
                await Task.Delay(3000);
                _activeTimer?.Start();
                await SendActiveTick();
            }
            else if (data.uEventID == (uint)Events.SimStop)
            {
                LogMessage("🛬 Flug beendet → Idle Modus");
                SetFlightLoaded(false);
                StopStream();
                ToIdleMode();
                await SendIdleHeartbeat();
            }
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Event-Handler Fehler: {ex.Message}");
        }
    }

    private void OnRecvSimobjectData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        try
        {
            if (data.dwData is not object[] arr || arr.Length == 0)
            {
                return;
            }

            var requestId = data.dwRequestID;

            switch ((Reqs)requestId)
            {
                case Reqs.ReqLatitude:
                    if (arr[0] is double lat)
                    {
                        _latitude = lat;
                        LogMessage($"📍 Latitude: {lat:F6}");
                    }
                    break;

                case Reqs.ReqLongitude:
                    if (arr[0] is double lon)
                    {
                        _longitude = lon;
                        LogMessage($"📍 Longitude: {lon:F6}");
                    }
                    break;

                case Reqs.ReqAltitude:
                    if (arr[0] is double alt)
                    {
                        _altitude = alt;
                        LogMessage($"⛰️ Altitude: {alt:F0}ft");
                    }
                    break;

                case Reqs.ReqAirspeedIndicated:
                    if (arr[0] is double ias)
                    {
                        _airspeedIndicated = ias;
                        LogMessage($"🏎️ IAS: {ias:F0}kt");
                    }
                    break;

                case Reqs.ReqAirspeedTrue:
                    if (arr[0] is double tas)
                    {
                        _airspeedTrue = tas;
                    }
                    break;

                case Reqs.ReqGroundVelocity:
                    if (arr[0] is double gs)
                    {
                        _groundVelocity = gs;
                    }
                    break;

                case Reqs.ReqTurbineN1:
                    if (arr[0] is double n1)
                    {
                        _turbineN1 = n1;
                    }
                    break;

                case Reqs.ReqOnGround:
                    if (arr[0] is int gnd)
                    {
                        _onGround = gnd;
                        LogMessage($"🛬 On Ground: {gnd != 0}");
                    }
                    break;

                case Reqs.ReqEngineCombustion:
                    if (arr[0] is int eng)
                    {
                        _engineCombustion = eng;
                    }
                    break;

                case Reqs.ReqIndicatedAltitude:
                    if (arr[0] is double indAlt)
                    {
                        _indicatedAltitude = indAlt;
                    }
                    break;

                case Reqs.ReqTransponderCode:
                    if (arr[0] is int xpdr)
                    {
                        _transponderCode = xpdr;
                        LogMessage($"📡 Transponder: {xpdr:0000}");
                    }
                    break;

                case Reqs.ReqAdfActiveFreq:
                    if (arr[0] is int adfAct)
                    {
                        _adfActiveFreq = adfAct;
                        LogMessage($"📻 ADF Active: {adfAct}");
                    }
                    break;

                case Reqs.ReqAdfStandbyFreq:
                    if (arr[0] is double adfStby)
                    {
                        _adfStandbyFreq = adfStby;
                        LogMessage($"📻 ADF Standby: {adfStby:F0}Hz");
                    }
                    break;
            }

            _lastTs = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            LogMessage($"❌ SimObject-Daten Fehler: {ex.Message}");
        }
    }

    private void OnRecvException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
    {
        var exception = (SIMCONNECT_EXCEPTION)data.dwException;
        LogMessage($"⚠️ SimConnect Exception: {exception}");

        if (exception == SIMCONNECT_EXCEPTION.DUPLICATE_ID)
        {
            LogMessage("   (DUPLICATE_ID ignoriert)");
        }
    }

    private void StartStream()
    {
        if (_sim == null || _streamActive || !_initializedOnce)
        {
            return;
        }

        try
        {
            _sim.RequestDataOnSimObject(Reqs.ReqLatitude, Defs.Latitude, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND);
            _sim.RequestDataOnSimObject(Reqs.ReqLongitude, Defs.Longitude, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND);
            _sim.RequestDataOnSimObject(Reqs.ReqAltitude, Defs.Altitude, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND);
            _sim.RequestDataOnSimObject(Reqs.ReqAirspeedIndicated, Defs.AirspeedIndicated, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND);
            _sim.RequestDataOnSimObject(Reqs.ReqAirspeedTrue, Defs.AirspeedTrue, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND);
            _sim.RequestDataOnSimObject(Reqs.ReqGroundVelocity, Defs.GroundVelocity, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND);
            _sim.RequestDataOnSimObject(Reqs.ReqTurbineN1, Defs.TurbineN1, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND);
            _sim.RequestDataOnSimObject(Reqs.ReqOnGround, Defs.OnGround, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND);
            _sim.RequestDataOnSimObject(Reqs.ReqEngineCombustion, Defs.EngineCombustion, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND);
            _sim.RequestDataOnSimObject(Reqs.ReqIndicatedAltitude, Defs.IndicatedAltitude, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND);
            _sim.RequestDataOnSimObject(Reqs.ReqTransponderCode, Defs.TransponderCode, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND);
            _sim.RequestDataOnSimObject(Reqs.ReqAdfActiveFreq, Defs.AdfActiveFreq, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND);
            _sim.RequestDataOnSimObject(Reqs.ReqAdfStandbyFreq, Defs.AdfStandbyFreq, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND);

            _streamActive = true;
            _lastTs = DateTime.UtcNow;
            LogMessage("📡 Datastream gestartet");
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Stream-Start Fehler: {ex.Message}");
        }
    }

    private void StopStream()
    {
        if (_sim == null || !_streamActive)
        {
            return;
        }

        try
        {
            _sim.RequestDataOnSimObject(Reqs.ReqLatitude, Defs.Latitude, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER);
            _sim.RequestDataOnSimObject(Reqs.ReqLongitude, Defs.Longitude, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER);
            _sim.RequestDataOnSimObject(Reqs.ReqAltitude, Defs.Altitude, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER);
            _sim.RequestDataOnSimObject(Reqs.ReqAirspeedIndicated, Defs.AirspeedIndicated, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER);
            _sim.RequestDataOnSimObject(Reqs.ReqAirspeedTrue, Defs.AirspeedTrue, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER);
            _sim.RequestDataOnSimObject(Reqs.ReqGroundVelocity, Defs.GroundVelocity, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER);
            _sim.RequestDataOnSimObject(Reqs.ReqTurbineN1, Defs.TurbineN1, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER);
            _sim.RequestDataOnSimObject(Reqs.ReqOnGround, Defs.OnGround, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER);
            _sim.RequestDataOnSimObject(Reqs.ReqEngineCombustion, Defs.EngineCombustion, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER);
            _sim.RequestDataOnSimObject(Reqs.ReqIndicatedAltitude, Defs.IndicatedAltitude, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER);
            _sim.RequestDataOnSimObject(Reqs.ReqTransponderCode, Defs.TransponderCode, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER);
            _sim.RequestDataOnSimObject(Reqs.ReqAdfActiveFreq, Defs.AdfActiveFreq, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER);
            _sim.RequestDataOnSimObject(Reqs.ReqAdfStandbyFreq, Defs.AdfStandbyFreq, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER);

            _streamActive = false;
            _lastTs = DateTime.MinValue;
            LogMessage("📡 Alle Datenstreams gestoppt");
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Stream-Stop Fehler: {ex.Message}");
        }
    }

    private void ToIdleMode()
    {
        try { _activeTimer?.Stop(); } catch { }
        try
        {
            if (_idleTimer != null && !_idleTimer.Enabled)
            {
                _idleTimer.Start();
                LogMessage("💤 Idle-Modus aktiviert");
            }
        }
        catch
        {
        }
    }

    private async Task SendActiveTick()
    {
        try
        {
            if (!_simConnected || !_flightLoaded)
            {
                LogMessage("⚠️ Simulator nicht verbunden oder kein Flug geladen");
                return;
            }

            if (string.IsNullOrWhiteSpace(_config.Token))
            {
                LogMessage("⚠️ Kein Token verfügbar – aktiver Tick wird übersprungen");
                return;
            }

            var age = DateTime.UtcNow - _lastTs;
            if (age > TimeSpan.FromSeconds(10))
            {
                LogMessage($"⚠️ Keine frischen Daten (Alter: {age.TotalSeconds:F1}s)");
                return;
            }

            if (double.IsNaN(_latitude) || double.IsNaN(_longitude) ||
                double.IsInfinity(_latitude) || double.IsInfinity(_longitude) ||
                Math.Abs(_latitude) > 90 || Math.Abs(_longitude) > 180)
            {
                LogMessage($"⚠️ Ungültige Koordinaten: lat={_latitude:F6}, lon={_longitude:F6}");
                return;
            }

            var gsKt = _groundVelocity * 1.943844;
            bool engOn = (_engineCombustion != 0) || (_turbineN1 > 5.0);

            var payload = new
            {
                token = _config.Token,
                status = "active",
                ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                latitude = Math.Round(_latitude, 6),
                longitude = Math.Round(_longitude, 6),
                altitude_ft_true = Math.Round(_altitude, 0),
                altitude_ft_indicated = Math.Round(_indicatedAltitude, 0),
                ias_kt = Math.Round(_airspeedIndicated, 1),
                tas_kt = Math.Round(_airspeedTrue, 1),
                groundspeed_kt = Math.Round(gsKt, 1),
                on_ground = _onGround != 0,
                eng_on = engOn,
                n1_pct = Math.Round(_turbineN1, 1),
                transponder_code = _transponderCode,
                adf_active_freq = _adfActiveFreq,
                adf_standby_freq_hz = Math.Round(_adfStandbyFreq, 0)
            };

            LogMessage($"🚁 AKTIV: lat={payload.latitude:F6} lon={payload.longitude:F6} " +
                       $"alt={payload.altitude_ft_true:F0}ft gs={payload.groundspeed_kt:F1}kt " +
                       $"engine={engOn} xpdr={_transponderCode:0000}");

            await PostJson(payload);
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Active tick Fehler: {ex.Message}");
        }
    }

    private async Task SendIdleHeartbeat()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_config.Token))
            {
                LogMessage("⚠️ Kein Token verfügbar – Idle-Heartbeat wird übersprungen");
                return;
            }

            var payload = new
            {
                token = _config.Token,
                status = "idle",
                ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                sim_connected = _simConnected,
                flight_loaded = _flightLoaded
            };

            string statusIcon = _simConnected ? (_flightLoaded ? "🛫" : "🎮") : "❌";
            LogMessage($"{statusIcon} Idle heartbeat (connected: {_simConnected}, flight: {_flightLoaded})");

            await PostJson(payload);
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Idle heartbeat Fehler: {ex.Message}");
        }
    }

    private async Task PostJson(object obj)
    {
        if (string.IsNullOrWhiteSpace(_telemetryUrl))
        {
            LogMessage("⚠️ Keine Telemetrie-URL definiert");
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(obj);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, _telemetryUrl)
            {
                Content = content
            };

            using var resp = await _http.SendAsync(request);

            if (!resp.IsSuccessStatusCode)
            {
                LogMessage($"❌ HTTP {resp.StatusCode}: {resp.ReasonPhrase}");
            }
            else
            {
                LogMessage("✓ Daten erfolgreich gesendet");
            }
        }
        catch (Exception ex)
        {
            LogMessage($"❌ HTTP POST Fehler: {ex.Message}");
        }
    }

    private async Task<BridgeUser?> FetchUserAsync()
    {
        if (string.IsNullOrWhiteSpace(_config.Token))
        {
            return null;
        }

        var url = $"{_bridgeBaseUrl}/bridge/me?token={Uri.EscapeDataString(_config.Token)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        var root = document.RootElement;

        if (root.TryGetProperty("connected", out var connectedProp) && connectedProp.ValueKind == JsonValueKind.False)
        {
            return null;
        }

        var userName = ExtractUserName(root);
        if (userName == null && root.ValueKind == JsonValueKind.Object && root.TryGetProperty("user", out var userElement))
        {
            userName = ExtractUserName(userElement);
        }

        return new BridgeUser(userName);
    }

    private static string? ExtractUserName(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? value = TryGetString(element, "username")
                        ?? TryGetString(element, "userName")
                        ?? TryGetString(element, "name")
                        ?? TryGetString(element, "displayName")
                        ?? TryGetString(element, "email");

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var property))
        {
            if (property.ValueKind == JsonValueKind.String)
            {
                return property.GetString();
            }
        }

        return null;
    }

    private static string GenerateToken()
    {
        Span<byte> buffer = stackalloc byte[32];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToBase64String(buffer).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private void SetSimConnected(bool connected)
    {
        if (_simConnected != connected)
        {
            _simConnected = connected;
            RaiseSimStatusChanged();
        }
    }

    private void SetFlightLoaded(bool loaded)
    {
        if (_flightLoaded != loaded)
        {
            _flightLoaded = loaded;
            RaiseSimStatusChanged();
        }
    }

    private void RaiseSimStatusChanged()
    {
        SimStatusChanged?.Invoke(this, new SimStatusChangedEventArgs(_simConnected, _flightLoaded));
    }

    private void LogMessage(string message)
    {
        try
        {
            Console.WriteLine(message);
        }
        catch
        {
        }

        Log?.Invoke(this, new BridgeLogEventArgs(message));
    }
}

internal sealed record BridgeUser(string? UserName);

internal sealed class UserStatusChangedEventArgs : EventArgs
{
    public UserStatusChangedEventArgs(bool isConnected, string? userName, string token)
    {
        IsConnected = isConnected;
        UserName = userName;
        Token = token;
    }

    public bool IsConnected { get; }
    public string? UserName { get; }
    public string Token { get; }
}

internal sealed class SimStatusChangedEventArgs : EventArgs
{
    public SimStatusChangedEventArgs(bool simConnected, bool flightLoaded)
    {
        SimConnected = simConnected;
        FlightLoaded = flightLoaded;
    }

    public bool SimConnected { get; }
    public bool FlightLoaded { get; }
}

internal sealed class BridgeLogEventArgs : EventArgs
{
    public BridgeLogEventArgs(string message)
    {
        Message = message;
        Timestamp = DateTimeOffset.Now;
    }

    public string Message { get; }
    public DateTimeOffset Timestamp { get; }
}
