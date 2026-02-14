namespace OpensquawkBridge.Abstractions;

public sealed class SimConnectionChangedEventArgs : EventArgs
{
    public SimConnectionChangedEventArgs(bool isConnected, bool isFlightLoaded)
    {
        IsConnected = isConnected;
        IsFlightLoaded = isFlightLoaded;
    }

    public bool IsConnected { get; }
    public bool IsFlightLoaded { get; }
}

public sealed class SimTelemetryEventArgs : EventArgs
{
    public SimTelemetryEventArgs(SimTelemetry telemetry)
    {
        Telemetry = telemetry;
    }

    public SimTelemetry Telemetry { get; }
}

public sealed class LogMessageEventArgs : EventArgs
{
    public LogMessageEventArgs(string message)
    {
        Message = message;
    }

    public string Message { get; }
}

public sealed record SimTelemetry(
    DateTimeOffset Timestamp,
    double Latitude,
    double Longitude,
    double Altitude,
    double IndicatedAltitude,
    double AirspeedIndicated,
    double AirspeedTrue,
    double GroundVelocity,
    double TurbineN1,
    bool OnGround,
    bool EngineCombustion,
    int TransponderCode,
    int AdfActiveFrequency,
    double AdfStandbyFrequency,
    double VerticalSpeed,
    double PlanePitchDegrees,
    double TurbineN1Engine2,
    bool GearHandlePosition,
    int FlapsHandleIndex,
    bool BrakeParkingPosition,
    bool AutopilotMaster);

public interface ISimConnectAdapter : IDisposable
{
    event EventHandler<LogMessageEventArgs>? Log;
    event EventHandler<SimConnectionChangedEventArgs>? ConnectionChanged;
    event EventHandler<SimTelemetryEventArgs>? Telemetry;

    bool IsConnected { get; }
    bool IsFlightLoaded { get; }

    Task StartAsync(CancellationToken token);
    Task StopAsync();
}
