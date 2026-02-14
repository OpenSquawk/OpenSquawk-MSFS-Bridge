#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DotNetEnv;
using OpensquawkBridge.Abstractions;
using STimer = System.Timers.Timer;

internal sealed class BridgeManager : IDisposable
{
    private readonly HttpClient _http = new();
    private readonly string _configPath;
    private BridgeConfig _config;

    private readonly string _bridgeBaseUrl;
    private readonly string _meUrl;
    private readonly string _statusUrl;
    private readonly string _dataUrl;
    private readonly string? _authToken;
    private readonly int _activeIntervalSec;
    private readonly int _idleIntervalSec;
    private readonly bool _ignoreSimLoadErrors;

    private readonly object _telemetryLock = new();

    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private CancellationTokenSource? _simCts;

    private readonly STimer _activeTimer;
    private readonly STimer _idleTimer;

    private bool _isUserConnected;
    private string? _connectedUserName;

    private SimAdapterHandle? _simHandle;
    private bool _simConnected;
    private bool _flightLoaded;
    private SimTelemetry? _latestTelemetry;
    private DateTimeOffset _latestTelemetryTimestamp;

    private string? _adapterLoadError;
    private bool _disposed;

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
        _meUrl = Environment.GetEnvironmentVariable("BRIDGE_ME_URL")
                  ?? $"{_bridgeBaseUrl}/api/bridge/me";
        _statusUrl = Environment.GetEnvironmentVariable("BRIDGE_STATUS_URL")
                      ?? $"{_bridgeBaseUrl}/api/bridge/status";
        _dataUrl = Environment.GetEnvironmentVariable("SERVER_URL")
                    ?? Environment.GetEnvironmentVariable("BRIDGE_TELEMETRY_URL")
                    ?? Environment.GetEnvironmentVariable("BRIDGE_DATA_URL")
                    ?? $"{_bridgeBaseUrl}/api/bridge/data";
        _authToken = Environment.GetEnvironmentVariable("AUTH_TOKEN");
        _activeIntervalSec = int.TryParse(Environment.GetEnvironmentVariable("ACTIVE_INTERVAL_SEC"), out var active) ? active : 30;
        _idleIntervalSec = int.TryParse(Environment.GetEnvironmentVariable("IDLE_INTERVAL_SEC"), out var idle) ? idle : 120;
        _ignoreSimLoadErrors = IsTruthy(Environment.GetEnvironmentVariable("BRIDGE_IGNORE_SIMCONNECT_LOAD_ERRORS"));

        _http.Timeout = TimeSpan.FromSeconds(10);
        if (!string.IsNullOrWhiteSpace(_authToken))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
        }

        ApplyTokenHeader();

        _activeTimer = new STimer(Math.Max(1, _activeIntervalSec) * 1000)
        {
            AutoReset = false
        };
        _activeTimer.Elapsed += async (_, __) => await OnActiveTimerAsync().ConfigureAwait(false);

        _idleTimer = new STimer(Math.Max(1, _idleIntervalSec) * 1000)
        {
            AutoReset = false
        };
        _idleTimer.Elapsed += async (_, __) => await OnIdleTimerAsync().ConfigureAwait(false);
    }

    public string Token => _config.Token;
    public bool IsUserConnected => _isUserConnected;
    public string? ConnectedUserName => _connectedUserName;
    public bool SimConnected => _simConnected;
    public bool FlightLoaded => _flightLoaded;

    public async Task InitializeAsync()
    {
        LogMessage("OpenSquawk Bridge ready.");
        LogMessage($"Status endpoint: {_statusUrl}");
        LogMessage($"Data endpoint: {_dataUrl}");
        LogMessage($"Intervals – active: {_activeIntervalSec}s, idle: {_idleIntervalSec}s");

        if (_ignoreSimLoadErrors)
        {
            LogMessage("⚠️ BRIDGE_IGNORE_SIMCONNECT_LOAD_ERRORS is enabled – the bridge will continue even if SimConnect cannot be loaded.");
        }

        if (string.IsNullOrWhiteSpace(_config.Token))
        {
            _config.Token = GenerateToken();
            _config.CreatedAt = DateTimeOffset.UtcNow;
            BridgeConfigService.Save(_configPath, _config);
            ApplyTokenHeader();
            LogMessage("Generated new bridge token. Use the browser login to link your account.");
            TokenChanged?.Invoke(this, EventArgs.Empty);
            OpenLoginPage();
        }
        else
        {
            TokenChanged?.Invoke(this, EventArgs.Empty);
        }

        StartLoginPolling();
        await CheckConnectionAsync(force: true).ConfigureAwait(false);
        await SendIdleHeartbeatAsync().ConfigureAwait(false);
        RestartIdleTimer();
    }

    public void OpenLoginPage()
    {
        if (string.IsNullOrWhiteSpace(_config.Token))
        {
            LogMessage("⚠️ Cannot open login page without a token.");
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
            LogMessage("🌐 Opened the browser for login.");
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Failed to launch browser: {ex.Message}");
        }
    }

    public async Task ResetTokenAsync()
    {
        await StopSimAsync().ConfigureAwait(false);

        _config.Token = GenerateToken();
        _config.CreatedAt = DateTimeOffset.UtcNow;
        BridgeConfigService.Save(_configPath, _config);
        ApplyTokenHeader();

        _isUserConnected = false;
        _connectedUserName = null;
        UserStatusChanged?.Invoke(this, new UserStatusChangedEventArgs(false, null, _config.Token));

        LogMessage("🔁 Token reset. Please login again in the browser.");
        TokenChanged?.Invoke(this, EventArgs.Empty);
        OpenLoginPage();
        await CheckConnectionAsync(force: true).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try { _activeTimer.Stop(); } catch { }
        try { _idleTimer.Stop(); } catch { }
        _activeTimer.Dispose();
        _idleTimer.Dispose();

        if (_pollCts != null)
        {
            try { _pollCts.Cancel(); } catch { }
        }

        try
        {
            _pollTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        _pollTask = null;
        _pollCts?.Dispose();
        _pollCts = null;

        if (_simCts != null)
        {
            try { _simCts.Cancel(); } catch { }
            _simCts.Dispose();
            _simCts = null;
        }

        DetachAdapter(stop: true);

        _http.Dispose();
    }

    private void StartLoginPolling()
    {
        if (_pollTask != null)
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
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                await CheckConnectionAsync().ConfigureAwait(false);
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
            var user = await FetchUserAsync().ConfigureAwait(false);
            var connected = user != null;
            var userName = user?.UserName;

            var changed = connected != _isUserConnected
                           || !string.Equals(userName, _connectedUserName, StringComparison.OrdinalIgnoreCase);

            if (changed || force)
            {
                _isUserConnected = connected;
                _connectedUserName = userName;

                UserStatusChanged?.Invoke(this, new UserStatusChangedEventArgs(connected, userName, _config.Token));

                if (connected)
                {
                    LogMessage(userName != null ? $"✅ Connected as {userName}" : "✅ User connected");
                    await StartSimAsync().ConfigureAwait(false);
                }
                else
                {
                    LogMessage("ℹ️ Waiting for login");
                    await StopSimAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Failed to check login status: {ex.Message}");
        }
    }

    private async Task StartSimAsync()
    {
        if (_simHandle != null)
        {
            return;
        }

        if (!SimAdapterLoader.TryLoad(out var handle, out var error))
        {
            _adapterLoadError = error?.Message;
            LogMessage($"⚠️ Unable to load SimConnect adapter: {_adapterLoadError ?? "unknown error"}.");

            if (error != null && SimAdapterLoader.IsSimConnectLoadFailure(error))
            {
                if (_ignoreSimLoadErrors)
                {
                    LogMessage("Continuing without SimConnect (override enabled).");
                }
                else
                {
                    LogMessage("Set BRIDGE_IGNORE_SIMCONNECT_LOAD_ERRORS=1 to ignore this error.");
                }
            }

            return;
        }

        _adapterLoadError = null;
        _simHandle = handle;
        _simHandle.Adapter.Log += OnAdapterLog;
        _simHandle.Adapter.ConnectionChanged += OnAdapterConnectionChanged;
        _simHandle.Adapter.Telemetry += OnAdapterTelemetry;

        _simCts = new CancellationTokenSource();

        try
        {
            await _simHandle.Adapter.StartAsync(_simCts.Token).ConfigureAwait(false);
            LogMessage("SimConnect adapter started.");
        }
        catch (Exception ex)
        {
            HandleAdapterFailure(ex);
        }
    }

    private async Task StopSimAsync()
    {
        StopActiveTimer();
        RestartIdleTimer();

        if (_simHandle == null)
        {
            return;
        }

        try
        {
            _simCts?.Cancel();
            await _simHandle.Adapter.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogMessage($"⚠️ Error while stopping SimConnect adapter: {ex.Message}");
        }
        finally
        {
            _simCts?.Dispose();
            _simCts = null;
            DetachAdapter(stop: false);
        }
    }

    private void HandleAdapterFailure(Exception ex)
    {
        _adapterLoadError = ex.Message;
        LogMessage($"⚠️ SimConnect failed: {ex.Message}");

        var loadFailure = SimAdapterLoader.IsSimConnectLoadFailure(ex);
        if (loadFailure)
        {
            if (_ignoreSimLoadErrors)
            {
                LogMessage("BRIDGE_IGNORE_SIMCONNECT_LOAD_ERRORS is enabled – running without simulator.");
            }
            else
            {
                LogMessage("Set BRIDGE_IGNORE_SIMCONNECT_LOAD_ERRORS=1 to continue without the simulator.");
            }
        }

        if (_simCts != null)
        {
            try { _simCts.Cancel(); } catch { }
            _simCts.Dispose();
            _simCts = null;
        }

        DetachAdapter(stop: false);
    }

    private void DetachAdapter(bool stop)
    {
        if (_simHandle == null)
        {
            if (_simConnected || _flightLoaded)
            {
                _simConnected = false;
                _flightLoaded = false;
                RaiseSimStatusChanged();
            }
            return;
        }

        try
        {
            if (stop)
            {
                _simHandle.Adapter.StopAsync().GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            LogMessage($"⚠️ Error stopping SimConnect adapter: {ex.Message}");
        }
        finally
        {
            try { _simHandle.Adapter.Log -= OnAdapterLog; } catch { }
            try { _simHandle.Adapter.ConnectionChanged -= OnAdapterConnectionChanged; } catch { }
            try { _simHandle.Adapter.Telemetry -= OnAdapterTelemetry; } catch { }

            try { _simHandle.Dispose(); } catch { }
            _simHandle = null;

            if (_simConnected || _flightLoaded)
            {
                _simConnected = false;
                _flightLoaded = false;
                RaiseSimStatusChanged();
            }
        }
    }

    private void OnAdapterLog(object? sender, LogMessageEventArgs e)
    {
        LogMessage(e.Message);
    }

    private void OnAdapterConnectionChanged(object? sender, SimConnectionChangedEventArgs e)
    {
        _simConnected = e.IsConnected;
        _flightLoaded = e.IsFlightLoaded;
        RaiseSimStatusChanged();
        _ = SendStatusUpdateAsync();

        if (_flightLoaded)
        {
            StopIdleTimer();
            RestartActiveTimer();
            _ = SendActiveTickAsync();
        }
        else
        {
            StopActiveTimer();
            RestartIdleTimer();
            _ = SendIdleHeartbeatAsync();
        }
    }

    private void OnAdapterTelemetry(object? sender, SimTelemetryEventArgs e)
    {
        lock (_telemetryLock)
        {
            _latestTelemetry = e.Telemetry;
            _latestTelemetryTimestamp = e.Telemetry.Timestamp;
        }
    }

    private async Task OnActiveTimerAsync()
    {
        try
        {
            await SendActiveTickAsync().ConfigureAwait(false);
        }
        finally
        {
            if (!_disposed && _flightLoaded)
            {
                RestartActiveTimer();
            }
        }
    }

    private async Task OnIdleTimerAsync()
    {
        try
        {
            await SendIdleHeartbeatAsync().ConfigureAwait(false);
        }
        finally
        {
            if (!_disposed)
            {
                RestartIdleTimer();
            }
        }
    }

    private void RestartActiveTimer()
    {
        try
        {
            _activeTimer.Stop();
            _activeTimer.Interval = Math.Max(1, _activeIntervalSec) * 1000;
            _activeTimer.Start();
        }
        catch
        {
        }
    }

    private void StopActiveTimer()
    {
        try { _activeTimer.Stop(); } catch { }
    }

    private void RestartIdleTimer()
    {
        try
        {
            _idleTimer.Stop();
            _idleTimer.Interval = Math.Max(1, _idleIntervalSec) * 1000;
            _idleTimer.Start();
        }
        catch
        {
        }
    }

    private void StopIdleTimer()
    {
        try { _idleTimer.Stop(); } catch { }
    }

    private async Task SendActiveTickAsync()
    {
        try
        {
            if (!_simConnected || !_flightLoaded)
            {
                LogMessage("Skipping active tick because the simulator is not ready.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_config.Token))
            {
                LogMessage("Skipping active tick because no token is available.");
                return;
            }

            SimTelemetry? telemetry;
            DateTimeOffset timestamp;

            lock (_telemetryLock)
            {
                telemetry = _latestTelemetry;
                timestamp = _latestTelemetryTimestamp;
            }

            if (telemetry == null)
            {
                LogMessage("No telemetry available yet – waiting for data.");
                return;
            }

            if (DateTimeOffset.UtcNow - timestamp > TimeSpan.FromSeconds(10))
            {
                LogMessage("Telemetry data is stale – skipping active tick.");
                return;
            }

            if (!IsValidCoordinate(telemetry.Latitude, -90, 90) || !IsValidCoordinate(telemetry.Longitude, -180, 180))
            {
                LogMessage($"Invalid coordinates lat={telemetry.Latitude:F6}, lon={telemetry.Longitude:F6}");
                return;
            }

            var payload = new
            {
                token = _config.Token,
                status = "active",
                ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                latitude = Math.Round(telemetry.Latitude, 6),
                longitude = Math.Round(telemetry.Longitude, 6),
                altitude_ft_true = Math.Round(telemetry.Altitude, 0),
                altitude_ft_indicated = Math.Round(telemetry.IndicatedAltitude, 0),
                ias_kt = Math.Round(telemetry.AirspeedIndicated, 1),
                tas_kt = Math.Round(telemetry.AirspeedTrue, 1),
                groundspeed_kt = Math.Round(telemetry.GroundVelocity * 1.943844, 1),
                on_ground = telemetry.OnGround,
                eng_on = telemetry.EngineCombustion || telemetry.TurbineN1 > 5,
                n1_pct = Math.Round(telemetry.TurbineN1, 1),
                transponder_code = telemetry.TransponderCode,
                adf_active_freq = telemetry.AdfActiveFrequency,
                adf_standby_freq_hz = Math.Round(telemetry.AdfStandbyFrequency, 0),
                vertical_speed_fpm = Math.Round(telemetry.VerticalSpeed, 0),
                pitch_deg = Math.Round(telemetry.PlanePitchDegrees, 1),
                n1_pct_2 = Math.Round(telemetry.TurbineN1Engine2, 1),
                gear_handle = telemetry.GearHandlePosition,
                flaps_index = telemetry.FlapsHandleIndex,
                parking_brake = telemetry.BrakeParkingPosition,
                autopilot_master = telemetry.AutopilotMaster
            };

            LogMessage($"Active tick lat={payload.latitude:F6} lon={payload.longitude:F6} alt={payload.altitude_ft_true:F0}ft gs={payload.groundspeed_kt:F1}kt");

            await SendStatusUpdateAsync().ConfigureAwait(false);
            await PostJsonAsync(_dataUrl, payload, "Telemetry").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Active tick failed: {ex.Message}");
        }
    }

    private async Task SendIdleHeartbeatAsync()
    {
        try
        {
            await SendStatusUpdateAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Idle heartbeat failed: {ex.Message}");
        }
    }

    private Task SendStatusUpdateAsync()
    {
        if (string.IsNullOrWhiteSpace(_config.Token))
        {
            return Task.CompletedTask;
        }

        var payload = new
        {
            token = _config.Token,
            simConnected = _simConnected,
            flightActive = _flightLoaded
        };

        return PostJsonAsync(_statusUrl, payload, "Status");
    }

    private async Task PostJsonAsync(string? url, object payload, string context)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            LogMessage($"⚠️ No {context} URL configured.");
            return;
        }

        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var response = await _http.PostAsync(url, content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogMessage($"⚠️ {context} POST failed: {(int)response.StatusCode} {response.ReasonPhrase}");
            }
        }
        catch (Exception ex)
        {
            LogMessage($"❌ {context} POST error: {ex.Message}");
        }
    }

    private async Task<BridgeUser?> FetchUserAsync()
    {
        if (string.IsNullOrWhiteSpace(_meUrl))
        {
            LogMessage("⚠️ No user endpoint configured.");
            return null;
        }

        var separator = _meUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var url = $"{_meUrl}{separator}token={Uri.EscapeDataString(_config.Token)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        using var response = await _http.SendAsync(request).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
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

        return TryGetString(element, "username")
               ?? TryGetString(element, "userName")
               ?? TryGetString(element, "name")
               ?? TryGetString(element, "displayName")
               ?? TryGetString(element, "email");
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            return property.GetString();
        }

        return null;
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

    private static bool IsTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
               || value.Equals("true", StringComparison.OrdinalIgnoreCase)
               || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
               || value.Equals("on", StringComparison.OrdinalIgnoreCase)
               || value.Equals("enabled", StringComparison.OrdinalIgnoreCase);
    }

    private static string GenerateToken()
    {
        Span<byte> buffer = stackalloc byte[32];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToBase64String(buffer).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool IsValidCoordinate(double value, double min, double max)
    {
        return !double.IsNaN(value)
               && !double.IsInfinity(value)
               && value >= min
               && value <= max;
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
