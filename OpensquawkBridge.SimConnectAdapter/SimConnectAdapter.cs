using System.Collections.Concurrent;
using System.Collections.Generic;
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
        AdfStandbyFreq = 0x500C,
        VerticalSpeed = 0x500D,
        PlanePitch = 0x500E,
        TurbineN1Engine2 = 0x500F,
        GearHandle = 0x5010,
        FlapsIndex = 0x5011,
        ParkingBrake = 0x5012,
        AutopilotMaster = 0x5013
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
        AdfStandbyFreq = 0x510C,
        VerticalSpeed = 0x510D,
        PlanePitch = 0x510E,
        TurbineN1Engine2 = 0x510F,
        GearHandle = 0x5110,
        FlapsIndex = 0x5111,
        ParkingBrake = 0x5112,
        AutopilotMaster = 0x5113
    }

    private enum Events : uint
    {
        SimStart = 0x5200,
        SimStop = 0x5201
    }

    private SimConnect? _sim;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private readonly ConcurrentQueue<SimControlCommand> _pendingCommands = new();
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
    private double _verticalSpeed;
    private double _planePitch;
    private double _turbineN1Engine2;
    private bool _gearHandle;
    private int _flapsIndex;
    private bool _parkingBrake;
    private bool _autopilotMaster;
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
        Log?.Invoke(this, new LogMessageEventArgs($"Starting SimConnect adapter loop (canceled={token.IsCancellationRequested})."));
        _loopTask = Task.Run(() => RunAsync(_loopCts.Token), CancellationToken.None);
        Log?.Invoke(this, new LogMessageEventArgs("SimConnect loop task scheduled."));
        return Task.CompletedTask;
    }

    public Task ApplyCommandsAsync(IReadOnlyCollection<SimControlCommand> commands, CancellationToken token = default)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SimConnectAdapter));
        }

        if (commands.Count == 0)
        {
            return Task.CompletedTask;
        }

        foreach (var command in commands)
        {
            token.ThrowIfCancellationRequested();
            _pendingCommands.Enqueue(command);
        }

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
            Log?.Invoke(this, new LogMessageEventArgs("SimConnect loop task started."));
            while (!token.IsCancellationRequested)
            {
                if (_sim == null)
                {
                    try
                    {
                        InitializeSimConnect();
                    }
                    catch (COMException ex)
                    {
                        Log?.Invoke(this, new LogMessageEventArgs($"SimConnect init failed (HRESULT=0x{ex.HResult:X8}): {ex.Message}. Retrying in 2s."));
                        await Task.Delay(2000, token).ConfigureAwait(false);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        Log?.Invoke(this, new LogMessageEventArgs($"SimConnect init failed: {ex.Message}. Retrying in 2s."));
                        await Task.Delay(2000, token).ConfigureAwait(false);
                        continue;
                    }
                }

                try
                {
                    _sim?.ReceiveMessage();
                }
                catch (COMException ex)
                {
                    Log?.Invoke(this, new LogMessageEventArgs($"SimConnect receive error (HRESULT=0x{ex.HResult:X8}): {ex.Message}"));
                    Cleanup();
                    await Task.Delay(1000, token).ConfigureAwait(false);
                    continue;
                }
                catch (Exception ex)
                {
                    Log?.Invoke(this, new LogMessageEventArgs($"SimConnect loop error: {ex.Message}"));
                    await Task.Delay(1000, token).ConfigureAwait(false);
                }

                try
                {
                    ProcessPendingCommands();
                }
                catch (Exception ex)
                {
                    Log?.Invoke(this, new LogMessageEventArgs($"Failed to process simulator commands: {ex.Message}"));
                }

                await Task.Delay(50, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Log?.Invoke(this, new LogMessageEventArgs("SimConnect loop task ended."));
            Cleanup();
        }
    }

    private void InitializeSimConnect()
    {
        var connectionName = $"OpenSquawkBridge_{Environment.ProcessId}";
        Log?.Invoke(this, new LogMessageEventArgs($"Initializing SimConnect (name={connectionName})."));
        _sim = new SimConnect(connectionName, IntPtr.Zero, 0, null, 0);
        Log?.Invoke(this, new LogMessageEventArgs("SimConnect instance created."));

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
        StartStream();
    }

    private void OnRecvQuit(SimConnect sender, SIMCONNECT_RECV data)
    {
        Log?.Invoke(this, new LogMessageEventArgs("Simulator closed"));
        StopStream();
        DrainPendingCommands();
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
            DrainPendingCommands();
        }
    }

    private void OnRecvSimobjectData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        if (data.dwData is not object[] values || values.Length == 0)
        {
            return;
        }

        if (!IsFlightLoaded)
        {
            IsFlightLoaded = true;
            Log?.Invoke(this, new LogMessageEventArgs("Flight detected via telemetry"));
            ConnectionChanged?.Invoke(this, new SimConnectionChangedEventArgs(IsConnected, true));
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
            case Reqs.VerticalSpeed:
                if (values[0] is double vs)
                {
                    _verticalSpeed = vs;
                }
                break;
            case Reqs.PlanePitch:
                if (values[0] is double pitch)
                {
                    _planePitch = pitch;
                }
                break;
            case Reqs.TurbineN1Engine2:
                if (values[0] is double n1e2)
                {
                    _turbineN1Engine2 = n1e2;
                }
                break;
            case Reqs.GearHandle:
                if (values[0] is int gear)
                {
                    _gearHandle = gear != 0;
                }
                break;
            case Reqs.FlapsIndex:
                if (values[0] is int flaps)
                {
                    _flapsIndex = flaps;
                }
                break;
            case Reqs.ParkingBrake:
                if (values[0] is int pbrake)
                {
                    _parkingBrake = pbrake != 0;
                }
                break;
            case Reqs.AutopilotMaster:
                if (values[0] is int apMaster)
                {
                    _autopilotMaster = apMaster != 0;
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
            _adfStandbyFreq,
            _verticalSpeed,
            _planePitch,
            _turbineN1Engine2,
            _gearHandle,
            _flapsIndex,
            _parkingBrake,
            _autopilotMaster);

        Telemetry?.Invoke(this, new SimTelemetryEventArgs(telemetry));
    }

    private void ProcessPendingCommands()
    {
        if (_pendingCommands.IsEmpty)
        {
            return;
        }

        if (_sim == null || !_registered || !IsConnected || !IsFlightLoaded)
        {
            var dropped = DrainPendingCommands();
            if (dropped > 0 && (_sim != null || IsConnected))
            {
                Log?.Invoke(this, new LogMessageEventArgs($"Dropped {dropped} simulator command(s) because the simulator is not ready."));
            }

            return;
        }

        var applied = 0;
        while (_pendingCommands.TryDequeue(out var command))
        {
            try
            {
                ApplyCommand(command);
                applied++;
            }
            catch (Exception ex)
            {
                Log?.Invoke(this, new LogMessageEventArgs($"Failed to apply command {command.Parameter}: {ex.Message}"));
            }
        }

        if (applied > 0)
        {
            Log?.Invoke(this, new LogMessageEventArgs($"Applied {applied} simulator command(s)."));
        }
    }

    private void ApplyCommand(SimControlCommand command)
    {
        if (double.IsNaN(command.Value) || double.IsInfinity(command.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(command), $"Command value must be finite: {command.Value}");
        }

        switch (command.Parameter)
        {
            case SimControlParameter.TransponderCode:
                var transponderCode = ToRoundedInt(command.Value);
                SendIntData(Defs.TransponderCode, transponderCode);
                _transponderCode = transponderCode;
                break;

            case SimControlParameter.AdfActiveFrequency:
                var adfActiveFrequency = ToRoundedInt(command.Value);
                SendIntData(Defs.AdfActiveFreq, adfActiveFrequency);
                _adfActiveFreq = adfActiveFrequency;
                break;

            case SimControlParameter.AdfStandbyFrequencyHz:
                SendDoubleData(Defs.AdfStandbyFreq, command.Value);
                _adfStandbyFreq = command.Value;
                break;

            case SimControlParameter.GearHandle:
                var gearHandle = ToBoolInt(command.Value);
                SendIntData(Defs.GearHandle, gearHandle);
                _gearHandle = gearHandle != 0;
                break;

            case SimControlParameter.FlapsHandleIndex:
                var flapsHandleIndex = ToRoundedInt(command.Value);
                SendIntData(Defs.FlapsIndex, flapsHandleIndex);
                _flapsIndex = flapsHandleIndex;
                break;

            case SimControlParameter.ParkingBrake:
                var parkingBrake = ToBoolInt(command.Value);
                SendIntData(Defs.ParkingBrake, parkingBrake);
                _parkingBrake = parkingBrake != 0;
                break;

            case SimControlParameter.AutopilotMaster:
                var autopilotMaster = ToBoolInt(command.Value);
                SendIntData(Defs.AutopilotMaster, autopilotMaster);
                _autopilotMaster = autopilotMaster != 0;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Parameter, "Unsupported simulator command.");
        }
    }

    private void SendIntData(Defs definition, int value)
    {
        _sim!.SetDataOnSimObject(definition, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, value);
    }
    SIMCONNECT_DATA_

    private void SendDoubleData(Defs definition, double value)
    {
        _sim!.SetDataOnSimObject(definition, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, value);
    }

    private static int ToBoolInt(double value)
    {
        return value >= 0.5d ? 1 : 0;
    }

    private static int ToRoundedInt(double value)
    {
        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    private int DrainPendingCommands()
    {
        var count = 0;
        while (_pendingCommands.TryDequeue(out _))
        {
            count++;
        }

        return count;
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

            _sim.AddToDataDefinition(Defs.VerticalSpeed, "VERTICAL SPEED", "feet per minute", SIMCONNECT_DATATYPE.FLOAT64, 0, 0);
            _sim.RegisterDataDefineStruct<double>(Defs.VerticalSpeed);

            _sim.AddToDataDefinition(Defs.PlanePitch, "PLANE PITCH DEGREES", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0, 0);
            _sim.RegisterDataDefineStruct<double>(Defs.PlanePitch);

            _sim.AddToDataDefinition(Defs.TurbineN1Engine2, "TURB ENG N1:2", "percent", SIMCONNECT_DATATYPE.FLOAT64, 0, 0);
            _sim.RegisterDataDefineStruct<double>(Defs.TurbineN1Engine2);

            _sim.AddToDataDefinition(Defs.GearHandle, "GEAR HANDLE POSITION", "Bool", SIMCONNECT_DATATYPE.INT32, 0, 0);
            _sim.RegisterDataDefineStruct<int>(Defs.GearHandle);

            _sim.AddToDataDefinition(Defs.FlapsIndex, "FLAPS HANDLE INDEX", "number", SIMCONNECT_DATATYPE.INT32, 0, 0);
            _sim.RegisterDataDefineStruct<int>(Defs.FlapsIndex);

            _sim.AddToDataDefinition(Defs.ParkingBrake, "BRAKE PARKING POSITION", "Bool", SIMCONNECT_DATATYPE.INT32, 0, 0);
            _sim.RegisterDataDefineStruct<int>(Defs.ParkingBrake);

            _sim.AddToDataDefinition(Defs.AutopilotMaster, "AUTOPILOT MASTER", "Bool", SIMCONNECT_DATATYPE.INT32, 0, 0);
            _sim.RegisterDataDefineStruct<int>(Defs.AutopilotMaster);

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

    private void RequestData(Reqs request, Defs definition, SIMCONNECT_PERIOD period)
    {
        if (_sim == null)
        {
            return;
        }

        _sim.RequestDataOnSimObject(
            request,
            definition,
            SimConnect.SIMCONNECT_OBJECT_ID_USER,
            period,
            SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT,
            0,
            0,
            0);
    }

    private void StartStream()
    {
        if (_sim == null || !_registered || _streamActive)
        {
            return;
        }

        try
        {
            RequestData(Reqs.Latitude, Defs.Latitude, SIMCONNECT_PERIOD.SECOND);
            RequestData(Reqs.Longitude, Defs.Longitude, SIMCONNECT_PERIOD.SECOND);
            RequestData(Reqs.Altitude, Defs.Altitude, SIMCONNECT_PERIOD.SECOND);
            RequestData(Reqs.AirspeedIndicated, Defs.AirspeedIndicated, SIMCONNECT_PERIOD.SECOND);
            RequestData(Reqs.AirspeedTrue, Defs.AirspeedTrue, SIMCONNECT_PERIOD.SECOND);
            RequestData(Reqs.GroundVelocity, Defs.GroundVelocity, SIMCONNECT_PERIOD.SECOND);
            RequestData(Reqs.TurbineN1, Defs.TurbineN1, SIMCONNECT_PERIOD.SECOND);
            RequestData(Reqs.OnGround, Defs.OnGround, SIMCONNECT_PERIOD.SECOND);
            RequestData(Reqs.EngineCombustion, Defs.EngineCombustion, SIMCONNECT_PERIOD.SECOND);
            RequestData(Reqs.IndicatedAltitude, Defs.IndicatedAltitude, SIMCONNECT_PERIOD.SECOND);
            RequestData(Reqs.TransponderCode, Defs.TransponderCode, SIMCONNECT_PERIOD.SECOND);
            RequestData(Reqs.AdfActiveFreq, Defs.AdfActiveFreq, SIMCONNECT_PERIOD.SECOND);
            RequestData(Reqs.AdfStandbyFreq, Defs.AdfStandbyFreq, SIMCONNECT_PERIOD.SECOND);
            RequestData(Reqs.VerticalSpeed, Defs.VerticalSpeed, SIMCONNECT_PERIOD.SECOND);
            RequestData(Reqs.PlanePitch, Defs.PlanePitch, SIMCONNECT_PERIOD.SECOND);
            RequestData(Reqs.TurbineN1Engine2, Defs.TurbineN1Engine2, SIMCONNECT_PERIOD.SECOND);
            RequestData(Reqs.GearHandle, Defs.GearHandle, SIMCONNECT_PERIOD.SECOND);
            RequestData(Reqs.FlapsIndex, Defs.FlapsIndex, SIMCONNECT_PERIOD.SECOND);
            RequestData(Reqs.ParkingBrake, Defs.ParkingBrake, SIMCONNECT_PERIOD.SECOND);
            RequestData(Reqs.AutopilotMaster, Defs.AutopilotMaster, SIMCONNECT_PERIOD.SECOND);

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
            RequestData(Reqs.Latitude, Defs.Latitude, SIMCONNECT_PERIOD.NEVER);
            RequestData(Reqs.Longitude, Defs.Longitude, SIMCONNECT_PERIOD.NEVER);
            RequestData(Reqs.Altitude, Defs.Altitude, SIMCONNECT_PERIOD.NEVER);
            RequestData(Reqs.AirspeedIndicated, Defs.AirspeedIndicated, SIMCONNECT_PERIOD.NEVER);
            RequestData(Reqs.AirspeedTrue, Defs.AirspeedTrue, SIMCONNECT_PERIOD.NEVER);
            RequestData(Reqs.GroundVelocity, Defs.GroundVelocity, SIMCONNECT_PERIOD.NEVER);
            RequestData(Reqs.TurbineN1, Defs.TurbineN1, SIMCONNECT_PERIOD.NEVER);
            RequestData(Reqs.OnGround, Defs.OnGround, SIMCONNECT_PERIOD.NEVER);
            RequestData(Reqs.EngineCombustion, Defs.EngineCombustion, SIMCONNECT_PERIOD.NEVER);
            RequestData(Reqs.IndicatedAltitude, Defs.IndicatedAltitude, SIMCONNECT_PERIOD.NEVER);
            RequestData(Reqs.TransponderCode, Defs.TransponderCode, SIMCONNECT_PERIOD.NEVER);
            RequestData(Reqs.AdfActiveFreq, Defs.AdfActiveFreq, SIMCONNECT_PERIOD.NEVER);
            RequestData(Reqs.AdfStandbyFreq, Defs.AdfStandbyFreq, SIMCONNECT_PERIOD.NEVER);
            RequestData(Reqs.VerticalSpeed, Defs.VerticalSpeed, SIMCONNECT_PERIOD.NEVER);
            RequestData(Reqs.PlanePitch, Defs.PlanePitch, SIMCONNECT_PERIOD.NEVER);
            RequestData(Reqs.TurbineN1Engine2, Defs.TurbineN1Engine2, SIMCONNECT_PERIOD.NEVER);
            RequestData(Reqs.GearHandle, Defs.GearHandle, SIMCONNECT_PERIOD.NEVER);
            RequestData(Reqs.FlapsIndex, Defs.FlapsIndex, SIMCONNECT_PERIOD.NEVER);
            RequestData(Reqs.ParkingBrake, Defs.ParkingBrake, SIMCONNECT_PERIOD.NEVER);
            RequestData(Reqs.AutopilotMaster, Defs.AutopilotMaster, SIMCONNECT_PERIOD.NEVER);
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

    private void Cleanup()
    {
        StopStream();
        DrainPendingCommands();

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
