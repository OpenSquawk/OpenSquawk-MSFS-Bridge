using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;
using OpensquawkBridge.Abstractions;

namespace OpensquawkBridge.SimConnectAdapter;

public sealed class SimConnectAdapter : ISimConnectAdapter
{
    private enum Defs : uint
    {
        Latitude = 0x5000,
        Longitude = 0x5001,
        Altitude = 0x5002,
        AirspeedIndicated = 0x5003,
        AirspeedTrue = 0x5004,
        GroundVelocity = 0x5005,
        TurbineN1 = 0x5006,
        OnGround = 0x5007,
        EngineCombustion = 0x5008,
        IndicatedAltitude = 0x5009,
        TransponderCode = 0x500A,
        AdfActiveFreq = 0x500B,
        AdfStandbyFreq = 0x500C
    }

    private enum Reqs : uint
    {
        Latitude = 0x5100,
        Longitude = 0x5101,
        Altitude = 0x5102,
        AirspeedIndicated = 0x5103,
        AirspeedTrue = 0x5104,
        GroundVelocity = 0x5105,
        TurbineN1 = 0x5106,
        OnGround = 0x5107,
        EngineCombustion = 0x5108,
        IndicatedAltitude = 0x5109,
        TransponderCode = 0x510A,
        AdfActiveFreq = 0x510B,
        AdfStandbyFreq = 0x510C
    }

    private enum Events : uint
    {
        SimStart = 0x5200,
        SimStop = 0x5201
    }

    private SimConnect? _sim;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private bool _registered;
    private bool _streamActive;
    private bool _disposed;

    private double _latitude;
    private double _longitude;
    private double _altitude;
    private double _indicatedAltitude;
    private double _airspeedIndicated;
    private double _airspeedTrue;
    private double _groundVelocity;
    private double _turbineN1;
    private bool _onGround;
    private bool _engineCombustion;
    private int _transponderCode;
    private int _adfActiveFreq;
    private double _adfStandbyFreq;
    private DateTimeOffset _lastTelemetryTs;

    public event EventHandler<LogMessageEventArgs>? Log;
    public event EventHandler<SimConnectionChangedEventArgs>? ConnectionChanged;
    public event EventHandler<SimTelemetryEventArgs>? Telemetry;

    public bool IsConnected { get; private set; }
    public bool IsFlightLoaded { get; private set; }

    public Task StartAsync(CancellationToken token)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SimConnectAdapter));
        }

        if (_loopTask != null)
        {
            return Task.CompletedTask;
        }

        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _loopTask = Task.Run(() => RunAsync(_loopCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_loopTask == null)
        {
            return;
        }

        try
        {
            _loopCts?.Cancel();
            await _loopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _loopTask = null;
            _loopCts?.Dispose();
            _loopCts = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }
        finally
        {
            Cleanup();
            GC.SuppressFinalize(this);
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            InitializeSimConnect();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    _sim?.ReceiveMessage();
                }
                catch (COMException ex)
                {
                    Log?.Invoke(this, new LogMessageEventArgs($"SimConnect receive error: {ex.Message}"));
                    await Task.Delay(1000, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log?.Invoke(this, new LogMessageEventArgs($"SimConnect loop error: {ex.Message}"));
                    await Task.Delay(1000, token).ConfigureAwait(false);
                }

                await Task.Delay(50, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Cleanup();
        }
    }

    private void InitializeSimConnect()
    {
        var connectionName = $"OpenSquawkBridge_{Environment.ProcessId}";
        _sim = new SimConnect(connectionName, IntPtr.Zero, 0, null, 0);

        _sim.OnRecvOpen += OnRecvOpen;
        _sim.OnRecvQuit += OnRecvQuit;
        _sim.OnRecvEvent += OnRecvEvent;
        _sim.OnRecvSimobjectData += OnRecvSimobjectData;
        _sim.OnRecvException += OnRecvException;

        Log?.Invoke(this, new LogMessageEventArgs($"SimConnect initialised: {connectionName}"));
    }

    private void OnRecvOpen(SimConnect sender, SIMCONNECT_RECV_OPEN data)
    {
        Log?.Invoke(this, new LogMessageEventArgs("Simulator connected"));
        IsConnected = true;
        ConnectionChanged?.Invoke(this, new SimConnectionChangedEventArgs(true, IsFlightLoaded));

        RegisterDataDefinitions();
        SubscribeToEvents();
    }

    private void OnRecvQuit(SimConnect sender, SIMCONNECT_RECV data)
    {
        Log?.Invoke(this, new LogMessageEventArgs("Simulator closed"));
        StopStream();
        IsConnected = false;
        IsFlightLoaded = false;
        ConnectionChanged?.Invoke(this, new SimConnectionChangedEventArgs(false, false));
    }

    private void OnRecvEvent(SimConnect sender, SIMCONNECT_RECV_EVENT data)
    {
        if (data.uEventID == (uint)Events.SimStart)
        {
            if (!IsFlightLoaded)
            {
                Log?.Invoke(this, new LogMessageEventArgs("Flight activated"));
            }

            IsFlightLoaded = true;
            ConnectionChanged?.Invoke(this, new SimConnectionChangedEventArgs(IsConnected, true));
            StartStream();
        }
        else if (data.uEventID == (uint)Events.SimStop)
        {
            Log?.Invoke(this, new LogMessageEventArgs("Flight ended"));
            IsFlightLoaded = false;
            ConnectionChanged?.Invoke(this, new SimConnectionChangedEventArgs(IsConnected, false));
            StopStream();
        }
    }

    private void OnRecvSimobjectData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        if (data.dwData is not object[] values || values.Length == 0)
        {
            return;
        }

        switch ((Reqs)data.dwRequestID)
        {
            case Reqs.Latitude:
                if (values[0] is double lat)
                {
                    _latitude = lat;
                }
                break;
            case Reqs.Longitude:
                if (values[0] is double lon)
                {
                    _longitude = lon;
                }
                break;
            case Reqs.Altitude:
                if (values[0] is double alt)
                {
                    _altitude = alt;
                }
                break;
            case Reqs.AirspeedIndicated:
                if (values[0] is double ias)
                {
                    _airspeedIndicated = ias;
                }
                break;
            case Reqs.AirspeedTrue:
                if (values[0] is double tas)
                {
                    _airspeedTrue = tas;
                }
                break;
            case Reqs.GroundVelocity:
                if (values[0] is double gs)
                {
                    _groundVelocity = gs;
                }
                break;
            case Reqs.TurbineN1:
                if (values[0] is double n1)
                {
                    _turbineN1 = n1;
                }
                break;
            case Reqs.OnGround:
                if (values[0] is int onGround)
                {
                    _onGround = onGround != 0;
                }
                break;
            case Reqs.EngineCombustion:
                if (values[0] is int combustion)
                {
                    _engineCombustion = combustion != 0;
                }
                break;
            case Reqs.IndicatedAltitude:
                if (values[0] is double indAlt)
                {
                    _indicatedAltitude = indAlt;
                }
                break;
            case Reqs.TransponderCode:
                if (values[0] is int xpdr)
                {
                    _transponderCode = xpdr;
                }
                break;
            case Reqs.AdfActiveFreq:
                if (values[0] is int adfAct)
                {
                    _adfActiveFreq = adfAct;
                }
                break;
            case Reqs.AdfStandbyFreq:
                if (values[0] is double adfStby)
                {
                    _adfStandbyFreq = adfStby;
                }
                break;
        }

        _lastTelemetryTs = DateTimeOffset.UtcNow;
        PublishTelemetry();
    }

    private void PublishTelemetry()
    {
        var telemetry = new SimTelemetry(
            _lastTelemetryTs,
            _latitude,
            _longitude,
            _altitude,
            _indicatedAltitude,
            _airspeedIndicated,
            _airspeedTrue,
            _groundVelocity,
            _turbineN1,
            _onGround,
            _engineCombustion,
            _transponderCode,
            _adfActiveFreq,
            _adfStandbyFreq);

        Telemetry?.Invoke(this, new SimTelemetryEventArgs(telemetry));
    }

    private void OnRecvException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
    {
        Log?.Invoke(this, new LogMessageEventArgs($"SimConnect exception: {data.dwException}"));
    }

    private void RegisterDataDefinitions()
    {
        if (_sim == null || _registered)
        {
            return;
        }

        try
        {
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

            _registered = true;
            Log?.Invoke(this, new LogMessageEventArgs("Data definitions registered"));
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, new LogMessageEventArgs($"Data definition error: {ex.Message}"));
        }
    }

    private void SubscribeToEvents()
    {
        if (_sim == null)
        {
            return;
        }

        try
        {
            _sim.SubscribeToSystemEvent(Events.SimStart, "SimStart");
            _sim.SubscribeToSystemEvent(Events.SimStop, "SimStop");
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, new LogMessageEventArgs($"Event subscription failed: {ex.Message}"));
        }
    }

    private void StartStream()
    {
        if (_sim == null || !_registered || _streamActive)
        {
            return;
        }

        try
        {
            RequestStream(Reqs.Latitude, Defs.Latitude, SIMCONNECT_PERIOD.SECOND);
            RequestStream(Reqs.Longitude, Defs.Longitude, SIMCONNECT_PERIOD.SECOND);
            RequestStream(Reqs.Altitude, Defs.Altitude, SIMCONNECT_PERIOD.SECOND);
            RequestStream(Reqs.AirspeedIndicated, Defs.AirspeedIndicated, SIMCONNECT_PERIOD.SECOND);
            RequestStream(Reqs.AirspeedTrue, Defs.AirspeedTrue, SIMCONNECT_PERIOD.SECOND);
            RequestStream(Reqs.GroundVelocity, Defs.GroundVelocity, SIMCONNECT_PERIOD.SECOND);
            RequestStream(Reqs.TurbineN1, Defs.TurbineN1, SIMCONNECT_PERIOD.SECOND);
            RequestStream(Reqs.OnGround, Defs.OnGround, SIMCONNECT_PERIOD.SECOND);
            RequestStream(Reqs.EngineCombustion, Defs.EngineCombustion, SIMCONNECT_PERIOD.SECOND);
            RequestStream(Reqs.IndicatedAltitude, Defs.IndicatedAltitude, SIMCONNECT_PERIOD.SECOND);
            RequestStream(Reqs.TransponderCode, Defs.TransponderCode, SIMCONNECT_PERIOD.SECOND);
            RequestStream(Reqs.AdfActiveFreq, Defs.AdfActiveFreq, SIMCONNECT_PERIOD.SECOND);
            RequestStream(Reqs.AdfStandbyFreq, Defs.AdfStandbyFreq, SIMCONNECT_PERIOD.SECOND);

            _streamActive = true;
            Log?.Invoke(this, new LogMessageEventArgs("Sim telemetry stream started"));
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, new LogMessageEventArgs($"Failed to start stream: {ex.Message}"));
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
            RequestStream(Reqs.Latitude, Defs.Latitude, SIMCONNECT_PERIOD.NEVER);
            RequestStream(Reqs.Longitude, Defs.Longitude, SIMCONNECT_PERIOD.NEVER);
            RequestStream(Reqs.Altitude, Defs.Altitude, SIMCONNECT_PERIOD.NEVER);
            RequestStream(Reqs.AirspeedIndicated, Defs.AirspeedIndicated, SIMCONNECT_PERIOD.NEVER);
            RequestStream(Reqs.AirspeedTrue, Defs.AirspeedTrue, SIMCONNECT_PERIOD.NEVER);
            RequestStream(Reqs.GroundVelocity, Defs.GroundVelocity, SIMCONNECT_PERIOD.NEVER);
            RequestStream(Reqs.TurbineN1, Defs.TurbineN1, SIMCONNECT_PERIOD.NEVER);
            RequestStream(Reqs.OnGround, Defs.OnGround, SIMCONNECT_PERIOD.NEVER);
            RequestStream(Reqs.EngineCombustion, Defs.EngineCombustion, SIMCONNECT_PERIOD.NEVER);
            RequestStream(Reqs.IndicatedAltitude, Defs.IndicatedAltitude, SIMCONNECT_PERIOD.NEVER);
            RequestStream(Reqs.TransponderCode, Defs.TransponderCode, SIMCONNECT_PERIOD.NEVER);
            RequestStream(Reqs.AdfActiveFreq, Defs.AdfActiveFreq, SIMCONNECT_PERIOD.NEVER);
            RequestStream(Reqs.AdfStandbyFreq, Defs.AdfStandbyFreq, SIMCONNECT_PERIOD.NEVER);
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, new LogMessageEventArgs($"Failed to stop stream: {ex.Message}"));
        }
        finally
        {
            _streamActive = false;
        }
    }

    private void RequestStream(Reqs request, Defs definition, SIMCONNECT_PERIOD period)
    {
        if (_sim == null)
        {
            return;
        }

        _sim.RequestDataOnSimObject(request, definition, SimConnect.SIMCONNECT_OBJECT_ID_USER, period, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
    }

    private void Cleanup()
    {
        StopStream();

        if (_sim != null)
        {
            try
            {
                _sim.OnRecvOpen -= OnRecvOpen;
                _sim.OnRecvQuit -= OnRecvQuit;
                _sim.OnRecvEvent -= OnRecvEvent;
                _sim.OnRecvSimobjectData -= OnRecvSimobjectData;
                _sim.OnRecvException -= OnRecvException;
                _sim.Dispose();
            }
            catch
            {
            }
            finally
            {
                _sim = null;
            }
        }

        if (IsConnected || IsFlightLoaded)
        {
            IsConnected = false;
            IsFlightLoaded = false;
            ConnectionChanged?.Invoke(this, new SimConnectionChangedEventArgs(false, false));
        }
    }
}
