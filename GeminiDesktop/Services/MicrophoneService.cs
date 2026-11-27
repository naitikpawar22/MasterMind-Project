using System.Diagnostics;
using System.Runtime.InteropServices;
using GeminiDesktop.Helpers;
using GeminiDesktop.Models;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GeminiDesktop.Services;

/// <summary>
/// Abstraction over physical Windows microphone capture & device enumeration (strictly physical capture devices, no loopback).
/// </summary>
public interface IMicrophoneBackend : IDisposable
{
    event EventHandler<double>? AudioLevelMeasured;
    event EventHandler<Exception?>? CaptureStoppedUnexpectedly;
    event EventHandler? DevicesTopologyChanged;
    event EventHandler<bool>? ExternalMuteChanged;

    MicrophonePermissionState CheckWindowsPermission();
    IReadOnlyList<MicrophoneDevice> EnumerateCaptureDevices();
    string? ResolveDefaultCaptureDeviceId();
    bool TryGetEndpointMute(string deviceId, out bool isMuted);
    bool TrySetEndpointMute(string deviceId, bool muted);
    void StartCapture(string deviceId);
    void StopCapture();
    void SetMutedSuppression(bool muted);
    void SetPcmSink(Pcm16kSpanHandler? sink);
}

/// <summary>
/// Real Windows WASAPI backend capturing strictly from physical DataFlow.Capture microphone devices.
/// </summary>
public sealed class WasapiMicrophoneBackend : IMicrophoneBackend
{
    private const string MicrophoneConsentKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    private const string NonPackagedConsentKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone\NonPackaged";

    private readonly MMDeviceEnumerator _enumerator;
    private readonly MMDeviceNotificationClient? _notificationClient;
    private readonly WasapiStreamCaptureSession _session;

    private bool _disposed;

    public WasapiMicrophoneBackend()
    {
        _enumerator = new MMDeviceEnumerator();
        _session = new WasapiStreamCaptureSession(
            isLoopback: false,
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
    public event EventHandler<bool>? ExternalMuteChanged;

    public MicrophonePermissionState CheckWindowsPermission()
    {
        try
        {
            if (IsConsentDenied(Registry.LocalMachine, MicrophoneConsentKey) ||
                IsConsentDenied(Registry.CurrentUser, MicrophoneConsentKey) ||
                IsConsentDenied(Registry.CurrentUser, NonPackagedConsentKey))
            {
                return MicrophonePermissionState.DeniedByWindows;
            }

            return MicrophonePermissionState.Allowed;
        }
        catch
        {
            return MicrophonePermissionState.Unknown;
        }
    }

    public IReadOnlyList<MicrophoneDevice> EnumerateCaptureDevices()
    {
        var list = new List<MicrophoneDevice>();
        var defaultId = ResolveDefaultCaptureDeviceId();

        // Strictly DataFlow.Capture (physical microphones/input devices only).
        var endpoints = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        foreach (var device in endpoints)
        {
            using (device)
            {
                var isDefault = string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase);
                list.Add(new MicrophoneDevice(device.ID, device.FriendlyName, isDefault, IsSystemDefaultOption: false));
            }
        }

        return list;
    }

    public string? ResolveDefaultCaptureDeviceId()
    {
        try
        {
            if (_enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
            {
                using var dev = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                return dev.ID;
            }
        }
        catch
        {
            // Fall through to multimedia default.
        }

        try
        {
            if (_enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia))
            {
                using var dev = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                return dev.ID;
            }
        }
        catch
        {
            // No active default capture device.
        }

        return null;
    }

    public bool TryGetEndpointMute(string deviceId, out bool isMuted)
    {
        isMuted = false;
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        try
        {
            using var device = _enumerator.GetDevice(deviceId);
            isMuted = device.AudioEndpointVolume.Mute;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TrySetEndpointMute(string deviceId, bool muted)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        try
        {
            var active = _session.ActiveDevice;
            if (active is not null && string.Equals(active.ID, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                active.AudioEndpointVolume.Mute = muted;
                return true;
            }

            using var device = _enumerator.GetDevice(deviceId);
            device.AudioEndpointVolume.Mute = muted;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void StartCapture(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        StopCapture();

        var device = _enumerator.GetDevice(deviceId);
        _session.Start(device, dev =>
        {
            try
            {
                dev.AudioEndpointVolume.OnVolumeNotification += OnEndpointVolumeNotification;
            }
            catch
            {
                // Some endpoints do not expose hardware volume notifications.
            }
        });
    }

    public void StopCapture()
    {
        _session.Stop(dev =>
        {
            try
            {
                dev.AudioEndpointVolume.OnVolumeNotification -= OnEndpointVolumeNotification;
            }
            catch
            {
                // Ignore unsubscribe issues.
            }
        });
    }

    public void SetMutedSuppression(bool muted) => _session.SetSuppressOutput(muted);

    public void SetPcmSink(Pcm16kSpanHandler? sink) => _session.SetPcmSink(sink);

    private void OnEndpointVolumeNotification(AudioVolumeNotificationData data) =>
        ExternalMuteChanged?.Invoke(this, data.Muted);

    private static bool IsConsentDenied(RegistryKey root, string subKeyPath)
    {
        using var key = root.OpenSubKey(subKeyPath, writable: false);
        var val = key?.GetValue("Value") as string;
        return string.Equals(val, "Deny", StringComparison.OrdinalIgnoreCase);
    }

    private void OnDeviceStateChanged(object? sender, DeviceStateChangedEventArgs e) =>
        DevicesTopologyChanged?.Invoke(this, EventArgs.Empty);

    private void OnDeviceAdded(object? sender, DeviceNotificationEventArgs e) =>
        DevicesTopologyChanged?.Invoke(this, EventArgs.Empty);

    private void OnDeviceRemoved(object? sender, DeviceNotificationEventArgs e) =>
        DevicesTopologyChanged?.Invoke(this, EventArgs.Empty);

    private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e)
    {
        if (e.Flow == DataFlow.Capture)
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
        StopCapture();
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
                // Ignore COM shutdown errors.
            }
        }

        _enumerator.Dispose();
    }
}

/// <summary>
/// Manages physical microphone selection, OS/app permissions, start/stop/mute controls, live level metering,
/// and graceful hot-plug/disconnection handling.
/// </summary>
public interface IMicrophoneService : IDisposable
{
    MicrophoneState CurrentState { get; }

    event EventHandler<MicrophoneState>? StateChanged;

    void Initialize();
    void RefreshDevices(string? notificationStatus = null);
    void StartListening();
    void StopListening();
    void SelectDevice(string deviceId);
    void SetMuted(bool muted);
    void ToggleMute();
    void SetMonitoringEnabled(bool enabled);
    void SetWorkspaceMicrophoneAllowed(bool allowed);
    void SetPcmSink(Pcm16kSpanHandler? sink);
    void OpenWindowsMicrophonePrivacySettings();
}

public sealed class MicrophoneService : IMicrophoneService
{
    public const string SystemDefaultDeviceId = "";
    private const int ErrorAccessDeniedHResult = unchecked((int)0x80070005);

    private readonly IMicrophoneBackend _backend;
    private readonly ISettingsService _settingsService;
    private readonly ILoggingService _loggingService;
    private readonly object _sync = new();

    private MicrophoneState _state = new();
    private string? _activeCaptureEndpointId;
    private bool _didModifyHardwareMute;
    private bool _originalHardwareMuteState;
    private bool _isMuted;
    private bool _isMonitoringEnabled;
    private bool _isWorkspaceAllowed;
    private string _selectedDeviceId = SystemDefaultDeviceId;
    private volatile bool _disposing;
    private bool _disposed;

    public MicrophoneService(
        IMicrophoneBackend backend,
        ISettingsService settingsService,
        ILoggingService loggingService)
    {
        _backend = backend;
        _settingsService = settingsService;
        _loggingService = loggingService;

        _backend.AudioLevelMeasured += OnAudioLevelMeasured;
        _backend.CaptureStoppedUnexpectedly += OnCaptureStoppedUnexpectedly;
        _backend.DevicesTopologyChanged += OnDevicesTopologyChanged;
        _backend.ExternalMuteChanged += OnExternalMuteChanged;

        var settings = _settingsService.CurrentSettings;
        _selectedDeviceId = settings.PreferredMicrophoneId ?? SystemDefaultDeviceId;
        _isMonitoringEnabled = settings.EnableMicrophoneLevelMonitoring;
        _isWorkspaceAllowed = settings.AllowMicrophoneAccess;
    }

    public MicrophoneState CurrentState => _state;

    public event EventHandler<MicrophoneState>? StateChanged;

    public void Initialize() => RefreshDevices();

    public void StartListening() => SetMonitoringEnabled(true);

    public void StopListening() => SetMonitoringEnabled(false);

    public void SetPcmSink(Pcm16kSpanHandler? sink) => _backend.SetPcmSink(sink);

    public void RefreshDevices(string? notificationStatus = null)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            var windowsPerm = _backend.CheckWindowsPermission();
            var rawDevices = _backend.EnumerateCaptureDevices();
            var defaultEndpointId = _backend.ResolveDefaultCaptureDeviceId();

            var uiDevices = new List<MicrophoneDevice>
            {
                new(SystemDefaultDeviceId, BuildSystemDefaultLabel(rawDevices, defaultEndpointId), IsDefault: true, IsSystemDefaultOption: true)
            };
            uiDevices.AddRange(rawDevices);

            var resolvedTargetId = ResolveTargetEndpointId(rawDevices, defaultEndpointId, _selectedDeviceId, out var fallbackNotice);
            var effectiveStatus = notificationStatus ?? fallbackNotice;

            if (string.IsNullOrWhiteSpace(resolvedTargetId))
            {
                StopCaptureLocked();
                _activeCaptureEndpointId = null;
                UpdateStateLocked(new MicrophoneState
                {
                    Devices = uiDevices,
                    SelectedDeviceId = _selectedDeviceId,
                    ActiveDeviceId = string.Empty,
                    ActiveDeviceName = "No microphone detected",
                    PermissionState = ResolveEffectivePermission(windowsPerm),
                    IsWorkspaceMicrophoneAllowed = _isWorkspaceAllowed,
                    IsMonitoring = false,
                    IsMuted = _isMuted,
                    InputLevel = 0.0,
                    IsDeviceAvailable = false,
                    HasError = false,
                    StatusText = effectiveStatus ?? "No active microphone detected"
                });
                return;
            }

            var targetDevice = rawDevices.FirstOrDefault(d =>
                string.Equals(d.Id, resolvedTargetId, StringComparison.OrdinalIgnoreCase));
            var activeName = targetDevice?.FriendlyName ?? "Windows Default Microphone";

            if (_backend.TryGetEndpointMute(resolvedTargetId, out var hardwareMuted))
            {
                if (!string.Equals(_activeCaptureEndpointId, resolvedTargetId, StringComparison.OrdinalIgnoreCase))
                {
                    _isMuted = hardwareMuted;
                    _didModifyHardwareMute = false;
                }
            }

            var effectivePerm = ResolveEffectivePermission(windowsPerm);
            if (effectivePerm == MicrophonePermissionState.DeniedByWindows)
            {
                StopCaptureLocked();
                _activeCaptureEndpointId = resolvedTargetId;
                UpdateStateLocked(new MicrophoneState
                {
                    Devices = uiDevices,
                    SelectedDeviceId = _selectedDeviceId,
                    ActiveDeviceId = resolvedTargetId,
                    ActiveDeviceName = activeName,
                    PermissionState = MicrophonePermissionState.DeniedByWindows,
                    IsWorkspaceMicrophoneAllowed = _isWorkspaceAllowed,
                    IsMonitoring = false,
                    IsMuted = _isMuted,
                    InputLevel = 0.0,
                    IsDeviceAvailable = true,
                    HasError = true,
                    StatusText = "Microphone access denied by Windows Privacy Settings",
                    ErrorMessage = "Windows is currently blocking desktop apps from accessing the microphone. Open Windows Microphone Privacy Settings and enable microphone access."
                });
                return;
            }

            if (!_isMonitoringEnabled)
            {
                StopCaptureLocked();
                _activeCaptureEndpointId = resolvedTargetId;
                UpdateStateLocked(new MicrophoneState
                {
                    Devices = uiDevices,
                    SelectedDeviceId = _selectedDeviceId,
                    ActiveDeviceId = resolvedTargetId,
                    ActiveDeviceName = activeName,
                    PermissionState = effectivePerm,
                    IsWorkspaceMicrophoneAllowed = _isWorkspaceAllowed,
                    IsMonitoring = false,
                    IsMuted = _isMuted,
                    InputLevel = 0.0,
                    IsDeviceAvailable = true,
                    HasError = false,
                    StatusText = effectiveStatus ?? $"Stopped · {activeName}"
                });
                return;
            }

            StartOrMaintainCaptureLocked(uiDevices, resolvedTargetId, activeName, effectivePerm, effectiveStatus);
        }
    }

    public void SelectDevice(string deviceId)
    {
        lock (_sync)
        {
            var normalized = deviceId ?? SystemDefaultDeviceId;
            if (string.Equals(_selectedDeviceId, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            RestoreHardwareMuteIfModifiedLocked();
            _selectedDeviceId = normalized;

            var updated = _settingsService.CurrentSettings.Clone();
            updated.PreferredMicrophoneId = normalized;
            _settingsService.SaveSettings(updated);

            _loggingService.Information("Selected preferred microphone device ID: {DeviceId}",
                string.IsNullOrEmpty(normalized) ? "<SystemDefault>" : normalized);
        }

        RefreshDevices();
    }

    public void SetMuted(bool muted)
    {
        lock (_sync)
        {
            _isMuted = muted;
            _backend.SetMutedSuppression(muted);

            if (!string.IsNullOrWhiteSpace(_activeCaptureEndpointId))
            {
                if (!_didModifyHardwareMute && _backend.TryGetEndpointMute(_activeCaptureEndpointId, out var currentHwMute))
                {
                    _originalHardwareMuteState = currentHwMute;
                    _didModifyHardwareMute = true;
                }

                _backend.TrySetEndpointMute(_activeCaptureEndpointId, muted);
            }

            var current = _state;
            var status = current.IsDeviceAvailable && current.IsMonitoring
                ? (muted ? $"Muted · {current.ActiveDeviceName}" : $"Listening · {current.ActiveDeviceName}")
                : current.StatusText;

            UpdateStateLocked(CopyWith(current,
                isMuted: muted,
                inputLevel: muted ? 0.0 : current.InputLevel,
                statusText: status));

            _loggingService.Information("Microphone mute changed to {Muted}.", muted);
        }
    }

    public void ToggleMute() => SetMuted(!_state.IsMuted);

    public void SetMonitoringEnabled(bool enabled)
    {
        lock (_sync)
        {
            if (_isMonitoringEnabled == enabled)
            {
                return;
            }

            _isMonitoringEnabled = enabled;
            var updated = _settingsService.CurrentSettings.Clone();
            updated.EnableMicrophoneLevelMonitoring = enabled;
            _settingsService.SaveSettings(updated);
        }

        RefreshDevices();
    }

    public void SetWorkspaceMicrophoneAllowed(bool allowed)
    {
        lock (_sync)
        {
            if (_isWorkspaceAllowed == allowed)
            {
                return;
            }

            _isWorkspaceAllowed = allowed;
            var updated = _settingsService.CurrentSettings.Clone();
            updated.AllowMicrophoneAccess = allowed;
            _settingsService.SaveSettings(updated);
        }

        RefreshDevices();
    }

    public void OpenWindowsMicrophonePrivacySettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _loggingService.Warning("Unable to open Windows Microphone Privacy Settings: {Error}", ex.Message);
        }
    }

    /// <summary>
    /// Computes normalized [0.0, 1.0] peak amplitude from raw WASAPI PCM or IEEE float audio frames.
    /// </summary>
    public static double ComputePeakAmplitude(byte[] buffer, int bytesRecorded, WaveFormat format) =>
        AudioProcessingHelper.ComputePeakAmplitude(buffer, bytesRecorded, format);

    private void StartOrMaintainCaptureLocked(
        IReadOnlyList<MicrophoneDevice> uiDevices,
        string resolvedTargetId,
        string activeName,
        MicrophonePermissionState effectivePerm,
        string? statusOverride)
    {
        if (string.Equals(_activeCaptureEndpointId, resolvedTargetId, StringComparison.OrdinalIgnoreCase) && _state.IsMonitoring)
        {
            _backend.SetMutedSuppression(_isMuted);
            UpdateStateLocked(CopyWith(_state,
                devices: uiDevices,
                selectedDeviceId: _selectedDeviceId,
                activeDeviceId: resolvedTargetId,
                activeDeviceName: activeName,
                permissionState: effectivePerm,
                isWorkspaceMicrophoneAllowed: _isWorkspaceAllowed,
                isDeviceAvailable: true,
                hasError: false,
                statusText: statusOverride ?? (_isMuted ? $"Muted · {activeName}" : $"Listening · {activeName}")));
            return;
        }

        try
        {
            _backend.SetMutedSuppression(_isMuted);
            _backend.StartCapture(resolvedTargetId);
            _activeCaptureEndpointId = resolvedTargetId;

            UpdateStateLocked(new MicrophoneState
            {
                Devices = uiDevices,
                SelectedDeviceId = _selectedDeviceId,
                ActiveDeviceId = resolvedTargetId,
                ActiveDeviceName = activeName,
                PermissionState = effectivePerm,
                IsWorkspaceMicrophoneAllowed = _isWorkspaceAllowed,
                IsMonitoring = true,
                IsMuted = _isMuted,
                InputLevel = 0.0,
                IsDeviceAvailable = true,
                HasError = false,
                StatusText = statusOverride ?? (_isMuted ? $"Muted · {activeName}" : $"Listening · {activeName}")
            });

            _loggingService.Information("Physical microphone capture started: {Name}", activeName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || (ex is COMException comEx && comEx.HResult == ErrorAccessDeniedHResult))
        {
            _loggingService.Warning("Windows denied microphone access: {Message}", ex.Message);
            StopCaptureLocked();
            UpdateStateLocked(new MicrophoneState
            {
                Devices = uiDevices,
                SelectedDeviceId = _selectedDeviceId,
                ActiveDeviceId = resolvedTargetId,
                ActiveDeviceName = activeName,
                PermissionState = MicrophonePermissionState.DeniedByWindows,
                IsWorkspaceMicrophoneAllowed = _isWorkspaceAllowed,
                IsMonitoring = false,
                IsMuted = _isMuted,
                InputLevel = 0.0,
                IsDeviceAvailable = true,
                HasError = true,
                StatusText = "Microphone permission required in Windows Settings",
                ErrorMessage = "Access to the microphone was denied by Windows. Check Settings → Privacy & security → Microphone and allow desktop apps."
            });
        }
        catch (Exception ex)
        {
            _loggingService.Warning("Failed to start capture on microphone {Name}: {Message}", activeName, ex.Message);
            StopCaptureLocked();
            UpdateStateLocked(new MicrophoneState
            {
                Devices = uiDevices,
                SelectedDeviceId = _selectedDeviceId,
                ActiveDeviceId = resolvedTargetId,
                ActiveDeviceName = activeName,
                PermissionState = effectivePerm,
                IsWorkspaceMicrophoneAllowed = _isWorkspaceAllowed,
                IsMonitoring = false,
                IsMuted = _isMuted,
                InputLevel = 0.0,
                IsDeviceAvailable = true,
                HasError = true,
                StatusText = $"Microphone unavailable: {activeName}",
                ErrorMessage = $"Could not open microphone '{activeName}': {ex.Message}"
            });
        }
    }

    private void StopCaptureLocked()
    {
        try
        {
            _backend.StopCapture();
        }
        catch
        {
            // Ignore stop failures during cleanup.
        }
    }

    private void RestoreHardwareMuteIfModifiedLocked()
    {
        if (_didModifyHardwareMute && !string.IsNullOrWhiteSpace(_activeCaptureEndpointId))
        {
            _backend.TrySetEndpointMute(_activeCaptureEndpointId, _originalHardwareMuteState);
            _didModifyHardwareMute = false;
            _loggingService.Information("Restored prior Windows microphone mute state.");
        }
    }

    private static string? ResolveTargetEndpointId(
        IReadOnlyList<MicrophoneDevice> activeDevices,
        string? defaultEndpointId,
        string selectedDeviceId,
        out string? fallbackNotice)
    {
        fallbackNotice = null;
        if (activeDevices.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(selectedDeviceId))
            {
                fallbackNotice = "Selected microphone disconnected — no microphones available";
            }

            return null;
        }

        if (string.IsNullOrWhiteSpace(selectedDeviceId))
        {
            return defaultEndpointId ?? activeDevices[0].Id;
        }

        var match = activeDevices.FirstOrDefault(d =>
            string.Equals(d.Id, selectedDeviceId, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match.Id;
        }

        // Selected device was unplugged or disabled; fall back gracefully to Windows default.
        var fallbackId = defaultEndpointId ?? activeDevices[0].Id;
        var fallbackDevice = activeDevices.FirstOrDefault(d =>
            string.Equals(d.Id, fallbackId, StringComparison.OrdinalIgnoreCase)) ?? activeDevices[0];
        fallbackNotice = $"Selected microphone disconnected — using {fallbackDevice.FriendlyName}";
        return fallbackDevice.Id;
    }

    private MicrophonePermissionState ResolveEffectivePermission(MicrophonePermissionState windowsPermission)
    {
        if (windowsPermission == MicrophonePermissionState.DeniedByWindows)
        {
            return MicrophonePermissionState.DeniedByWindows;
        }

        return _isWorkspaceAllowed
            ? MicrophonePermissionState.Allowed
            : MicrophonePermissionState.DisabledByAppSetting;
    }

    private static string BuildSystemDefaultLabel(IReadOnlyList<MicrophoneDevice> activeDevices, string? defaultEndpointId)
    {
        var def = activeDevices.FirstOrDefault(d =>
            string.Equals(d.Id, defaultEndpointId, StringComparison.OrdinalIgnoreCase));
        return def is not null
            ? $"System Default ({def.FriendlyName})"
            : "System Default Microphone";
    }

    private void OnAudioLevelMeasured(object? sender, double level)
    {
        if (_disposing || !Monitor.TryEnter(_sync))
        {
            return;
        }

        try
        {
            if (_disposed || !_state.IsMonitoring)
            {
                return;
            }

            var effective = _isMuted ? 0.0 : level;
            if (Math.Abs(_state.InputLevel - effective) < 0.008 && effective > 0.001)
            {
                return;
            }

            UpdateStateLocked(CopyWith(_state, inputLevel: effective));
        }
        finally
        {
            Monitor.Exit(_sync);
        }
    }

    private void OnCaptureStoppedUnexpectedly(object? sender, Exception? ex)
    {
        _loggingService.Warning("Microphone capture stopped unexpectedly ({Message}); re-checking audio devices.",
            ex?.Message ?? "Device disconnected");
        Task.Run(() => RefreshDevices("Microphone disconnected — re-scanning devices..."));
    }

    private void OnDevicesTopologyChanged(object? sender, EventArgs e) =>
        Task.Run(() => RefreshDevices());

    private void OnExternalMuteChanged(object? sender, bool muted)
    {
        lock (_sync)
        {
            if (_disposed || _isMuted == muted)
            {
                return;
            }

            _isMuted = muted;
            _backend.SetMutedSuppression(muted);
            UpdateStateLocked(CopyWith(_state,
                isMuted: muted,
                inputLevel: muted ? 0.0 : _state.InputLevel,
                statusText: _state.IsDeviceAvailable && _state.IsMonitoring
                    ? (muted ? $"Muted · {_state.ActiveDeviceName}" : $"Listening · {_state.ActiveDeviceName}")
                    : _state.StatusText));
        }
    }

    private void UpdateStateLocked(MicrophoneState next)
    {
        _state = next;
        StateChanged?.Invoke(this, next);
    }

    private static MicrophoneState CopyWith(
        MicrophoneState src,
        IReadOnlyList<MicrophoneDevice>? devices = null,
        string? selectedDeviceId = null,
        string? activeDeviceId = null,
        string? activeDeviceName = null,
        MicrophonePermissionState? permissionState = null,
        bool? isWorkspaceMicrophoneAllowed = null,
        bool? isMonitoring = null,
        bool? isMuted = null,
        double? inputLevel = null,
        bool? isDeviceAvailable = null,
        bool? hasError = null,
        string? statusText = null,
        string? errorMessage = null)
    {
        return new MicrophoneState
        {
            Devices = devices ?? src.Devices,
            SelectedDeviceId = selectedDeviceId ?? src.SelectedDeviceId,
            ActiveDeviceId = activeDeviceId ?? src.ActiveDeviceId,
            ActiveDeviceName = activeDeviceName ?? src.ActiveDeviceName,
            PermissionState = permissionState ?? src.PermissionState,
            IsWorkspaceMicrophoneAllowed = isWorkspaceMicrophoneAllowed ?? src.IsWorkspaceMicrophoneAllowed,
            IsMonitoring = isMonitoring ?? src.IsMonitoring,
            IsMuted = isMuted ?? src.IsMuted,
            InputLevel = inputLevel ?? src.InputLevel,
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
        _backend.ExternalMuteChanged -= OnExternalMuteChanged;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            RestoreHardwareMuteIfModifiedLocked();
            StopCaptureLocked();
            _backend.Dispose();
        }
    }
}
