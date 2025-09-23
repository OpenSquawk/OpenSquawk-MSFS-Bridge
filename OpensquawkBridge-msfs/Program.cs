#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using STimer = System.Timers.Timer;
using Microsoft.FlightSimulator.SimConnect;
using DotNetEnv; // NuGet: dotnet add package DotNetEnv

class Program
{
    // Separate Definitionen für jede SimVar
    enum Defs 
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
    
    // Separate Requests für jede SimVar
    enum Reqs 
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
    
    enum Events { SimStart = 52000, SimStop = 52001 }

    static SimConnect? sim;
    static STimer? activeT, idleT;
    static readonly HttpClient http = new HttpClient();

    // .env / ENV
    static string SERVER_URL = "http://localhost:3000/api/msfs/telemetry";
    static string AUTH_TOKEN = "";
    static int ACTIVE_SEC = 30, IDLE_SEC = 120;

    // Einzelne Variablen für jede SimVar
    static double _latitude = 0.0;
    static double _longitude = 0.0;
    static double _altitude = 0.0;
    static double _airspeedIndicated = 0.0;
    static double _airspeedTrue = 0.0;
    static double _groundVelocity = 0.0;
    static double _turbineN1 = 0.0;
    static int _onGround = 0;
    static int _engineCombustion = 0;
    static double _indicatedAltitude = 0.0;
    static int _transponderCode = 0;
    static int _adfActiveFreq = 0;
    static double _adfStandbyFreq = 0.0;
    
    static DateTime _lastTs = DateTime.MinValue;
    static bool _streamActive = false;
    static bool _simConnected = false;
    static bool _flightLoaded = false;
    static bool _initializedOnce = false;
    static Dictionary<uint, string> _requestNames = new();

    static async Task Main()
    {
        // .env laden (optional)
        try { Env.Load(); } catch { }
        SERVER_URL = Environment.GetEnvironmentVariable("SERVER_URL") ?? SERVER_URL;
        AUTH_TOKEN = Environment.GetEnvironmentVariable("AUTH_TOKEN") ?? AUTH_TOKEN;
        ACTIVE_SEC = int.TryParse(Environment.GetEnvironmentVariable("ACTIVE_INTERVAL_SEC"), out var a) ? a : ACTIVE_SEC;
        IDLE_SEC   = int.TryParse(Environment.GetEnvironmentVariable("IDLE_INTERVAL_SEC"),   out var b) ? b : IDLE_SEC;

        Console.WriteLine("OpenSquawk Bridge – MSFS Telemetrie Uploader (Einzelabfrage)");
        Console.WriteLine($"Server: {SERVER_URL}");
        Console.WriteLine($"Active Interval: {ACTIVE_SEC}s, Idle Interval: {IDLE_SEC}s");

        // Request-Namen für Debugging
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

        http.Timeout = TimeSpan.FromSeconds(10);
        if (!string.IsNullOrWhiteSpace(AUTH_TOKEN))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AUTH_TOKEN);

        activeT = new STimer(ACTIVE_SEC * 1000) { AutoReset = true };
        activeT.Elapsed += async (_, __) => await SendActiveTick();

        idleT = new STimer(IDLE_SEC * 1000) { AutoReset = true };
        idleT.Elapsed += async (_, __) => await SendIdleHeartbeat();

        try
        {
            await InitializeSimConnect();
            await SendIdleHeartbeat();
            idleT.Start();
            Console.WriteLine("Warte auf SimConnect-Ereignisse...");

            while (true)
            {
                try
                {
                    sim?.ReceiveMessage();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ ReceiveMessage Fehler: {ex.Message}");
                    await Task.Delay(2000);
                    
                    if (!_simConnected)
                    {
                        try
                        {
                            await ReconnectSimConnect();
                        }
                        catch (Exception initEx)
                        {
                            Console.WriteLine($"❌ Reconnection fehlgeschlagen: {initEx.Message}");
                            await Task.Delay(5000);
                        }
                    }
                }
                await Task.Delay(50);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ SimConnect Initialisierung fehlgeschlagen: {ex.Message}");
            Console.WriteLine("Läuft im Offline-Modus...");
            
            await SendIdleHeartbeat();
            idleT.Start();
            
            while (true) 
            {
                await Task.Delay(1000);
            }
        }
    }

    static async Task InitializeSimConnect()
    {
        try
        {
            string connectionName = $"OpenSquawkBridge_{Environment.ProcessId}";
            sim = new SimConnect(connectionName, IntPtr.Zero, 0, null, 0);

            // Event Handlers registrieren
            sim.OnRecvOpen += OnRecvOpen;
            sim.OnRecvQuit += OnRecvQuit;
            sim.OnRecvEvent += OnRecvEvent;
            sim.OnRecvSimobjectData += OnRecvSimobjectData;
            sim.OnRecvException += OnRecvException;

            Console.WriteLine($"SimConnect initialisiert: {connectionName}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ SimConnect Initialisierung Fehler: {ex.Message}");
            _simConnected = false;
            throw;
        }
    }

    static async Task ReconnectSimConnect()
    {
        try
        {
            if (sim != null)
            {
                try { sim.Dispose(); } catch { }
                sim = null;
            }
            
            _simConnected = false;
            await Task.Delay(1000);
            
            await InitializeSimConnect();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Reconnect Fehler: {ex.Message}");
            throw;
        }
    }

    static void RegisterDataDefinitionsOnce()
    {
        if (sim == null || _initializedOnce) return;

        try
        {
            Console.WriteLine("Registriere einzelne Datendefinitionen...");

            // Jede SimVar einzeln definieren
            sim.AddToDataDefinition(Defs.Latitude, "PLANE LATITUDE", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0, 0);
            sim.RegisterDataDefineStruct<double>(Defs.Latitude);

            sim.AddToDataDefinition(Defs.Longitude, "PLANE LONGITUDE", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0, 0);
            sim.RegisterDataDefineStruct<double>(Defs.Longitude);

            sim.AddToDataDefinition(Defs.Altitude, "PLANE ALTITUDE", "feet", SIMCONNECT_DATATYPE.FLOAT64, 0, 0);
            sim.RegisterDataDefineStruct<double>(Defs.Altitude);

            sim.AddToDataDefinition(Defs.AirspeedIndicated, "AIRSPEED INDICATED", "knots", SIMCONNECT_DATATYPE.FLOAT64, 0, 0);
            sim.RegisterDataDefineStruct<double>(Defs.AirspeedIndicated);

            sim.AddToDataDefinition(Defs.AirspeedTrue, "AIRSPEED TRUE", "knots", SIMCONNECT_DATATYPE.FLOAT64, 0, 0);
            sim.RegisterDataDefineStruct<double>(Defs.AirspeedTrue);

            sim.AddToDataDefinition(Defs.GroundVelocity, "GROUND VELOCITY", "meters per second", SIMCONNECT_DATATYPE.FLOAT64, 0, 0);
            sim.RegisterDataDefineStruct<double>(Defs.GroundVelocity);

            sim.AddToDataDefinition(Defs.TurbineN1, "TURB ENG N1:1", "percent", SIMCONNECT_DATATYPE.FLOAT64, 0, 0);
            sim.RegisterDataDefineStruct<double>(Defs.TurbineN1);

            sim.AddToDataDefinition(Defs.OnGround, "SIM ON GROUND", "Bool", SIMCONNECT_DATATYPE.INT32, 0, 0);
            sim.RegisterDataDefineStruct<int>(Defs.OnGround);

            sim.AddToDataDefinition(Defs.EngineCombustion, "GENERAL ENG COMBUSTION:1", "Bool", SIMCONNECT_DATATYPE.INT32, 0, 0);
            sim.RegisterDataDefineStruct<int>(Defs.EngineCombustion);

            sim.AddToDataDefinition(Defs.IndicatedAltitude, "INDICATED ALTITUDE", "feet", SIMCONNECT_DATATYPE.FLOAT64, 0, 0);
            sim.RegisterDataDefineStruct<double>(Defs.IndicatedAltitude);

            sim.AddToDataDefinition(Defs.TransponderCode, "TRANSPONDER CODE:2", "BCD16", SIMCONNECT_DATATYPE.INT32, 0, 0);
            sim.RegisterDataDefineStruct<int>(Defs.TransponderCode);

            sim.AddToDataDefinition(Defs.AdfActiveFreq, "ADF ACTIVE FREQUENCY:1", "Frequency ADF BCD32", SIMCONNECT_DATATYPE.INT32, 0, 0);
            sim.RegisterDataDefineStruct<int>(Defs.AdfActiveFreq);

            sim.AddToDataDefinition(Defs.AdfStandbyFreq, "ADF STANDBY FREQUENCY:1", "Hz", SIMCONNECT_DATATYPE.FLOAT64, 0, 0);
            sim.RegisterDataDefineStruct<double>(Defs.AdfStandbyFreq);

            // Events registrieren
            sim.SubscribeToSystemEvent(Events.SimStart, "SimStart");
            sim.SubscribeToSystemEvent(Events.SimStop, "SimStop");

            _initializedOnce = true;
            Console.WriteLine("✓ Alle Datendefinitionen einzeln registriert");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Fehler beim Registrieren der Datendefinitionen: {ex.Message}");
        }
    }

    static void OnRecvOpen(SimConnect sender, SIMCONNECT_RECV_OPEN data)
    {
        Console.WriteLine("✓ SimConnect verbunden");
        _simConnected = true;
        RegisterDataDefinitionsOnce();
    }

    static void OnRecvQuit(SimConnect sender, SIMCONNECT_RECV data)
    {
        Console.WriteLine("❌ Simulator beendet");
        _simConnected = false;
        _flightLoaded = false;
        StopStream();
        ToIdleMode();
    }

    static async void OnRecvEvent(SimConnect sender, SIMCONNECT_RECV_EVENT data)
    {
        try
        {
            if (data.uEventID == (uint)Events.SimStart)
            {
                Console.WriteLine("🛫 Flug gestartet → Aktiver Modus");
                _flightLoaded = true;
                StartStream();
                idleT!.Stop();
                await Task.Delay(3000);
                activeT!.Start();
                await SendActiveTick();
            }
            else if (data.uEventID == (uint)Events.SimStop)
            {
                Console.WriteLine("🛬 Flug beendet → Idle Modus");
                _flightLoaded = false;
                StopStream();
                ToIdleMode();
                await SendIdleHeartbeat();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Event-Handler Fehler: {ex.Message}");
        }
    }

    static void OnRecvSimobjectData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        try
        {
            if (data.dwData is not object[] arr || arr.Length == 0) return;

            var requestId = data.dwRequestID;
            var requestName = _requestNames.GetValueOrDefault(requestId, $"Unknown_{requestId}");

            // Je nach Request ID die entsprechende Variable setzen
            switch ((Reqs)requestId)
            {
                case Reqs.ReqLatitude:
                    if (arr[0] is double lat) 
                    {
                        _latitude = lat;
                        Console.WriteLine($"📍 Latitude: {lat:F6}");
                    }
                    break;
                    
                case Reqs.ReqLongitude:
                    if (arr[0] is double lon) 
                    {
                        _longitude = lon;
                        Console.WriteLine($"📍 Longitude: {lon:F6}");
                    }
                    break;
                    
                case Reqs.ReqAltitude:
                    if (arr[0] is double alt) 
                    {
                        _altitude = alt;
                        Console.WriteLine($"⛰️ Altitude: {alt:F0}ft");
                    }
                    break;
                    
                case Reqs.ReqAirspeedIndicated:
                    if (arr[0] is double ias) 
                    {
                        _airspeedIndicated = ias;
                        Console.WriteLine($"🏎️ IAS: {ias:F0}kt");
                    }
                    break;
                    
                case Reqs.ReqAirspeedTrue:
                    if (arr[0] is double tas) _airspeedTrue = tas;
                    break;
                    
                case Reqs.ReqGroundVelocity:
                    if (arr[0] is double gs) _groundVelocity = gs;
                    break;
                    
                case Reqs.ReqTurbineN1:
                    if (arr[0] is double n1) _turbineN1 = n1;
                    break;
                    
                case Reqs.ReqOnGround:
                    if (arr[0] is int gnd) 
                    {
                        _onGround = gnd;
                        Console.WriteLine($"🛬 On Ground: {gnd != 0}");
                    }
                    break;
                    
                case Reqs.ReqEngineCombustion:
                    if (arr[0] is int eng) _engineCombustion = eng;
                    break;
                    
                case Reqs.ReqIndicatedAltitude:
                    if (arr[0] is double indAlt) _indicatedAltitude = indAlt;
                    break;
                    
                case Reqs.ReqTransponderCode:
                    if (arr[0] is int xpdr) 
                    {
                        _transponderCode = xpdr;
                        Console.WriteLine($"📡 Transponder: {xpdr:0000}");
                    }
                    break;
                    
                case Reqs.ReqAdfActiveFreq:
                    if (arr[0] is int adfAct) 
                    {
                        _adfActiveFreq = adfAct;
                        Console.WriteLine($"📻 ADF Active: {adfAct}");
                    }
                    break;
                    
                case Reqs.ReqAdfStandbyFreq:
                    if (arr[0] is double adfStby) 
                    {
                        _adfStandbyFreq = adfStby;
                        Console.WriteLine($"📻 ADF Standby: {adfStby:F0}Hz");
                    }
                    break;
            }

            _lastTs = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ SimObject-Daten Fehler: {ex.Message}");
        }
    }

    static void OnRecvException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
    {
        var exception = (SIMCONNECT_EXCEPTION)data.dwException;
        Console.WriteLine($"⚠️ SimConnect Exception: {exception}");
        
        if (exception == SIMCONNECT_EXCEPTION.DUPLICATE_ID)
        {
            Console.WriteLine("   (DUPLICATE_ID ignoriert)");
            return;
        }
    }

    static void StartStream()
    {
        if (sim == null || _streamActive || !_initializedOnce) return;
        
        try
        {
            // Alle Daten einzeln anfordern
            sim.RequestDataOnSimObject(Reqs.ReqLatitude, Defs.Latitude, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            sim.RequestDataOnSimObject(Reqs.ReqLongitude, Defs.Longitude, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            sim.RequestDataOnSimObject(Reqs.ReqAltitude, Defs.Altitude, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            sim.RequestDataOnSimObject(Reqs.ReqAirspeedIndicated, Defs.AirspeedIndicated, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            sim.RequestDataOnSimObject(Reqs.ReqAirspeedTrue, Defs.AirspeedTrue, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            sim.RequestDataOnSimObject(Reqs.ReqGroundVelocity, Defs.GroundVelocity, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            sim.RequestDataOnSimObject(Reqs.ReqTurbineN1, Defs.TurbineN1, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            sim.RequestDataOnSimObject(Reqs.ReqOnGround, Defs.OnGround, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            sim.RequestDataOnSimObject(Reqs.ReqEngineCombustion, Defs.EngineCombustion, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            sim.RequestDataOnSimObject(Reqs.ReqIndicatedAltitude, Defs.IndicatedAltitude, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            sim.RequestDataOnSimObject(Reqs.ReqTransponderCode, Defs.TransponderCode, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            sim.RequestDataOnSimObject(Reqs.ReqAdfActiveFreq, Defs.AdfActiveFreq, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            sim.RequestDataOnSimObject(Reqs.ReqAdfStandbyFreq, Defs.AdfStandbyFreq, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);

            _streamActive = true;
            Console.WriteLine("📡 Alle Datenstreams einzeln gestartet");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Stream-Start Fehler: {ex.Message}");
        }
    }

    static void StopStream()
    {
        if (sim == null || !_streamActive) return;
        
        try
        {
            // Alle Datenstreams stoppen
            sim.RequestDataOnSimObject(Reqs.ReqLatitude, Defs.Latitude, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            sim.RequestDataOnSimObject(Reqs.ReqLongitude, Defs.Longitude, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            sim.RequestDataOnSimObject(Reqs.ReqAltitude, Defs.Altitude, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            sim.RequestDataOnSimObject(Reqs.ReqAirspeedIndicated, Defs.AirspeedIndicated, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            sim.RequestDataOnSimObject(Reqs.ReqAirspeedTrue, Defs.AirspeedTrue, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            sim.RequestDataOnSimObject(Reqs.ReqGroundVelocity, Defs.GroundVelocity, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            sim.RequestDataOnSimObject(Reqs.ReqTurbineN1, Defs.TurbineN1, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            sim.RequestDataOnSimObject(Reqs.ReqOnGround, Defs.OnGround, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            sim.RequestDataOnSimObject(Reqs.ReqEngineCombustion, Defs.EngineCombustion, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            sim.RequestDataOnSimObject(Reqs.ReqIndicatedAltitude, Defs.IndicatedAltitude, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            sim.RequestDataOnSimObject(Reqs.ReqTransponderCode, Defs.TransponderCode, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            sim.RequestDataOnSimObject(Reqs.ReqAdfActiveFreq, Defs.AdfActiveFreq, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            sim.RequestDataOnSimObject(Reqs.ReqAdfStandbyFreq, Defs.AdfStandbyFreq, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);

            _streamActive = false;
            _lastTs = DateTime.MinValue;
            Console.WriteLine("📡 Alle Datenstreams gestoppt");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Stream-Stop Fehler: {ex.Message}");
        }
    }

    static void ToIdleMode()
    {
        try { activeT?.Stop(); } catch { }
        try { if (idleT != null && !idleT.Enabled) { idleT.Start(); Console.WriteLine("💤 Idle-Modus aktiviert"); } } catch { }
    }

    static async Task SendActiveTick()
    {
        try
        {
            if (!_simConnected || !_flightLoaded)
            {
                Console.WriteLine("⚠️ Simulator nicht verbunden oder kein Flug geladen");
                return;
            }

            var age = DateTime.UtcNow - _lastTs;
            if (age > TimeSpan.FromSeconds(10))
            {
                Console.WriteLine($"⚠️ Keine frischen Daten (Alter: {age.TotalSeconds:F1}s)");
                return;
            }

            // Datenvalidierung
            if (double.IsNaN(_latitude) || double.IsNaN(_longitude) || 
                double.IsInfinity(_latitude) || double.IsInfinity(_longitude) ||
                Math.Abs(_latitude) > 90 || Math.Abs(_longitude) > 180)
            {
                Console.WriteLine($"⚠️ Ungültige Koordinaten: lat={_latitude:F6}, lon={_longitude:F6}");
                return;
            }

            var gsKt = _groundVelocity * 1.943844;
            bool engOn = (_engineCombustion != 0) || (_turbineN1 > 5.0);

            var payload = new
            {
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

            Console.WriteLine($"🚁 AKTIV: lat={payload.latitude:F6} lon={payload.longitude:F6} " +
                            $"alt={payload.altitude_ft_true:F0}ft gs={payload.groundspeed_kt:F1}kt " +
                            $"engine={engOn} xpdr={_transponderCode:0000}");
            
            await PostJson(payload);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Active tick Fehler: {ex.Message}");
        }
    }

    static async Task SendIdleHeartbeat()
    {
        try
        {
            var payload = new 
            { 
                status = "idle", 
                ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                sim_connected = _simConnected,
                flight_loaded = _flightLoaded
            };
            
            string statusIcon = _simConnected ? (_flightLoaded ? "🛫" : "🎮") : "❌";
            Console.WriteLine($"{statusIcon} Idle heartbeat (connected: {_simConnected}, flight: {_flightLoaded})");
            
            await PostJson(payload);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Idle heartbeat Fehler: {ex.Message}");
        }
    }

    static async Task PostJson(object obj)
    {
        try
        {
            using var content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(obj), 
                System.Text.Encoding.UTF8, 
                "application/json"
            );
            
            var resp = await http.PostAsync(SERVER_URL, content);
            
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"❌ HTTP {resp.StatusCode}: {resp.ReasonPhrase}");
            }
            else
            {
                Console.WriteLine("✓ Daten erfolgreich gesendet");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ HTTP POST Fehler: {ex.Message}");
        }
    }
}