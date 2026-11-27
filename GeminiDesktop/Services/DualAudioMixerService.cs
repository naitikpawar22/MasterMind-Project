using System.Diagnostics;
using System.Runtime.InteropServices;
using GeminiDesktop.Helpers;
using GeminiDesktop.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GeminiDesktop.Services;

/// <summary>
/// Abstraction over virtual audio playback rendering so mixed System Audio (Teams/Zoom/Meet) + Microphone audio
/// feeds a signed Windows virtual microphone driver (CABLE Input -> CABLE Output) without bypassing browser security.
/// </summary>
public interface IDualAudioMixerBackend : IDisposable
{
    event EventHandler<Exception?>? RoutingStoppedUnexpectedly;
    event EventHandler? DevicesTopologyChanged;

    IReadOnlyList<VirtualAudioOutputDevice> EnumerateVirtualOutputs();
    WaveFormat ResolveOutputWaveFormat(string deviceId);
    void StartRouting(string deviceId, IWaveProvider mixedWaveProvider, out string friendlyName, out string pairedCaptureHint);
    void StopRouting();
}

/// <summary>
/// Real Windows WASAPI virtual-cable playback backend using NAudio WasapiPlayerBuilder.
/// Refuses physical speakers/headphones to prevent acoustic or loopback feedback loops.
/// </summary>
public sealed class WasapiDualAudioMixerBackend : IDualAudioMixerBackend
{
    private readonly MMDeviceEnumerator _enumerator;
    private readonly MMDeviceNotificationClient? _notificationClient;
    private readonly object _lock = new();

    private MMDevice? _activeDevice;
    private WasapiPlayer? _player;
    private volatile bool _routing;
    private bool _disposed;

    public WasapiDualAudioMixerBackend()
    {
        _enumerator = new MMDeviceEnumerator();

        try
        {
            _notificationClient = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
            _notificationClient.DeviceStateChanged += OnDeviceStateChanged;
            _notificationClient.DeviceAdded += OnDeviceAdded;
            _notificationClient.DeviceRemoved += OnDeviceRemoved;
        }
        catch
        {
            _notificationClient = null;
        }
    }

    public event EventHandler<Exception?>? RoutingStoppedUnexpectedly;
    public event EventHandler? DevicesTopologyChanged;

    public IReadOnlyList<VirtualAudioOutputDevice> EnumerateVirtualOutputs()
    {
        if (_disposed)
        {
            return [];
        }

        var list = new List<VirtualAudioOutputDevice>();
        try
        {
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    if (!VirtualAudioDriverCatalog.IsVirtualAudioRenderSink(device.FriendlyName))
                    {
                        continue;
                    }

                    // Skip 16-channel CABLE In 16ch if 2-channel CABLE Input exists cleanly, unless ordered secondary
                    var hint = VirtualAudioDriverCatalog.InferPairedCaptureMicHint(device.FriendlyName);
                    list.Add(new VirtualAudioOutputDevice(device.ID, device.FriendlyName, hint));
                }
            }
        }
        catch
        {
            // Return discovered virtual endpoints so far.
        }

        return list
            .OrderByDescending(d => d.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
            .ThenBy(d => d.FriendlyName.Contains("16ch", StringComparison.OrdinalIgnoreCase))
            .ThenBy(d => d.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public WaveFormat ResolveOutputWaveFormat(string deviceId)
    {
        try
        {
            using var dev = _enumerator.GetDevice(deviceId);
            using var ac = dev.CreateAudioClient();
            return ac.MixFormat;
        }
        catch
        {
            return WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        }
    }

    public void StartRouting(string deviceId, IWaveProvider mixedWaveProvider, out string friendlyName, out string pairedCaptureHint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(mixedWaveProvider);

        StopRouting();

        lock (_lock)
        {
            if (_disposed)
            {
                friendlyName = string.Empty;
                pairedCaptureHint = string.Empty;
                return;
            }

            var device = _enumerator.GetDevice(deviceId);
            try
            {
                if (device.DataFlow != DataFlow.Render || !VirtualAudioDriverCatalog.IsVirtualAudioRenderSink(device.FriendlyName))
                {
                    throw new InvalidOperationException(
                        $"Refusing to route mixed microphone + system audio to '{device.FriendlyName}' because it is not a signed virtual audio cable output device (prevents feedback loop).");
                }

                friendlyName = device.FriendlyName;
                pairedCaptureHint = VirtualAudioDriverCatalog.InferPairedCaptureMicHint(friendlyName);

                var player = BuildPlayerWithFallback(device, mixedWaveProvider);
                _activeDevice = device;
                device = null;
                _player = player;
                _routing = true;
                player.Play();
            }
            catch
            {
                device?.Dispose();
                throw;
            }
        }
    }

    private WasapiPlayer BuildPlayerWithFallback(MMDevice device, IWaveProvider provider)
    {
        try
        {
            var player = new WasapiPlayerBuilder()
                .WithDevice(device)
                .WithSharedMode()
                .WithPollingSync()
                .WithLatency(40)
                .Build();
            player.PlaybackStopped += OnPlaybackStopped;
            player.Init(provider);
            return player;
        }
        catch
        {
            var player = new WasapiPlayerBuilder()
                .WithDevice(device)
                .WithSharedMode()
                .WithEventSync()
                .WithLatency(50)
                .Build();
            player.PlaybackStopped += OnPlaybackStopped;
            player.Init(provider);
            return player;
        }
    }

    public void StopRouting()
    {
        _routing = false;
        WasapiPlayer? oldPlayer;
        MMDevice? oldDevice;

        lock (_lock)
        {
            oldPlayer = _player;
            _player = null;
            oldDevice = _activeDevice;
            _activeDevice = null;
        }

        if (oldPlayer is not null)
        {
            oldPlayer.PlaybackStopped -= OnPlaybackStopped;
            try
            {
                oldPlayer.Stop();
            }
            catch
            {
                // Ignore stop errors if driver removed.
            }

            try
            {
                oldPlayer.Dispose();
            }
            catch
            {
                // Ignore COM teardown issues.
            }
        }

        oldDevice?.Dispose();
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null && _routing)
        {
            RoutingStoppedUnexpectedly?.Invoke(this, e.Exception);
        }
    }

    private void OnDeviceStateChanged(object? sender, DeviceStateChangedEventArgs e) =>
        DevicesTopologyChanged?.Invoke(this, EventArgs.Empty);

    private void OnDeviceAdded(object? sender, DeviceNotificationEventArgs e) =>
        DevicesTopologyChanged?.Invoke(this, EventArgs.Empty);

    private void OnDeviceRemoved(object? sender, DeviceNotificationEventArgs e) =>
        DevicesTopologyChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopRouting();

        if (_notificationClient is not null)
        {
            _notificationClient.DeviceStateChanged -= OnDeviceStateChanged;
            _notificationClient.DeviceAdded -= OnDeviceAdded;
            _notificationClient.DeviceRemoved -= OnDeviceRemoved;
            try
            {
                _notificationClient.Dispose();
            }
            catch
            {
                // Ignore COM teardown errors.
            }
        }

        _enumerator.Dispose();
    }
}

/// <summary>
/// Mixes live physical Microphone and Windows System Audio (Teams/Meet/Zoom) via NAudio MixingSampleProvider
/// into CABLE Input (VB-Audio Virtual Cable), with an automatic 400ms Echo/Feedback Guard driven by WebView2 audio playback.
/// </summary>
public interface IDualAudioMixerService : IDisposable
{
    MixedAudioState CurrentState { get; }

    event EventHandler<MixedAudioState>? StateChanged;

    void Initialize();
    void RefreshDevices(string? notificationStatus = null);
    void SetEnabled(bool enabled);
    void SelectOutputDevice(string deviceId);
    void SetMicrophoneMixVolume(int percent);
    void SetSystemAudioMixVolume(int percent);
    void ToggleMicrophoneMixMute();
    void ToggleSystemAudioMixMute();
    void OpenVirtualAudioDriverWebsite();
    void OpenWindowsSoundSettings();
}

public sealed class DualAudioMixerService : IDualAudioMixerService
{
    public const string AutoVirtualCableId = "";
    private const string VirtualAudioDownloadUrl = "https://vb-audio.com/Cable/";
    private const int EchoGuardReleaseDelayMs = 400;
    private const int ErrorAccessDeniedHResult = unchecked((int)0x80070005);

    private readonly IDualAudioMixerBackend _backend;
    private readonly IMicrophoneService _microphoneService;
    private readonly ISystemAudioService _systemAudioService;
    private readonly IGeminiBrowserService _browserService;
    private readonly ISettingsService _settingsService;
    private readonly ILoggingService _loggingService;
    private readonly object _sync = new();
    private readonly Timer _topologyDebounceTimer;
    private readonly Timer _echoGuardReleaseTimer;

    private SynchronizedMixedAudioWaveProvider? _activeProvider;
    private MixedAudioState _state = new();
    private string _selectedOutputId = AutoVirtualCableId;
    private string? _activeOutputEndpointId;
    private bool _isEnabled;
    private int _micVolumePercent = 100;
    private int _sysVolumePercent = 100;
    private bool _isMicMixMuted;
    private bool _isSysMixMuted;
    private bool _isGeminiPlayingAudio;
    private bool _isEchoGuardActive;
    private volatile bool _disposing;
    private bool _disposed;

    public DualAudioMixerService(
        IDualAudioMixerBackend backend,
        IMicrophoneService microphoneService,
        ISystemAudioService systemAudioService,
        IGeminiBrowserService browserService,
        ISettingsService settingsService,
        ILoggingService loggingService)
    {
        _backend = backend;
        _microphoneService = microphoneService;
        _systemAudioService = systemAudioService;
        _browserService = browserService;
        _settingsService = settingsService;
        _loggingService = loggingService;

        _topologyDebounceTimer = new Timer(_ => RefreshDevices(), null, Timeout.Infinite, Timeout.Infinite);
        _echoGuardReleaseTimer = new Timer(OnEchoGuardReleaseElapsed, null, Timeout.Infinite, Timeout.Infinite);

        _backend.RoutingStoppedUnexpectedly += OnRoutingStoppedUnexpectedly;
        _backend.DevicesTopologyChanged += OnDevicesTopologyChanged;
        _browserService.DocumentPlayingAudioChanged += OnGeminiDocumentPlayingAudioChanged;

        var settings = _settingsService.CurrentSettings;
        _isEnabled = settings.EnableGeminiMixedAudioRouting;
        _selectedOutputId = settings.PreferredMixedOutputDeviceId ?? AutoVirtualCableId;
        _micVolumePercent = Math.Clamp(settings.MixedMicrophoneVolumePercent, 0, 150);
        _sysVolumePercent = Math.Clamp(settings.MixedSystemAudioVolumePercent, 0, 150);
    }

    public MixedAudioState CurrentState => _state;

    public event EventHandler<MixedAudioState>? StateChanged;

    public void Initialize()
    {
        _microphoneService.SetMonitorFloatSink(OnMicrophoneFloat16k);
        _systemAudioService.SetMonitorFloatSink(OnSystemAudioFloat16k);
        RefreshDevices();
    }

    public void RefreshDevices(string? notificationStatus = null)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            var virtualOutputs = _backend.EnumerateVirtualOutputs();
            var isDriverInstalled = virtualOutputs.Count > 0;

            if (!isDriverInstalled)
            {
                StopRoutingLocked();
                _activeOutputEndpointId = null;

                var depMessage =
                    "Gemini inside WebView2 reads audio from a real Windows Microphone device. " +
                    "Install VB-Audio Virtual Cable (CABLE Input / CABLE Output) and select 'CABLE Output' for Gemini to hear Microphone + System Audio (Teams).";

                UpdateStateLocked(new MixedAudioState
                {
                    IsEnabled = _isEnabled,
                    IsStreaming = false,
                    IsDriverInstalled = false,
                    AvailableOutputs = virtualOutputs,
                    SelectedOutputDeviceId = _selectedOutputId,
                    ActiveOutputDeviceName = "No signed virtual audio driver detected",
                    PairedCaptureMicHint = "CABLE Output (VB-Audio Virtual Cable)",
                    MicrophoneVolumePercent = _micVolumePercent,
                    SystemAudioVolumePercent = _sysVolumePercent,
                    IsMicrophoneMixMuted = _isMicMixMuted,
                    IsSystemAudioMixMuted = _isSysMixMuted,
                    IsEchoGuardActive = false,
                    MixedOutputLevel = 0.0,
                    StatusText = notificationStatus ?? (_isEnabled
                        ? "Virtual Audio Driver required (CABLE Output missing)"
                        : "Virtual Audio Driver not installed"),
                    DependencyMessage = depMessage,
                    ErrorMessage = _isEnabled
                        ? "Cannot stream mixed audio to Gemini yet: no VB-Audio Virtual Cable or VoiceMeeter driver is installed on Windows."
                        : null
                });
                return;
            }

            var target = ResolveTargetDevice(virtualOutputs, _selectedOutputId, out var fallbackNotice);
            var effectiveStatus = notificationStatus ?? fallbackNotice;

            if (!_isEnabled)
            {
                StopRoutingLocked();
                _activeOutputEndpointId = target.Id;

                UpdateStateLocked(new MixedAudioState
                {
                    IsEnabled = false,
                    IsStreaming = false,
                    IsDriverInstalled = true,
                    AvailableOutputs = virtualOutputs,
                    SelectedOutputDeviceId = _selectedOutputId,
                    ActiveOutputDeviceName = target.FriendlyName,
                    PairedCaptureMicHint = target.PairedCaptureHint,
                    MicrophoneVolumePercent = _micVolumePercent,
                    SystemAudioVolumePercent = _sysVolumePercent,
                    IsMicrophoneMixMuted = _isMicMixMuted,
                    IsSystemAudioMixMuted = _isSysMixMuted,
                    IsEchoGuardActive = _isEchoGuardActive,
                    MixedOutputLevel = 0.0,
                    StatusText = effectiveStatus ?? $"Ready (Off) · Virtual Mic: {target.PairedCaptureHint}",
                    DependencyMessage = null,
                    ErrorMessage = null
                });
                return;
            }

            if (string.Equals(_activeOutputEndpointId, target.Id, StringComparison.OrdinalIgnoreCase) && _state.IsStreaming)
            {
                UpdateStateLocked(CopyWith(_state,
                    availableOutputs: virtualOutputs,
                    selectedOutputDeviceId: _selectedOutputId,
                    activeOutputDeviceName: target.FriendlyName,
                    pairedCaptureMicHint: target.PairedCaptureHint,
                    isDriverInstalled: true,
                    isEchoGuardActive: _isEchoGuardActive,
                    dependencyMessage: string.Empty,
                    errorMessage: string.Empty,
                    statusText: effectiveStatus ?? FormatStreamingStatus(target.PairedCaptureHint)));
                return;
            }

            try
            {
                var waveFormat = _backend.ResolveOutputWaveFormat(target.Id);
                var provider = new SynchronizedMixedAudioWaveProvider(waveFormat, OnMixedLevelMeasured);
                provider.SetMicrophoneMix(_micVolumePercent, _isMicMixMuted);
                provider.SetSystemAudioMix(_sysVolumePercent, _isSysMixMuted);
                provider.SetEchoGuardSuppressed(_isEchoGuardActive);

                _activeProvider = provider;
                _backend.StartRouting(target.Id, provider, out var friendlyName, out var pairedHint);
                _activeOutputEndpointId = target.Id;

                UpdateStateLocked(new MixedAudioState
                {
                    IsEnabled = true,
                    IsStreaming = true,
                    IsDriverInstalled = true,
                    AvailableOutputs = virtualOutputs,
                    SelectedOutputDeviceId = _selectedOutputId,
                    ActiveOutputDeviceName = friendlyName,
                    PairedCaptureMicHint = pairedHint,
                    MicrophoneVolumePercent = _micVolumePercent,
                    SystemAudioVolumePercent = _sysVolumePercent,
                    IsMicrophoneMixMuted = _isMicMixMuted,
                    IsSystemAudioMixMuted = _isSysMixMuted,
                    IsEchoGuardActive = _isEchoGuardActive,
                    MixedOutputLevel = 0.0,
                    StatusText = effectiveStatus ?? FormatStreamingStatus(pairedHint),
                    DependencyMessage = null,
                    ErrorMessage = null
                });

                _loggingService.Information("DualAudioMixer started 48kHz IEEE Float stream on '{Output}' (Gemini Mic: '{CaptureHint}').",
                    friendlyName, pairedHint);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || (ex is COMException comEx && comEx.HResult == ErrorAccessDeniedHResult))
            {
                _loggingService.Warning("Windows denied access to virtual audio routing device {Name}: {Message}", target.FriendlyName, ex.Message);
                StopRoutingLocked();
                UpdateStateLocked(new MixedAudioState
                {
                    IsEnabled = true,
                    IsStreaming = false,
                    IsDriverInstalled = true,
                    AvailableOutputs = virtualOutputs,
                    SelectedOutputDeviceId = _selectedOutputId,
                    ActiveOutputDeviceName = target.FriendlyName,
                    PairedCaptureMicHint = target.PairedCaptureHint,
                    MicrophoneVolumePercent = _micVolumePercent,
                    SystemAudioVolumePercent = _sysVolumePercent,
                    IsMicrophoneMixMuted = _isMicMixMuted,
                    IsSystemAudioMixMuted = _isSysMixMuted,
                    IsEchoGuardActive = _isEchoGuardActive,
                    MixedOutputLevel = 0.0,
                    StatusText = "Virtual audio output permission denied",
                    ErrorMessage = $"Windows blocked playback routing to '{target.FriendlyName}': {ex.Message}"
                });
            }
            catch (Exception ex)
            {
                _loggingService.Warning("Failed to route mixed audio to {Name}: {Message}", target.FriendlyName, ex.Message);
                StopRoutingLocked();
                UpdateStateLocked(new MixedAudioState
                {
                    IsEnabled = true,
                    IsStreaming = false,
                    IsDriverInstalled = true,
                    AvailableOutputs = virtualOutputs,
                    SelectedOutputDeviceId = _selectedOutputId,
                    ActiveOutputDeviceName = target.FriendlyName,
                    PairedCaptureMicHint = target.PairedCaptureHint,
                    MicrophoneVolumePercent = _micVolumePercent,
                    SystemAudioVolumePercent = _sysVolumePercent,
                    IsMicrophoneMixMuted = _isMicMixMuted,
                    IsSystemAudioMixMuted = _isSysMixMuted,
                    IsEchoGuardActive = _isEchoGuardActive,
                    MixedOutputLevel = 0.0,
                    StatusText = $"Mixed routing unavailable: {target.FriendlyName}",
                    ErrorMessage = $"Could not open virtual audio output '{target.FriendlyName}': {ex.Message}"
                });
            }
        }
    }

    public void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            if (_isEnabled == enabled)
            {
                return;
            }

            _isEnabled = enabled;
            var updated = _settingsService.CurrentSettings.Clone();
            updated.EnableGeminiMixedAudioRouting = enabled;
            _settingsService.SaveSettings(updated);
        }

        if (enabled)
        {
            if (!_microphoneService.CurrentState.IsMonitoring)
            {
                _microphoneService.StartListening();
            }

            if (!_systemAudioService.CurrentState.IsListening)
            {
                _systemAudioService.StartListening();
            }
        }

        RefreshDevices();
    }

    public void SelectOutputDevice(string deviceId)
    {
        lock (_sync)
        {
            var normalized = deviceId ?? AutoVirtualCableId;
            if (string.Equals(_selectedOutputId, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedOutputId = normalized;
            _activeOutputEndpointId = null;
            var updated = _settingsService.CurrentSettings.Clone();
            updated.PreferredMixedOutputDeviceId = normalized;
            _settingsService.SaveSettings(updated);
        }

        RefreshDevices();
    }

    public void SetMicrophoneMixVolume(int percent)
    {
        var clamped = Math.Clamp(percent, 0, 150);
        lock (_sync)
        {
            if (_micVolumePercent == clamped)
            {
                return;
            }

            _micVolumePercent = clamped;
            _activeProvider?.SetMicrophoneMix(_micVolumePercent, _isMicMixMuted);

            var updated = _settingsService.CurrentSettings.Clone();
            updated.MixedMicrophoneVolumePercent = clamped;
            _settingsService.SaveSettings(updated);

            UpdateStateLocked(CopyWith(_state, micVolumePercent: clamped));
        }
    }

    public void SetSystemAudioMixVolume(int percent)
    {
        var clamped = Math.Clamp(percent, 0, 150);
        lock (_sync)
        {
            if (_sysVolumePercent == clamped)
            {
                return;
            }

            _sysVolumePercent = clamped;
            _activeProvider?.SetSystemAudioMix(_sysVolumePercent, _isSysMixMuted);

            var updated = _settingsService.CurrentSettings.Clone();
            updated.MixedSystemAudioVolumePercent = clamped;
            _settingsService.SaveSettings(updated);

            UpdateStateLocked(CopyWith(_state, sysVolumePercent: clamped));
        }
    }

    public void ToggleMicrophoneMixMute()
    {
        lock (_sync)
        {
            _isMicMixMuted = !_isMicMixMuted;
            _activeProvider?.SetMicrophoneMix(_micVolumePercent, _isMicMixMuted);
            UpdateStateLocked(CopyWith(_state, isMicMixMuted: _isMicMixMuted));
        }
    }

    public void ToggleSystemAudioMixMute()
    {
        lock (_sync)
        {
            _isSysMixMuted = !_isSysMixMuted;
            _activeProvider?.SetSystemAudioMix(_sysVolumePercent, _isSysMixMuted);
            UpdateStateLocked(CopyWith(_state, isSysMixMuted: _isSysMixMuted));
        }
    }

    public void OpenVirtualAudioDriverWebsite()
    {
        try
        {
            Process.Start(new ProcessStartInfo(VirtualAudioDownloadUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _loggingService.Warning("Unable to open VB-Audio download page: {Error}", ex.Message);
        }
    }

    public void OpenWindowsSoundSettings() => _systemAudioService.OpenWindowsSoundSettings();

    private void OnGeminiDocumentPlayingAudioChanged(object? sender, bool isPlaying)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _isGeminiPlayingAudio = isPlaying;
            if (isPlaying)
            {
                _echoGuardReleaseTimer.Change(Timeout.Infinite, Timeout.Infinite);
                if (!_isEchoGuardActive)
                {
                    _isEchoGuardActive = true;
                    _activeProvider?.SetEchoGuardSuppressed(true);
                    _loggingService.Debug("Echo Guard engaged: pausing System Audio feed into CABLE Input while Gemini speaks.");
                    if (_state.IsStreaming)
                    {
                        UpdateStateLocked(CopyWith(_state,
                            isEchoGuardActive: true,
                            statusText: FormatStreamingStatus(_state.PairedCaptureMicHint)));
                    }
                }
            }
            else if (_isEchoGuardActive)
            {
                // Keep System Audio suppressed for 400ms after Gemini stops speaking to prevent tail echo.
                _echoGuardReleaseTimer.Change(EchoGuardReleaseDelayMs, Timeout.Infinite);
            }
        }
    }

    private void OnEchoGuardReleaseElapsed(object? state)
    {
        lock (_sync)
        {
            if (_disposed || _isGeminiPlayingAudio || !_isEchoGuardActive)
            {
                return;
            }

            _isEchoGuardActive = false;
            _activeProvider?.SetEchoGuardSuppressed(false);
            _loggingService.Debug("Echo Guard released 400ms after Gemini stopped speaking.");
            if (_state.IsStreaming)
            {
                UpdateStateLocked(CopyWith(_state,
                    isEchoGuardActive: false,
                    statusText: FormatStreamingStatus(_state.PairedCaptureMicHint)));
            }
        }
    }

    private string FormatStreamingStatus(string pairedHint) =>
        _isEchoGuardActive
            ? $"Echo Guard Active (Gemini speaking) · Mic live → {pairedHint}"
            : $"Streaming Mixed Audio → {pairedHint}";

    private void OnMicrophoneFloat16k(ReadOnlySpan<float> samples)
    {
        if (!_isEnabled || _disposing)
        {
            return;
        }

        var provider = Volatile.Read(ref _activeProvider);
        if (provider is not null && !_microphoneService.CurrentState.IsMuted)
        {
            provider.PushMicrophoneFloat16k(samples);
        }
    }

    private void OnSystemAudioFloat16k(ReadOnlySpan<float> samples)
    {
        if (!_isEnabled || _disposing || _isEchoGuardActive)
        {
            return;
        }

        var provider = Volatile.Read(ref _activeProvider);
        if (provider is not null && !_systemAudioService.CurrentState.IsMuted)
        {
            provider.PushSystemAudioFloat16k(samples);
        }
    }

    private void OnMixedLevelMeasured(double level)
    {
        if (_disposing || !Monitor.TryEnter(_sync))
        {
            return;
        }

        try
        {
            if (_disposed || !_state.IsStreaming)
            {
                return;
            }

            if (Math.Abs(_state.MixedOutputLevel - level) < 0.008 && (level == 0.0) == (_state.MixedOutputLevel == 0.0))
            {
                return;
            }

            UpdateStateLocked(CopyWith(_state, mixedOutputLevel: level));
        }
        finally
        {
            Monitor.Exit(_sync);
        }
    }

    private static VirtualAudioOutputDevice ResolveTargetDevice(
        IReadOnlyList<VirtualAudioOutputDevice> devices,
        string selectedId,
        out string? fallbackNotice)
    {
        fallbackNotice = null;
        if (!string.IsNullOrWhiteSpace(selectedId))
        {
            var match = devices.FirstOrDefault(d => string.Equals(d.Id, selectedId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }

            fallbackNotice = $"Selected virtual output disconnected — using {devices[0].FriendlyName}";
        }

        return devices.FirstOrDefault(d => d.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
            ?? devices[0];
    }

    private void StopRoutingLocked()
    {
        _activeProvider?.Reset();
        _activeProvider = null;
        try
        {
            _backend.StopRouting();
        }
        catch
        {
            // Ignore cleanup stop errors.
        }
    }

    private void OnRoutingStoppedUnexpectedly(object? sender, Exception? ex)
    {
        _loggingService.Warning("Virtual audio routing stopped unexpectedly ({Message}); refreshing virtual audio drivers.",
            ex?.Message ?? "Driver disconnected");
        Task.Run(() =>
        {
            lock (_sync)
            {
                _activeOutputEndpointId = null;
            }

            RefreshDevices("Virtual audio driver reconnecting...");
        });
    }

    private void OnDevicesTopologyChanged(object? sender, EventArgs e)
    {
        try
        {
            if (!_disposed)
            {
                _topologyDebounceTimer.Change(160, Timeout.Infinite);
            }
        }
        catch
        {
            // Ignore if disposed.
        }
    }

    private void UpdateStateLocked(MixedAudioState next)
    {
        _state = next;
        StateChanged?.Invoke(this, next);
    }

    private static MixedAudioState CopyWith(
        MixedAudioState src,
        bool? isEnabled = null,
        bool? isStreaming = null,
        bool? isDriverInstalled = null,
        IReadOnlyList<VirtualAudioOutputDevice>? availableOutputs = null,
        string? selectedOutputDeviceId = null,
        string? activeOutputDeviceName = null,
        string? pairedCaptureMicHint = null,
        int? micVolumePercent = null,
        int? sysVolumePercent = null,
        bool? isMicMixMuted = null,
        bool? isSysMixMuted = null,
        bool? isEchoGuardActive = null,
        double? mixedOutputLevel = null,
        string? statusText = null,
        string? dependencyMessage = null,
        string? errorMessage = null)
    {
        return new MixedAudioState
        {
            IsEnabled = isEnabled ?? src.IsEnabled,
            IsStreaming = isStreaming ?? src.IsStreaming,
            IsDriverInstalled = isDriverInstalled ?? src.IsDriverInstalled,
            AvailableOutputs = availableOutputs ?? src.AvailableOutputs,
            SelectedOutputDeviceId = selectedOutputDeviceId ?? src.SelectedOutputDeviceId,
            ActiveOutputDeviceName = activeOutputDeviceName ?? src.ActiveOutputDeviceName,
            PairedCaptureMicHint = pairedCaptureMicHint ?? src.PairedCaptureMicHint,
            MicrophoneVolumePercent = micVolumePercent ?? src.MicrophoneVolumePercent,
            SystemAudioVolumePercent = sysVolumePercent ?? src.SystemAudioVolumePercent,
            IsMicrophoneMixMuted = isMicMixMuted ?? src.IsMicrophoneMixMuted,
            IsSystemAudioMixMuted = isSysMixMuted ?? src.IsSystemAudioMixMuted,
            IsEchoGuardActive = isEchoGuardActive ?? src.IsEchoGuardActive,
            MixedOutputLevel = mixedOutputLevel ?? src.MixedOutputLevel,
            StatusText = statusText ?? src.StatusText,
            DependencyMessage = dependencyMessage is null ? src.DependencyMessage : (dependencyMessage.Length == 0 ? null : dependencyMessage),
            ErrorMessage = errorMessage is null ? src.ErrorMessage : (errorMessage.Length == 0 ? null : errorMessage)
        };
    }

    public void Dispose()
    {
        _disposing = true;
        _backend.RoutingStoppedUnexpectedly -= OnRoutingStoppedUnexpectedly;
        _backend.DevicesTopologyChanged -= OnDevicesTopologyChanged;
        _browserService.DocumentPlayingAudioChanged -= OnGeminiDocumentPlayingAudioChanged;

        _microphoneService.SetMonitorFloatSink(null);
        _systemAudioService.SetMonitorFloatSink(null);

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _topologyDebounceTimer.Dispose();
            _echoGuardReleaseTimer.Dispose();
            StopRoutingLocked();
            _backend.Dispose();
        }
    }
}
