using System.Diagnostics;
using System.Runtime.InteropServices;
using GeminiDesktop.Helpers;
using GeminiDesktop.Models;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace GeminiDesktop.Services;

/// <summary>
/// Abstraction over Windows WASAPI loopback capture on playback (DataFlow.Render) endpoints.
/// Supports default Playback (Multimedia/Console), Communications (Meet/Zoom/Teams calls),
/// explicit output device selection, and Smart Auto multi-output capture.
/// Never reads from physical capture (microphone) endpoints.
/// </summary>
public interface ISystemAudioBackend : IDisposable
{
    event EventHandler<double>? AudioLevelMeasured;
    event EventHandler<string>? ActiveEndpointDetected;
    event EventHandler<Exception?>? CaptureStoppedUnexpectedly;
    event EventHandler? DevicesTopologyChanged;

    IReadOnlyList<SystemAudioDevice> EnumerateRenderDevices();
    IReadOnlyList<string> ResolveTargetRenderDevices(
        string selectedOptionId,
        out string displaySummary,
        out string? fallbackNotice);

    void StartLoopbackCapture(IReadOnlyList<string> deviceIds, out IReadOnlyList<string> startedDeviceNames);
    void StopCapture();
    void SetMutedSuppression(bool muted);
    void SetPcmSink(Pcm16kSpanHandler? sink);
}

/// <summary>
/// Real Windows Core Audio loopback backend capturing directly from active DataFlow.Render output endpoints
/// (browsers, Google Meet, Zoom, Microsoft Teams, media players, Bluetooth headsets, etc.) using Polling/Fallback
/// WASAPI loopback without ever reading physical microphone endpoints.
/// </summary>
public sealed class WasapiSystemAudioBackend : ISystemAudioBackend
{
    private const int MaxAutoCaptureEndpoints = 4;

    private sealed class CaptureSlot : IDisposable
    {
        public CaptureSlot(string deviceId, string friendlyName, bool isCommunicationsDefault, bool isMultimediaDefault, WasapiStreamCaptureSession session)
        {
            DeviceId = deviceId;
            FriendlyName = friendlyName;
            IsCommunicationsDefault = isCommunicationsDefault;
            IsMultimediaDefault = isMultimediaDefault;
            Session = session;
        }

        public string DeviceId { get; }
        public string FriendlyName { get; }
        public bool IsCommunicationsDefault { get; }
        public bool IsMultimediaDefault { get; }
        public WasapiStreamCaptureSession Session { get; }
        public double LastLevel { get; set; }
        public float RecentRms { get; set; }
        public long LastVoiceTickMs { get; set; }

        public void Dispose() => Session.Dispose();
    }

    private readonly MMDeviceEnumerator _enumerator;
    private readonly MMDeviceNotificationClient? _notificationClient;
    private readonly object _slotsLock = new();
    private readonly object _routerLock = new();
    private readonly List<CaptureSlot> _slots = [];
    private readonly AdaptiveSpeechGainProcessor _speechGain = new();

    private Pcm16kSpanHandler? _pcmSink;
    private string? _activeSpeechSlotId;
    private string? _lastReportedEndpointName;
    private float[] _gainScratchBuffer = new float[2048];
    private byte[] _pcmScratchBuffer = new byte[4096];
    private volatile bool _suppressOutput;
    private bool _disposed;

    public WasapiSystemAudioBackend()
    {
        _enumerator = new MMDeviceEnumerator();

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
    public event EventHandler<string>? ActiveEndpointDetected;
    public event EventHandler<Exception?>? CaptureStoppedUnexpectedly;
    public event EventHandler? DevicesTopologyChanged;

    public IReadOnlyList<SystemAudioDevice> EnumerateRenderDevices()
    {
        if (_disposed)
        {
            return [];
        }

        var defaultMultimediaId = TryGetDefaultEndpointId(Role.Multimedia);
        var defaultConsoleId = TryGetDefaultEndpointId(Role.Console);
        var defaultCommId = TryGetDefaultEndpointId(Role.Communications);
        var primaryMultimediaId = defaultMultimediaId ?? defaultConsoleId;

        var result = new List<SystemAudioDevice>();
        try
        {
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    var id = device.ID;
                    var isMult = string.Equals(id, primaryMultimediaId, StringComparison.OrdinalIgnoreCase);
                    var isComm = string.Equals(id, defaultCommId, StringComparison.OrdinalIgnoreCase);
                    result.Add(new SystemAudioDevice(id, device.FriendlyName, isMult, isComm));
                }
            }
        }
        catch
        {
            // Return discovered devices so far.
        }

        return result;
    }

    public IReadOnlyList<string> ResolveTargetRenderDevices(
        string selectedOptionId,
        out string displaySummary,
        out string? fallbackNotice)
    {
        fallbackNotice = null;
        displaySummary = "No playback device detected";

        var activeDevices = EnumerateRenderDevices();
        if (activeDevices.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(selectedOptionId) &&
                !string.Equals(selectedOptionId, SystemAudioService.CommunicationsDefaultOptionId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(selectedOptionId, SystemAudioService.MultimediaDefaultOptionId, StringComparison.OrdinalIgnoreCase))
            {
                fallbackNotice = "Selected playback device disconnected — no playback outputs detected";
            }

            return [];
        }

        var commId = TryGetDefaultEndpointId(Role.Communications);
        var multId = TryGetDefaultEndpointId(Role.Multimedia) ?? TryGetDefaultEndpointId(Role.Console);

        if (string.Equals(selectedOptionId, SystemAudioService.CommunicationsDefaultOptionId, StringComparison.OrdinalIgnoreCase))
        {
            var commTarget = FindById(activeDevices, commId) ?? FindById(activeDevices, multId) ?? activeDevices[0];
            displaySummary = commTarget.FriendlyName;
            return [commTarget.Id];
        }

        if (string.Equals(selectedOptionId, SystemAudioService.MultimediaDefaultOptionId, StringComparison.OrdinalIgnoreCase))
        {
            var multTarget = FindById(activeDevices, multId) ?? FindById(activeDevices, commId) ?? activeDevices[0];
            displaySummary = multTarget.FriendlyName;
            return [multTarget.Id];
        }

        if (!string.IsNullOrWhiteSpace(selectedOptionId))
        {
            var exact = FindById(activeDevices, selectedOptionId);
            if (exact is not null)
            {
                displaySummary = exact.FriendlyName;
                return [exact.Id];
            }

            var fallbackIds = BuildAutoEndpointList(activeDevices, commId, multId, out var fallbackSummary);
            displaySummary = fallbackSummary;
            fallbackNotice = $"Selected playback device disconnected — using {fallbackSummary}";
            return fallbackIds;
        }

        var autoIds = BuildAutoEndpointList(activeDevices, commId, multId, out var autoSummary);
        displaySummary = autoSummary;
        return autoIds;
    }

    public void StartLoopbackCapture(IReadOnlyList<string> deviceIds, out IReadOnlyList<string> startedDeviceNames)
    {
        StopCapture();

        if (deviceIds is null || deviceIds.Count == 0)
        {
            startedDeviceNames = [];
            return;
        }

        var commId = TryGetDefaultEndpointId(Role.Communications);
        var multId = TryGetDefaultEndpointId(Role.Multimedia) ?? TryGetDefaultEndpointId(Role.Console);

        var uniqueIds = deviceIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxAutoCaptureEndpoints)
            .ToArray();

        var newSlots = new List<CaptureSlot>();
        var startedNames = new List<string>();
        Exception? fatalPermissionError = null;
        Exception? lastFailure = null;

        try
        {
            foreach (var deviceId in uniqueIds)
            {
                MMDevice? device = null;
                CaptureSlot? slot = null;
                try
                {
                    device = _enumerator.GetDevice(deviceId);
                    var friendlyName = device.FriendlyName;
                    var isComm = string.Equals(deviceId, commId, StringComparison.OrdinalIgnoreCase);
                    var isMult = string.Equals(deviceId, multId, StringComparison.OrdinalIgnoreCase);

                    CaptureSlot? capturedSlotRef = null;
                    var session = new WasapiStreamCaptureSession(
                        isLoopback: true,
                        onLevel: level => OnSlotLevel(capturedSlotRef, level),
                        onStoppedUnexpectedly: ex => OnSlotStoppedUnexpectedly(capturedSlotRef, ex));

                    slot = new CaptureSlot(deviceId, friendlyName, isComm, isMult, session);
                    capturedSlotRef = slot;

                    session.SetSuppressOutput(_suppressOutput);
                    session.SetFloatSink(samples => OnSlotFloatSamples(slot, samples));
                    session.Start(device); // Session takes ownership & disposes device
                    device = null;

                    newSlots.Add(slot);
                    startedNames.Add(friendlyName);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || (ex is COMException comEx && comEx.HResult == unchecked((int)0x80070005)))
                {
                    device?.Dispose();
                    slot?.Dispose();
                    fatalPermissionError ??= ex;
                }
                catch (Exception ex)
                {
                    device?.Dispose();
                    slot?.Dispose();
                    lastFailure = ex;
                }
            }

            if (newSlots.Count == 0)
            {
                if (fatalPermissionError is not null)
                {
                    throw fatalPermissionError;
                }

                if (lastFailure is not null)
                {
                    throw lastFailure;
                }
            }

            lock (_slotsLock)
            {
                _slots.Clear();
                _slots.AddRange(newSlots);
            }

            lock (_routerLock)
            {
                _speechGain.Reset();
                _activeSpeechSlotId = newSlots.Count > 0 ? newSlots[0].DeviceId : null;
                _lastReportedEndpointName = newSlots.Count > 0 ? newSlots[0].FriendlyName : null;
            }

            startedDeviceNames = startedNames;
        }
        catch
        {
            foreach (var slot in newSlots)
            {
                slot.Dispose();
            }

            throw;
        }
    }

    public void StopCapture()
    {
        List<CaptureSlot> toDispose;
        lock (_slotsLock)
        {
            if (_slots.Count == 0)
            {
                return;
            }

            toDispose = [.. _slots];
            _slots.Clear();
        }

        lock (_routerLock)
        {
            _activeSpeechSlotId = null;
            _lastReportedEndpointName = null;
            _speechGain.Reset();
        }

        foreach (var slot in toDispose)
        {
            slot.Dispose();
        }
    }

    public void SetMutedSuppression(bool muted)
    {
        _suppressOutput = muted;
        lock (_slotsLock)
        {
            foreach (var slot in _slots)
            {
                slot.Session.SetSuppressOutput(muted);
                if (muted)
                {
                    slot.LastLevel = 0.0;
                    slot.RecentRms = 0f;
                }
            }
        }
    }

    public void SetPcmSink(Pcm16kSpanHandler? sink) => Volatile.Write(ref _pcmSink, sink);

    private void OnSlotLevel(CaptureSlot? slot, double level)
    {
        if (slot is null || _disposed)
        {
            return;
        }

        double maxLevel;
        lock (_slotsLock)
        {
            slot.LastLevel = _suppressOutput ? 0.0 : level;
            maxLevel = 0.0;
            foreach (var s in _slots)
            {
                if (s.LastLevel > maxLevel)
                {
                    maxLevel = s.LastLevel;
                }
            }
        }

        AudioLevelMeasured?.Invoke(this, _suppressOutput ? 0.0 : maxLevel);
    }

    private void OnSlotFloatSamples(CaptureSlot slot, ReadOnlySpan<float> samples)
    {
        if (_disposed || _suppressOutput || samples.IsEmpty)
        {
            return;
        }

        var sink = Volatile.Read(ref _pcmSink);
        if (sink is null)
        {
            return;
        }

        var now = Environment.TickCount64;
        var rms = ComputeRms(samples);
        var hasVoiceEnergy = rms > 0.0018f;

        lock (_routerLock)
        {
            slot.RecentRms = hasVoiceEnergy
                ? (slot.RecentRms * 0.65f) + (rms * 0.35f)
                : slot.RecentRms * 0.85f;
            if (hasVoiceEnergy)
            {
                slot.LastVoiceTickMs = now;
            }

            CaptureSlot? activeSlot = null;
            List<CaptureSlot> currentSlots;
            lock (_slotsLock)
            {
                if (_slots.Count == 0)
                {
                    return;
                }

                currentSlots = [.. _slots];
            }

            if (currentSlots.Count == 1)
            {
                activeSlot = currentSlots[0];
            }
            else
            {
                var currentPrimary = currentSlots.FirstOrDefault(s =>
                    string.Equals(s.DeviceId, _activeSpeechSlotId, StringComparison.OrdinalIgnoreCase));

                if (currentPrimary is null)
                {
                    activeSlot = slot;
                }
                else if (string.Equals(currentPrimary.DeviceId, slot.DeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    activeSlot = currentPrimary;
                }
                else
                {
                    var currentSilentTooLong = (now - currentPrimary.LastVoiceTickMs) > 320;
                    var challengerMuchStronger = hasVoiceEnergy && rms > (currentPrimary.RecentRms * 1.40f + 0.004f);
                    activeSlot = (currentSilentTooLong && hasVoiceEnergy) || challengerMuchStronger
                        ? slot
                        : currentPrimary;
                }
            }

            if (!string.Equals(activeSlot.DeviceId, slot.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _activeSpeechSlotId = activeSlot.DeviceId;
            var changedName = !string.Equals(_lastReportedEndpointName, activeSlot.FriendlyName, StringComparison.Ordinal);
            if (changedName)
            {
                _lastReportedEndpointName = activeSlot.FriendlyName;
            }

            if (_gainScratchBuffer.Length < samples.Length)
            {
                _gainScratchBuffer = new float[samples.Length];
            }

            samples.CopyTo(_gainScratchBuffer);
            var processedSpan = _gainScratchBuffer.AsSpan(0, samples.Length);
            _speechGain.ProcessInPlace(processedSpan);

            var byteCount = AudioProcessingHelper.EncodeFloatToPcm16(processedSpan, ref _pcmScratchBuffer);
            if (byteCount > 0)
            {
                sink(_pcmScratchBuffer.AsSpan(0, byteCount));
            }

            if (changedName && hasVoiceEnergy)
            {
                var activeName = activeSlot.FriendlyName;
                Task.Run(() => ActiveEndpointDetected?.Invoke(this, activeName));
            }
        }
    }

    private void OnSlotStoppedUnexpectedly(CaptureSlot? slot, Exception? ex)
    {
        if (slot is null || _disposed)
        {
            return;
        }

        CaptureStoppedUnexpectedly?.Invoke(this, ex);
    }

    private List<string> BuildAutoEndpointList(
        IReadOnlyList<SystemAudioDevice> activeDevices,
        string? commId,
        string? multId,
        out string summary)
    {
        var chosen = new List<SystemAudioDevice>();
        void TryAdd(SystemAudioDevice? candidate)
        {
            if (candidate is not null &&
                chosen.Count < MaxAutoCaptureEndpoints &&
                !chosen.Any(c => string.Equals(c.Id, candidate.Id, StringComparison.OrdinalIgnoreCase)))
            {
                chosen.Add(candidate);
            }
        }

        // Priority 1: Windows Default Communications output (Google Meet, Zoom, Microsoft Teams calls)
        TryAdd(FindById(activeDevices, commId));
        // Priority 2: Windows Default Multimedia / Console output (Chrome/Edge tabs, YouTube, media)
        TryAdd(FindById(activeDevices, multId));

        // Priority 3: Any other active playback endpoint currently running audio sessions
        var withSessionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dev in activeDevices)
        {
            if (chosen.Count >= MaxAutoCaptureEndpoints)
            {
                break;
            }

            if (HasActiveAudioSession(dev.Id))
            {
                withSessionIds.Add(dev.Id);
                TryAdd(dev);
            }
        }

        // Priority 4: Remaining active playback endpoints (e.g. plugged-in headphones before call audio starts)
        foreach (var dev in activeDevices)
        {
            if (chosen.Count >= MaxAutoCaptureEndpoints)
            {
                break;
            }

            if (!withSessionIds.Contains(dev.Id))
            {
                TryAdd(dev);
            }
        }

        summary = chosen.Count switch
        {
            0 => "No playback device detected",
            1 => chosen[0].FriendlyName,
            _ => $"Auto ({chosen.Count} outputs: {chosen[0].FriendlyName} + {chosen.Count - 1} more)"
        };

        return chosen.Select(c => c.Id).ToList();
    }

    private bool HasActiveAudioSession(string deviceId)
    {
        try
        {
            using var device = _enumerator.GetDevice(deviceId);
            var sessions = device.AudioSessionManager?.Sessions;
            if (sessions is null)
            {
                return false;
            }

            for (var i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session.State == AudioSessionState.AudioSessionStateActive)
                {
                    return true;
                }
            }
        }
        catch
        {
            // Some endpoints disallow session enumeration.
        }

        return false;
    }

    private string? TryGetDefaultEndpointId(Role role)
    {
        try
        {
            if (_enumerator.HasDefaultAudioEndpoint(DataFlow.Render, role))
            {
                using var dev = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
                return dev.ID;
            }
        }
        catch
        {
            // Role unset or inaccessible.
        }

        return null;
    }

    private static SystemAudioDevice? FindById(IReadOnlyList<SystemAudioDevice> devices, string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    private static float ComputeRms(ReadOnlySpan<float> samples)
    {
        double sumSq = 0.0;
        for (var i = 0; i < samples.Length; i++)
        {
            var s = samples[i];
            sumSq += (double)s * s;
        }

        return (float)Math.Sqrt(sumSq / Math.Max(1, samples.Length));
    }

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
        StopCapture();

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
/// Manages Windows system-audio loopback listening, playback device routing (including Communications/Headphones),
/// independent mute, live level metering & waveform bars, and debounced hot-plug output switching.
/// </summary>
public interface ISystemAudioService : IDisposable
{
    SystemAudioState CurrentState { get; }

    event EventHandler<SystemAudioState>? StateChanged;

    void Initialize();
    void RefreshDevice(string? notificationStatus = null);
    void SelectDevice(string deviceId);
    void StartListening();
    void StopListening();
    void SetMuted(bool muted);
    void ToggleMute();
    void SetPcmSink(Pcm16kSpanHandler? sink);
    void OpenWindowsSoundSettings();
}

public sealed class SystemAudioService : ISystemAudioService
{
    public const string SystemAutoDeviceId = "";
    public const string CommunicationsDefaultOptionId = "__comm__";
    public const string MultimediaDefaultOptionId = "__multimedia__";

    private const int ErrorAccessDeniedHResult = unchecked((int)0x80070005);
    private const int WaveformBarCount = 20;
    private const double WaveformBaselineHeight = 3.0;
    private const double WaveformMaxExtraHeight = 23.0;

    private readonly ISystemAudioBackend _backend;
    private readonly ISettingsService _settingsService;
    private readonly ILoggingService _loggingService;
    private readonly object _sync = new();
    private readonly Timer _waveformDecayTimer;
    private readonly Timer _topologyDebounceTimer;
    private readonly double[] _waveformHistory = new double[WaveformBarCount];

    private SystemAudioState _state = new();
    private string _selectedDeviceId = SystemAutoDeviceId;
    private string _activeEndpointSignature = string.Empty;
    private string _activeDisplaySummary = "No playback device detected";
    private string? _lastDetectedSpeakingDevice;
    private double _latestCapturedLevel;
    private long _lastAudioCallbackTickMs;
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

        Array.Fill(_waveformHistory, WaveformBaselineHeight);

        _waveformDecayTimer = new Timer(OnWaveformTick, null, Timeout.Infinite, Timeout.Infinite);
        _topologyDebounceTimer = new Timer(_ => RefreshDevice(), null, Timeout.Infinite, Timeout.Infinite);

        _backend.AudioLevelMeasured += OnAudioLevelMeasured;
        _backend.ActiveEndpointDetected += OnActiveEndpointDetected;
        _backend.CaptureStoppedUnexpectedly += OnCaptureStoppedUnexpectedly;
        _backend.DevicesTopologyChanged += OnDevicesTopologyChanged;

        _selectedDeviceId = _settingsService.CurrentSettings.PreferredSystemAudioDeviceId ?? SystemAutoDeviceId;
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

            var rawDevices = _backend.EnumerateRenderDevices();
            var uiDevices = BuildUiDeviceList(rawDevices);

            var targetIds = _backend.ResolveTargetRenderDevices(_selectedDeviceId, out var displaySummary, out var fallbackNotice);
            var effectiveNotice = notificationStatus ?? fallbackNotice;

            if (targetIds.Count == 0)
            {
                StopCaptureLocked();
                _activeEndpointSignature = string.Empty;
                _activeDisplaySummary = "No playback device detected";
                _lastDetectedSpeakingDevice = null;
                ResetWaveformLocked();

                UpdateStateLocked(new SystemAudioState
                {
                    Devices = uiDevices,
                    SelectedDeviceId = _selectedDeviceId,
                    ActiveDeviceId = string.Empty,
                    ActiveDeviceName = "No playback device detected",
                    IsListening = false,
                    IsMuted = _isMuted,
                    OutputLevel = 0.0,
                    WaveformBars = SnapshotWaveformLocked(),
                    IsDeviceAvailable = false,
                    HasError = false,
                    StatusText = effectiveNotice ?? "No active Windows playback output detected"
                });
                return;
            }

            var signature = string.Join("|", targetIds);

            if (!_isListeningEnabled)
            {
                StopCaptureLocked();
                _activeEndpointSignature = signature;
                _activeDisplaySummary = displaySummary;
                _lastDetectedSpeakingDevice = null;
                ResetWaveformLocked();

                UpdateStateLocked(new SystemAudioState
                {
                    Devices = uiDevices,
                    SelectedDeviceId = _selectedDeviceId,
                    ActiveDeviceId = targetIds[0],
                    ActiveDeviceName = displaySummary,
                    IsListening = false,
                    IsMuted = _isMuted,
                    OutputLevel = 0.0,
                    WaveformBars = SnapshotWaveformLocked(),
                    IsDeviceAvailable = true,
                    HasError = false,
                    StatusText = effectiveNotice ?? $"Stopped · {displaySummary}"
                });
                return;
            }

            if (string.Equals(_activeEndpointSignature, signature, StringComparison.OrdinalIgnoreCase) && _state.IsListening)
            {
                _activeDisplaySummary = displaySummary;
                UpdateStateLocked(CopyWith(_state,
                    devices: uiDevices,
                    selectedDeviceId: _selectedDeviceId,
                    activeDeviceId: targetIds[0],
                    activeDeviceName: _lastDetectedSpeakingDevice ?? displaySummary,
                    isDeviceAvailable: true,
                    hasError: false,
                    statusText: effectiveNotice ?? FormatActiveStatus(_lastDetectedSpeakingDevice ?? displaySummary, _isMuted)));
                return;
            }

            try
            {
                _backend.SetMutedSuppression(_isMuted);
                _backend.StartLoopbackCapture(targetIds, out var startedNames);
                _activeEndpointSignature = signature;
                _activeDisplaySummary = startedNames.Count == 1 ? startedNames[0] : displaySummary;
                _lastDetectedSpeakingDevice = null;

                UpdateStateLocked(new SystemAudioState
                {
                    Devices = uiDevices,
                    SelectedDeviceId = _selectedDeviceId,
                    ActiveDeviceId = targetIds[0],
                    ActiveDeviceName = _activeDisplaySummary,
                    IsListening = true,
                    IsMuted = _isMuted,
                    OutputLevel = 0.0,
                    WaveformBars = SnapshotWaveformLocked(),
                    IsDeviceAvailable = true,
                    HasError = false,
                    StatusText = effectiveNotice ?? FormatActiveStatus(_activeDisplaySummary, _isMuted)
                });

                _loggingService.Information("System audio loopback capture started on {Count} output(s): {Names}",
                    startedNames.Count, string.Join(", ", startedNames));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || (ex is COMException comEx && comEx.HResult == ErrorAccessDeniedHResult))
            {
                _loggingService.Warning("Windows denied system audio loopback capture: {Message}", ex.Message);
                StopCaptureLocked();
                _activeEndpointSignature = string.Empty;
                ResetWaveformLocked();

                UpdateStateLocked(new SystemAudioState
                {
                    Devices = uiDevices,
                    SelectedDeviceId = _selectedDeviceId,
                    ActiveDeviceId = targetIds[0],
                    ActiveDeviceName = displaySummary,
                    IsListening = false,
                    IsMuted = _isMuted,
                    OutputLevel = 0.0,
                    WaveformBars = SnapshotWaveformLocked(),
                    IsDeviceAvailable = true,
                    HasError = true,
                    StatusText = "System audio permission blocked",
                    ErrorMessage = "Windows blocked system audio loopback capture. Verify sound output permissions and audio drivers in Windows Settings."
                });
            }
            catch (Exception ex)
            {
                _loggingService.Warning("Failed to start system audio loopback capture on {Name}: {Message}", displaySummary, ex.Message);
                StopCaptureLocked();
                _activeEndpointSignature = string.Empty;
                ResetWaveformLocked();

                UpdateStateLocked(new SystemAudioState
                {
                    Devices = uiDevices,
                    SelectedDeviceId = _selectedDeviceId,
                    ActiveDeviceId = targetIds[0],
                    ActiveDeviceName = displaySummary,
                    IsListening = false,
                    IsMuted = _isMuted,
                    OutputLevel = 0.0,
                    WaveformBars = SnapshotWaveformLocked(),
                    IsDeviceAvailable = true,
                    HasError = true,
                    StatusText = $"System audio unavailable: {displaySummary}",
                    ErrorMessage = $"Could not capture Windows playback audio from '{displaySummary}': {ex.Message}"
                });
            }
        }
    }

    public void SelectDevice(string deviceId)
    {
        lock (_sync)
        {
            var normalized = deviceId ?? SystemAutoDeviceId;
            if (string.Equals(_selectedDeviceId, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedDeviceId = normalized;
            _activeEndpointSignature = string.Empty;
            _lastDetectedSpeakingDevice = null;

            var updated = _settingsService.CurrentSettings.Clone();
            updated.PreferredSystemAudioDeviceId = normalized;
            _settingsService.SaveSettings(updated);

            _loggingService.Information("Selected preferred system audio output option: {DeviceId}",
                string.IsNullOrEmpty(normalized) ? "<Auto>" : normalized);
        }

        RefreshDevice();
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
            if (muted)
            {
                _latestCapturedLevel = 0.0;
                ResetWaveformLocked();
                StopWaveformTimerLocked();
            }

            var current = _state;
            var status = current.IsListening
                ? FormatActiveStatus(current.ActiveDeviceName, muted)
                : (current.IsDeviceAvailable ? $"Stopped · {current.ActiveDeviceName}" : current.StatusText);

            UpdateStateLocked(CopyWith(current,
                isMuted: muted,
                outputLevel: muted ? 0.0 : current.OutputLevel,
                waveformBars: muted ? SnapshotWaveformLocked() : null,
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

    private static IReadOnlyList<SystemAudioDevice> BuildUiDeviceList(IReadOnlyList<SystemAudioDevice> rawDevices)
    {
        var list = new List<SystemAudioDevice>
        {
            new(SystemAutoDeviceId, "Auto (Speakers + Headphones + Call Outputs)", IsAutoOption: true)
        };

        var commDev = rawDevices.FirstOrDefault(d => d.IsDefaultCommunications);
        var multDev = rawDevices.FirstOrDefault(d => d.IsDefaultMultimedia);

        if (commDev is not null)
        {
            list.Add(new SystemAudioDevice(CommunicationsDefaultOptionId, $"Windows Call Default ({commDev.FriendlyName})", IsDefaultCommunications: true));
        }

        if (multDev is not null && !string.Equals(multDev.Id, commDev?.Id, StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new SystemAudioDevice(MultimediaDefaultOptionId, $"Windows Playback Default ({multDev.FriendlyName})", IsDefaultMultimedia: true));
        }

        list.AddRange(rawDevices);
        return list;
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
        StopWaveformTimerLocked();
        _latestCapturedLevel = 0.0;
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

            var effective = _isMuted ? 0.0 : Math.Clamp(level, 0.0, 1.0);
            _latestCapturedLevel = effective;
            if (effective > 0.004)
            {
                _lastAudioCallbackTickMs = Environment.TickCount64;
                StartWaveformTimerLocked();
            }

            var prev = _state.OutputLevel;
            if (Math.Abs(prev - effective) < 0.008 && (effective == 0.0) == (prev == 0.0))
            {
                return;
            }

            PushWaveformSampleLocked(effective);
            UpdateStateLocked(CopyWith(_state,
                outputLevel: effective,
                waveformBars: SnapshotWaveformLocked()));
        }
        finally
        {
            Monitor.Exit(_sync);
        }
    }

    private void OnWaveformTick(object? state)
    {
        if (_disposing || !Monitor.TryEnter(_sync))
        {
            return;
        }

        try
        {
            if (_disposed || !_state.IsListening || _isMuted)
            {
                StopWaveformTimerLocked();
                return;
            }

            var elapsedSinceAudio = Environment.TickCount64 - _lastAudioCallbackTickMs;
            if (elapsedSinceAudio > 110)
            {
                // WASAPI loopback stops firing DataAvailable when playback pauses; decay smoothly to 0.
                _latestCapturedLevel = _latestCapturedLevel < 0.015 ? 0.0 : _latestCapturedLevel * 0.68;
            }

            PushWaveformSampleLocked(_latestCapturedLevel);
            var hasVisibleEnergy = _latestCapturedLevel > 0.0 || _waveformHistory.Any(h => h > WaveformBaselineHeight + 0.15);

            UpdateStateLocked(CopyWith(_state,
                outputLevel: _latestCapturedLevel,
                waveformBars: SnapshotWaveformLocked()));

            if (!hasVisibleEnergy)
            {
                StopWaveformTimerLocked();
            }
        }
        finally
        {
            Monitor.Exit(_sync);
        }
    }

    private void OnActiveEndpointDetected(object? sender, string friendlyName)
    {
        if (_disposing || string.IsNullOrWhiteSpace(friendlyName) || !Monitor.TryEnter(_sync))
        {
            return;
        }

        try
        {
            if (_disposed || !_state.IsListening)
            {
                return;
            }

            _lastDetectedSpeakingDevice = friendlyName;
            if (!string.Equals(_state.ActiveDeviceName, friendlyName, StringComparison.Ordinal))
            {
                UpdateStateLocked(CopyWith(_state,
                    activeDeviceName: friendlyName,
                    statusText: FormatActiveStatus(friendlyName, _isMuted)));
            }
        }
        finally
        {
            Monitor.Exit(_sync);
        }
    }

    private void StartWaveformTimerLocked()
    {
        try
        {
            _waveformDecayTimer.Change(45, 45);
        }
        catch
        {
            // Ignore if disposed.
        }
    }

    private void StopWaveformTimerLocked()
    {
        try
        {
            _waveformDecayTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch
        {
            // Ignore if disposed.
        }
    }

    private void PushWaveformSampleLocked(double level)
    {
        Array.Copy(_waveformHistory, 1, _waveformHistory, 0, WaveformBarCount - 1);
        var shaped = level > 0.003 ? Math.Pow(Math.Clamp(level, 0.0, 1.0), 0.72) : 0.0;
        _waveformHistory[WaveformBarCount - 1] = Math.Round(WaveformBaselineHeight + (shaped * WaveformMaxExtraHeight), 1);
    }

    private void ResetWaveformLocked() => Array.Fill(_waveformHistory, WaveformBaselineHeight);

    private IReadOnlyList<double> SnapshotWaveformLocked() => (double[])_waveformHistory.Clone();

    private void OnCaptureStoppedUnexpectedly(object? sender, Exception? ex)
    {
        _loggingService.Warning("System audio loopback stopped unexpectedly ({Message}); switching to current playback device.",
            ex?.Message ?? "Playback device changed");
        Task.Run(() =>
        {
            lock (_sync)
            {
                _activeEndpointSignature = string.Empty;
            }

            RefreshDevice("Playback device changed — reconnecting...");
        });
    }

    private void OnDevicesTopologyChanged(object? sender, EventArgs e)
    {
        try
        {
            if (!_disposed)
            {
                _topologyDebounceTimer.Change(140, Timeout.Infinite);
            }
        }
        catch
        {
            // Ignore if disposed.
        }
    }

    private void UpdateStateLocked(SystemAudioState next)
    {
        _state = next;
        StateChanged?.Invoke(this, next);
    }

    private static SystemAudioState CopyWith(
        SystemAudioState src,
        IReadOnlyList<SystemAudioDevice>? devices = null,
        string? selectedDeviceId = null,
        string? activeDeviceId = null,
        string? activeDeviceName = null,
        bool? isListening = null,
        bool? isMuted = null,
        double? outputLevel = null,
        IReadOnlyList<double>? waveformBars = null,
        bool? isDeviceAvailable = null,
        bool? hasError = null,
        string? statusText = null,
        string? errorMessage = null)
    {
        return new SystemAudioState
        {
            Devices = devices ?? src.Devices,
            SelectedDeviceId = selectedDeviceId ?? src.SelectedDeviceId,
            ActiveDeviceId = activeDeviceId ?? src.ActiveDeviceId,
            ActiveDeviceName = activeDeviceName ?? src.ActiveDeviceName,
            IsListening = isListening ?? src.IsListening,
            IsMuted = isMuted ?? src.IsMuted,
            OutputLevel = outputLevel ?? src.OutputLevel,
            WaveformBars = waveformBars ?? src.WaveformBars,
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
        _backend.ActiveEndpointDetected -= OnActiveEndpointDetected;
        _backend.CaptureStoppedUnexpectedly -= OnCaptureStoppedUnexpectedly;
        _backend.DevicesTopologyChanged -= OnDevicesTopologyChanged;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _waveformDecayTimer.Dispose();
            _topologyDebounceTimer.Dispose();
            StopCaptureLocked();
            _backend.Dispose();
        }
    }
}
