using System.Diagnostics;
using System.Runtime.InteropServices;
using GeminiDesktop.Helpers;
using GeminiDesktop.Models;
using NAudio.CoreAudioApi;

namespace GeminiDesktop.Services;

/// <summary>
/// Abstraction over Windows WASAPI loopback capture on playback (DataFlow.Render) endpoints.
/// Never reads from physical capture (microphone) endpoints.
/// </summary>
public interface ISystemAudioBackend : IDisposable
{
    event EventHandler<double>? AudioLevelMeasured;
    event EventHandler<Exception?>? CaptureStoppedUnexpectedly;
    event EventHandler? DevicesTopologyChanged;

    string? ResolveDefaultRenderDevice(out string friendlyName);
    void StartLoopbackCapture(string deviceId);
    void StopCapture();
    void SetMutedSuppression(bool muted);
    void SetPcmSink(Pcm16kSpanHandler? sink);
}

/// <summary>
/// Real Windows Core Audio loopback backend capturing directly from the active DataFlow.Render output endpoint
/// (browsers, Google Meet, Zoom, YouTube, media players, etc.) using NAudio WasapiRecorder.WithLoopbackCapture().
/// </summary>
public sealed class WasapiSystemAudioBackend : ISystemAudioBackend
{
    private readonly MMDeviceEnumerator _enumerator;
    private readonly MMDeviceNotificationClient? _notificationClient;
    private readonly WasapiStreamCaptureSession _session;

    private bool _disposed;

    public WasapiSystemAudioBackend()
    {
        _enumerator = new MMDeviceEnumerator();
        _session = new WasapiStreamCaptureSession(
            isLoopback: true,
            onLevel: level => AudioLevelMeasured?.Invoke(this, level),
            onStoppedUnexpectedly: ex => CaptureStoppedUnexpectedly?.Invoke(this, ex));

        try
        {
            _notificationClient = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
            _notificationClient.DeviceStateChanged += OnDeviceStateChanged;
            _notificationClient.DeviceAdded += OnDeviceAdded;
            _notificationClient.DeviceRemoved += OnDeviceRemoved;
            _notificationClient.DefaultDeviceChanged += OnDefaultDeviceChanged;
        }
        catch
        {
            _notificationClient = null;
        }
    }

    public event EventHandler<double>? AudioLevelMeasured;
    public event EventHandler<Exception?>? CaptureStoppedUnexpectedly;
    public event EventHandler? DevicesTopologyChanged;

    public string? ResolveDefaultRenderDevice(out string friendlyName)
    {
        friendlyName = "No playback device detected";

        try
        {
            if (_enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            {
                using var dev = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                friendlyName = dev.FriendlyName;
                return dev.ID;
            }
        }
        catch
        {
            // Fall through to console role or enumeration.
        }

        try
        {
            if (_enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console))
            {
                using var dev = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                friendlyName = dev.FriendlyName;
                return dev.ID;
            }
        }
        catch
        {
            // Fall through to first active render device.
        }

        try
        {
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    friendlyName = device.FriendlyName;
                    return device.ID;
                }
            }
        }
        catch
        {
            // No active render devices.
        }

        return null;
    }

    public void StartLoopbackCapture(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var device = _enumerator.GetDevice(deviceId);
        _session.Start(device);
    }

    public void StopCapture() => _session.Stop();

    public void SetMutedSuppression(bool muted) => _session.SetSuppressOutput(muted);

    public void SetPcmSink(Pcm16kSpanHandler? sink) => _session.SetPcmSink(sink);

    private void OnDeviceStateChanged(object? sender, DeviceStateChangedEventArgs e) =>
        DevicesTopologyChanged?.Invoke(this, EventArgs.Empty);

    private void OnDeviceAdded(object? sender, DeviceNotificationEventArgs e) =>
        DevicesTopologyChanged?.Invoke(this, EventArgs.Empty);

    private void OnDeviceRemoved(object? sender, DeviceNotificationEventArgs e) =>
        DevicesTopologyChanged?.Invoke(this, EventArgs.Empty);

    private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e)
    {
        if (e.Flow == DataFlow.Render)
        {
            DevicesTopologyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.Dispose();

        if (_notificationClient is not null)
        {
            _notificationClient.DeviceStateChanged -= OnDeviceStateChanged;
            _notificationClient.DeviceAdded -= OnDeviceAdded;
            _notificationClient.DeviceRemoved -= OnDeviceRemoved;
            _notificationClient.DefaultDeviceChanged -= OnDefaultDeviceChanged;
            try
            {
                _notificationClient.Dispose();
            }
            catch
            {
                // Ignore COM cleanup errors.
            }
        }

        _enumerator.Dispose();
    }
}

/// <summary>
/// Manages Windows system-audio loopback listening, mute (suppresses capture without muting PC speakers),
/// live audio activity indicators, and hot-plug output switching.
/// </summary>
public interface ISystemAudioService : IDisposable
{
    SystemAudioState CurrentState { get; }

    event EventHandler<SystemAudioState>? StateChanged;

    void Initialize();
    void RefreshDevice(string? notificationStatus = null);
    void StartListening();
    void StopListening();
    void SetMuted(bool muted);
    void ToggleMute();
    void SetPcmSink(Pcm16kSpanHandler? sink);
    void OpenWindowsSoundSettings();
}

public sealed class SystemAudioService : ISystemAudioService
{
    private const int ErrorAccessDeniedHResult = unchecked((int)0x80070005);

    private readonly ISystemAudioBackend _backend;
    private readonly ISettingsService _settingsService;
    private readonly ILoggingService _loggingService;
    private readonly object _sync = new();

    private SystemAudioState _state = new();
    private string? _activeEndpointId;
    private bool _isListeningEnabled;
    private bool _isMuted;
    private volatile bool _disposing;
    private bool _disposed;

    public SystemAudioService(
        ISystemAudioBackend backend,
        ISettingsService settingsService,
        ILoggingService loggingService)
    {
        _backend = backend;
        _settingsService = settingsService;
        _loggingService = loggingService;

        _backend.AudioLevelMeasured += OnAudioLevelMeasured;
        _backend.CaptureStoppedUnexpectedly += OnCaptureStoppedUnexpectedly;
        _backend.DevicesTopologyChanged += OnDevicesTopologyChanged;

        _isListeningEnabled = _settingsService.CurrentSettings.EnableSystemAudioMonitoring;
    }

    public SystemAudioState CurrentState => _state;

    public event EventHandler<SystemAudioState>? StateChanged;

    public void Initialize() => RefreshDevice();

    public void RefreshDevice(string? notificationStatus = null)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            var endpointId = _backend.ResolveDefaultRenderDevice(out var friendlyName);
            if (string.IsNullOrWhiteSpace(endpointId))
            {
                StopCaptureLocked();
                _activeEndpointId = null;
                UpdateStateLocked(new SystemAudioState
                {
                    ActiveDeviceId = string.Empty,
                    ActiveDeviceName = "No playback device detected",
                    IsListening = false,
                    IsMuted = _isMuted,
                    OutputLevel = 0.0,
                    IsDeviceAvailable = false,
                    HasError = false,
                    StatusText = notificationStatus ?? "No active Windows playback output detected"
                });
                return;
            }

            if (!_isListeningEnabled)
            {
                StopCaptureLocked();
                _activeEndpointId = endpointId;
                UpdateStateLocked(new SystemAudioState
                {
                    ActiveDeviceId = endpointId,
                    ActiveDeviceName = friendlyName,
                    IsListening = false,
                    IsMuted = _isMuted,
                    OutputLevel = 0.0,
                    IsDeviceAvailable = true,
                    HasError = false,
                    StatusText = notificationStatus ?? $"Stopped · {friendlyName}"
                });
                return;
            }

            if (string.Equals(_activeEndpointId, endpointId, StringComparison.OrdinalIgnoreCase) && _state.IsListening)
            {
                UpdateStateLocked(CopyWith(_state,
                    activeDeviceId: endpointId,
                    activeDeviceName: friendlyName,
                    isDeviceAvailable: true,
                    hasError: false,
                    statusText: notificationStatus ?? FormatActiveStatus(friendlyName, _isMuted)));
                return;
            }

            try
            {
                _backend.SetMutedSuppression(_isMuted);
                _backend.StartLoopbackCapture(endpointId);
                _activeEndpointId = endpointId;

                UpdateStateLocked(new SystemAudioState
                {
                    ActiveDeviceId = endpointId,
                    ActiveDeviceName = friendlyName,
                    IsListening = true,
                    IsMuted = _isMuted,
                    OutputLevel = 0.0,
                    IsDeviceAvailable = true,
                    HasError = false,
                    StatusText = notificationStatus ?? FormatActiveStatus(friendlyName, _isMuted)
                });

                _loggingService.Information("System audio loopback capture started: {Name}", friendlyName);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || (ex is COMException comEx && comEx.HResult == ErrorAccessDeniedHResult))
            {
                _loggingService.Warning("Windows denied system audio loopback capture: {Message}", ex.Message);
                StopCaptureLocked();
                UpdateStateLocked(new SystemAudioState
                {
                    ActiveDeviceId = endpointId,
                    ActiveDeviceName = friendlyName,
                    IsListening = false,
                    IsMuted = _isMuted,
                    OutputLevel = 0.0,
                    IsDeviceAvailable = true,
                    HasError = true,
                    StatusText = "System audio permission blocked",
                    ErrorMessage = "Windows blocked system audio loopback capture. Verify sound output permissions and audio drivers in Windows Settings."
                });
            }
            catch (Exception ex)
            {
                _loggingService.Warning("Failed to start system audio loopback capture on {Name}: {Message}", friendlyName, ex.Message);
                StopCaptureLocked();
                UpdateStateLocked(new SystemAudioState
                {
                    ActiveDeviceId = endpointId,
                    ActiveDeviceName = friendlyName,
                    IsListening = false,
                    IsMuted = _isMuted,
                    OutputLevel = 0.0,
                    IsDeviceAvailable = true,
                    HasError = true,
                    StatusText = $"System audio unavailable: {friendlyName}",
                    ErrorMessage = $"Could not capture Windows playback audio from '{friendlyName}': {ex.Message}"
                });
            }
        }
    }

    public void StartListening()
    {
        lock (_sync)
        {
            _isListeningEnabled = true;
            PersistMonitoringSettingLocked(true);
        }

        RefreshDevice();
    }

    public void StopListening()
    {
        lock (_sync)
        {
            _isListeningEnabled = false;
            PersistMonitoringSettingLocked(false);
        }

        RefreshDevice("Stopped");
    }

    public void SetMuted(bool muted)
    {
        lock (_sync)
        {
            _isMuted = muted;
            _backend.SetMutedSuppression(muted);

            var current = _state;
            var status = current.IsListening
                ? FormatActiveStatus(current.ActiveDeviceName, muted)
                : (current.IsDeviceAvailable ? $"Stopped · {current.ActiveDeviceName}" : current.StatusText);

            UpdateStateLocked(CopyWith(current,
                isMuted: muted,
                outputLevel: muted ? 0.0 : current.OutputLevel,
                statusText: status));

            _loggingService.Information("System audio capture mute set to {Muted}.", muted);
        }
    }

    public void ToggleMute() => SetMuted(!_state.IsMuted);

    public void SetPcmSink(Pcm16kSpanHandler? sink) => _backend.SetPcmSink(sink);

    public void OpenWindowsSoundSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _loggingService.Warning("Unable to open Windows Sound Settings: {Error}", ex.Message);
        }
    }

    private void PersistMonitoringSettingLocked(bool enabled)
    {
        if (_settingsService.CurrentSettings.EnableSystemAudioMonitoring == enabled)
        {
            return;
        }

        var updated = _settingsService.CurrentSettings.Clone();
        updated.EnableSystemAudioMonitoring = enabled;
        _settingsService.SaveSettings(updated);
    }

    private static string FormatActiveStatus(string deviceName, bool isMuted) =>
        isMuted ? $"Muted (speakers active) · {deviceName}" : $"Listening · {deviceName}";

    private void StopCaptureLocked()
    {
        try
        {
            _backend.StopCapture();
        }
        catch
        {
            // Ignore cleanup stop errors.
        }
    }

    private void OnAudioLevelMeasured(object? sender, double level)
    {
        if (_disposing || !Monitor.TryEnter(_sync))
        {
            return;
        }

        try
        {
            if (_disposed || !_state.IsListening)
            {
                return;
            }

            var effective = _isMuted ? 0.0 : level;
            if (Math.Abs(_state.OutputLevel - effective) < 0.008 && effective > 0.001)
            {
                return;
            }

            UpdateStateLocked(CopyWith(_state, outputLevel: effective));
        }
        finally
        {
            Monitor.Exit(_sync);
        }
    }

    private void OnCaptureStoppedUnexpectedly(object? sender, Exception? ex)
    {
        _loggingService.Warning("System audio loopback stopped unexpectedly ({Message}); switching to current playback device.",
            ex?.Message ?? "Playback device changed");
        Task.Run(() => RefreshDevice("Playback device changed — reconnecting..."));
    }

    private void OnDevicesTopologyChanged(object? sender, EventArgs e) =>
        Task.Run(() => RefreshDevice());

    private void UpdateStateLocked(SystemAudioState next)
    {
        _state = next;
        StateChanged?.Invoke(this, next);
    }

    private static SystemAudioState CopyWith(
        SystemAudioState src,
        string? activeDeviceId = null,
        string? activeDeviceName = null,
        bool? isListening = null,
        bool? isMuted = null,
        double? outputLevel = null,
        bool? isDeviceAvailable = null,
        bool? hasError = null,
        string? statusText = null,
        string? errorMessage = null)
    {
        return new SystemAudioState
        {
            ActiveDeviceId = activeDeviceId ?? src.ActiveDeviceId,
            ActiveDeviceName = activeDeviceName ?? src.ActiveDeviceName,
            IsListening = isListening ?? src.IsListening,
            IsMuted = isMuted ?? src.IsMuted,
            OutputLevel = outputLevel ?? src.OutputLevel,
            IsDeviceAvailable = isDeviceAvailable ?? src.IsDeviceAvailable,
            HasError = hasError ?? src.HasError,
            StatusText = statusText ?? src.StatusText,
            ErrorMessage = hasError == false ? null : (errorMessage ?? src.ErrorMessage)
        };
    }

    public void Dispose()
    {
        _disposing = true;
        _backend.AudioLevelMeasured -= OnAudioLevelMeasured;
        _backend.CaptureStoppedUnexpectedly -= OnCaptureStoppedUnexpectedly;
        _backend.DevicesTopologyChanged -= OnDevicesTopologyChanged;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopCaptureLocked();
            _backend.Dispose();
        }
    }
}
