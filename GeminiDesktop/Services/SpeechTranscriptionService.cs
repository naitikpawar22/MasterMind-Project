using System.Globalization;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Text;
using GeminiDesktop.Helpers;
using GeminiDesktop.Models;

namespace GeminiDesktop.Services;

public sealed class SpeechTranscriptEventArgs(
    AudioSourceKind source,
    string deviceName,
    string text,
    double confidence) : EventArgs
{
    public AudioSourceKind Source { get; } = source;
    public string DeviceName { get; } = deviceName;
    public string Text { get; } = text;
    public double Confidence { get; } = confidence;
}

/// <summary>
/// Abstraction over continuous per-source real-time speech recognition so Microphone and System Audio
/// transcribe independently and concurrently with full unit testability.
/// </summary>
public interface ISpeechTranscriptionBackend : IDisposable
{
    event EventHandler<SpeechTranscriptEventArgs>? HypothesisReceived;
    event EventHandler<SpeechTranscriptEventArgs>? FinalTranscriptReceived;
    event EventHandler<string>? EngineError;

    bool TryInitialize(out string recognizerDisplayName, out string? errorMessage);
    bool StartSource(AudioSourceKind source, string deviceName);
    void StopSource(AudioSourceKind source);
    void PushPcm16k(AudioSourceKind source, ReadOnlySpan<byte> pcm16kMonoBytes);
}

/// <summary>
/// Offline Windows Desktop SAPI speech recognizer (System.Speech) running isolated continuous streams
/// for both Microphone and System Audio loopback simultaneously.
/// </summary>
public sealed class SapiSpeechTranscriptionBackend : ISpeechTranscriptionBackend
{
    private sealed class SourcePipeline : IDisposable
    {
        private readonly AudioSourceKind _source;
        private readonly SapiSpeechTranscriptionBackend _owner;
        private readonly SpeechRecognitionEngine _engine;
        private readonly ContinuousPcmAudioStream _stream;
        private readonly ManualResetEventSlim _completed = new(false);

        private string _deviceName;
        private int _disposed;

        public SourcePipeline(AudioSourceKind source, string deviceName, RecognizerInfo recognizer, SapiSpeechTranscriptionBackend owner)
        {
            _source = source;
            _deviceName = deviceName;
            _owner = owner;
            _stream = new ContinuousPcmAudioStream();
            _engine = new SpeechRecognitionEngine(recognizer);

            try
            {
                _engine.BabbleTimeout = TimeSpan.Zero;
                _engine.InitialSilenceTimeout = TimeSpan.Zero;
                _engine.EndSilenceTimeout = TimeSpan.FromMilliseconds(450);
                _engine.EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(650);
                _engine.LoadGrammar(new DictationGrammar());

                _engine.SpeechHypothesized += OnSpeechHypothesized;
                _engine.SpeechRecognized += OnSpeechRecognized;
                _engine.RecognizeCompleted += OnRecognizeCompleted;

                var format = new SpeechAudioFormatInfo(
                    AudioProcessingHelper.TargetSpeechSampleRate,
                    AudioBitsPerSample.Sixteen,
                    AudioChannel.Mono);
                _engine.SetInputToAudioStream(_stream, format);
                _engine.RecognizeAsync(RecognizeMode.Multiple);
            }
            catch
            {
                _stream.Dispose();
                _engine.Dispose();
                _completed.Dispose();
                throw;
            }
        }

        public void UpdateDeviceName(string deviceName) => Volatile.Write(ref _deviceName, deviceName);

        public void PushPcm16k(ReadOnlySpan<byte> pcm) => _stream.WritePcm(pcm);

        private void OnSpeechHypothesized(object? sender, SpeechHypothesizedEventArgs e)
        {
            var text = e.Result?.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                _owner.HypothesisReceived?.Invoke(_owner,
                    new SpeechTranscriptEventArgs(_source, Volatile.Read(ref _deviceName), text, e.Result!.Confidence));
            }
        }

        private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
        {
            var text = e.Result?.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(text) && text.Length >= 2 && e.Result!.Confidence >= 0.02f)
            {
                _owner.FinalTranscriptReceived?.Invoke(_owner,
                    new SpeechTranscriptEventArgs(_source, Volatile.Read(ref _deviceName), text, e.Result.Confidence));
            }
        }

        private void OnRecognizeCompleted(object? sender, RecognizeCompletedEventArgs e)
        {
            _completed.Set();
            if (e.Error is not null && Volatile.Read(ref _disposed) == 0)
            {
                _owner.EngineError?.Invoke(_owner, $"Speech recognizer error ({_source}): {e.Error.Message}");
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _engine.SpeechHypothesized -= OnSpeechHypothesized;
            _engine.SpeechRecognized -= OnSpeechRecognized;
            _engine.RecognizeCompleted -= OnRecognizeCompleted;

            // Complete stream first so Read() returns 0 cleanly without stalling SAPI thread.
            _stream.Complete();
            try
            {
                _engine.RecognizeAsyncCancel();
            }
            catch
            {
                // Ignore cancel errors if recognizer already stopped.
            }

            _completed.Wait(350);

            try
            {
                _engine.Dispose();
            }
            catch
            {
                // Ignore COM teardown issues.
            }

            _stream.Dispose();
            _completed.Dispose();
        }
    }

    private readonly object _lock = new();
    private readonly Dictionary<AudioSourceKind, SourcePipeline> _pipelines = [];

    private RecognizerInfo? _selectedRecognizer;
    private bool _disposed;

    public event EventHandler<SpeechTranscriptEventArgs>? HypothesisReceived;
    public event EventHandler<SpeechTranscriptEventArgs>? FinalTranscriptReceived;
    public event EventHandler<string>? EngineError;

    public bool TryInitialize(out string recognizerDisplayName, out string? errorMessage)
    {
        recognizerDisplayName = "Unavailable";
        errorMessage = null;

        try
        {
            var installed = SpeechRecognitionEngine.InstalledRecognizers();
            if (installed.Count == 0)
            {
                errorMessage = "Windows Speech Recognition desktop language pack is not installed. Enable Speech in Windows Settings → Time & language → Speech.";
                return false;
            }

            var uiCulture = CultureInfo.CurrentUICulture;
            _selectedRecognizer =
                installed.FirstOrDefault(r => string.Equals(r.Culture.Name, uiCulture.Name, StringComparison.OrdinalIgnoreCase))
                ?? installed.FirstOrDefault(r => string.Equals(r.Culture.Name, "en-US", StringComparison.OrdinalIgnoreCase))
                ?? installed.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName.Equals("en", StringComparison.OrdinalIgnoreCase))
                ?? installed[0];

            recognizerDisplayName = $"{_selectedRecognizer.Description} ({_selectedRecognizer.Culture.Name})";
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"Could not initialize Windows Speech Recognition: {ex.Message}";
            return false;
        }
    }

    public bool StartSource(AudioSourceKind source, string deviceName)
    {
        SourcePipeline? pipelineToStart = null;
        lock (_lock)
        {
            if (_disposed || _selectedRecognizer is null)
            {
                return false;
            }

            if (_pipelines.TryGetValue(source, out var existing))
            {
                existing.UpdateDeviceName(deviceName);
                return true;
            }

            try
            {
                pipelineToStart = new SourcePipeline(source, deviceName, _selectedRecognizer, this);
                _pipelines[source] = pipelineToStart;
                return true;
            }
            catch (Exception ex)
            {
                pipelineToStart?.Dispose();
                EngineError?.Invoke(this, $"Could not start transcription for {source}: {ex.Message}");
                return false;
            }
        }
    }

    public void StopSource(AudioSourceKind source)
    {
        SourcePipeline? toDispose = null;
        lock (_lock)
        {
            if (_pipelines.Remove(source, out var existing))
            {
                toDispose = existing;
            }
        }

        // Dispose outside lock so audio push threads never block waiting on teardown.
        toDispose?.Dispose();
    }

    public void PushPcm16k(AudioSourceKind source, ReadOnlySpan<byte> pcm16kMonoBytes)
    {
        if (pcm16kMonoBytes.IsEmpty)
        {
            return;
        }

        SourcePipeline? target;
        lock (_lock)
        {
            target = _pipelines.TryGetValue(source, out var pipeline) && !_disposed ? pipeline : null;
        }

        target?.PushPcm16k(pcm16kMonoBytes);
    }

    public void Dispose()
    {
        List<SourcePipeline> toDispose;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            toDispose = [.. _pipelines.Values];
            _pipelines.Clear();
        }

        foreach (var pipeline in toDispose)
        {
            pipeline.Dispose();
        }
    }
}

/// <summary>
/// Coordinates simultaneous real-time speech-to-text across Microphone Audio and Windows System Audio,
/// producing labeled live hypotheses and finalized chronological transcript entries.
/// </summary>
public interface ISpeechTranscriptionService : IDisposable
{
    TranscriptionState CurrentState { get; }

    event EventHandler<TranscriptionState>? StateChanged;

    void Initialize();
    void SetEnabled(bool enabled);
    void StartAllSources();
    void StopAllSources();
    void ClearTranscript();
    string FormatTranscriptText();
}

public sealed class SpeechTranscriptionService : ISpeechTranscriptionService
{
    private const int MaxTranscriptEntries = 250;

    private readonly ISpeechTranscriptionBackend _backend;
    private readonly IMicrophoneService _microphoneService;
    private readonly ISystemAudioService _systemAudioService;
    private readonly ISettingsService _settingsService;
    private readonly ILoggingService _loggingService;
    private readonly object _sync = new();
    private readonly List<TranscriptEntry> _entries = [];

    private bool _engineAvailable;
    private bool _isEnabled;
    private string _recognizerDisplayName = "Initializing...";
    private string _liveMicText = string.Empty;
    private string _liveSystemText = string.Empty;
    private string? _errorMessage;
    private bool _micTranscribing;
    private bool _systemTranscribing;
    private (bool Active, string Device) _lastMicPipelineKey;
    private (bool Active, string Device) _lastSysPipelineKey;
    private TranscriptionState _state = new();
    private bool _disposed;

    public SpeechTranscriptionService(
        ISpeechTranscriptionBackend backend,
        IMicrophoneService microphoneService,
        ISystemAudioService systemAudioService,
        ISettingsService settingsService,
        ILoggingService loggingService)
    {
        _backend = backend;
        _microphoneService = microphoneService;
        _systemAudioService = systemAudioService;
        _settingsService = settingsService;
        _loggingService = loggingService;

        _isEnabled = _settingsService.CurrentSettings.EnableLiveSpeechTranscription;

        _backend.HypothesisReceived += OnHypothesisReceived;
        _backend.FinalTranscriptReceived += OnFinalTranscriptReceived;
        _backend.EngineError += OnEngineError;
        _microphoneService.StateChanged += OnMicrophoneStateChanged;
        _systemAudioService.StateChanged += OnSystemAudioStateChanged;
    }

    public TranscriptionState CurrentState => _state;

    public event EventHandler<TranscriptionState>? StateChanged;

    public void Initialize()
    {
        bool initialized;
        string displayName;
        string? err;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            initialized = _backend.TryInitialize(out displayName, out err);
            _engineAvailable = initialized;
            _recognizerDisplayName = displayName;
            _errorMessage = err;

            if (!initialized)
            {
                _loggingService.Warning("Speech transcription unavailable: {Error}", err ?? "Unknown");
            }
            else
            {
                _loggingService.Information("Speech transcription engine ready: {Recognizer}", displayName);
            }
        }

        _microphoneService.SetPcmSink(pcm => PushSourceAudio(AudioSourceKind.Microphone, pcm));
        _systemAudioService.SetPcmSink(pcm => PushSourceAudio(AudioSourceKind.SystemAudio, pcm));

        SyncActivePipelines();
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
            updated.EnableLiveSpeechTranscription = enabled;
            _settingsService.SaveSettings(updated);
        }

        SyncActivePipelines();
    }

    public void StartAllSources()
    {
        SetEnabled(true);
        _microphoneService.StartListening();
        _systemAudioService.StartListening();
    }

    public void StopAllSources()
    {
        _microphoneService.StopListening();
        _systemAudioService.StopListening();
    }

    public void ClearTranscript()
    {
        lock (_sync)
        {
            _entries.Clear();
            _liveMicText = string.Empty;
            _liveSystemText = string.Empty;
            PublishStateLocked();
        }
    }

    public string FormatTranscriptText()
    {
        lock (_sync)
        {
            var sb = new StringBuilder();
            foreach (var entry in _entries)
            {
                sb.AppendLine(entry.FormattedLine);
            }

            return sb.ToString().TrimEnd();
        }
    }

    private void PushSourceAudio(AudioSourceKind source, ReadOnlySpan<byte> pcm)
    {
        if (!_engineAvailable || !_isEnabled || _disposed)
        {
            return;
        }

        _backend.PushPcm16k(source, pcm);
    }

    private void OnMicrophoneStateChanged(object? sender, MicrophoneState state)
    {
        var nextKey = (state.IsMonitoring && state.IsDeviceAvailable, state.ActiveDeviceName);
        if (nextKey == _lastMicPipelineKey)
        {
            return;
        }

        SyncActivePipelines();
    }

    private void OnSystemAudioStateChanged(object? sender, SystemAudioState state)
    {
        var nextKey = (state.IsListening && state.IsDeviceAvailable, state.ActiveDeviceName);
        if (nextKey == _lastSysPipelineKey)
        {
            return;
        }

        SyncActivePipelines();
    }

    private void SyncActivePipelines()
    {
        var micState = _microphoneService.CurrentState;
        var sysState = _systemAudioService.CurrentState;

        var wantMic = _engineAvailable && _isEnabled && micState.IsMonitoring && micState.IsDeviceAvailable;
        var wantSys = _engineAvailable && _isEnabled && sysState.IsListening && sysState.IsDeviceAvailable;

        _lastMicPipelineKey = (micState.IsMonitoring && micState.IsDeviceAvailable, micState.ActiveDeviceName);
        _lastSysPipelineKey = (sysState.IsListening && sysState.IsDeviceAvailable, sysState.ActiveDeviceName);

        if (wantMic)
        {
            wantMic = _backend.StartSource(AudioSourceKind.Microphone, micState.ActiveDeviceName);
        }
        else
        {
            _backend.StopSource(AudioSourceKind.Microphone);
        }

        if (wantSys)
        {
            wantSys = _backend.StartSource(AudioSourceKind.SystemAudio, sysState.ActiveDeviceName);
        }
        else
        {
            _backend.StopSource(AudioSourceKind.SystemAudio);
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _micTranscribing = wantMic;
            _systemTranscribing = wantSys;
            if (!wantMic)
            {
                _liveMicText = string.Empty;
            }

            if (!wantSys)
            {
                _liveSystemText = string.Empty;
            }

            PublishStateLocked();
        }
    }

    private void OnHypothesisReceived(object? sender, SpeechTranscriptEventArgs e)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (e.Source == AudioSourceKind.Microphone)
            {
                _liveMicText = e.Text;
            }
            else
            {
                _liveSystemText = e.Text;
            }

            PublishStateLocked();
        }
    }

    private void OnFinalTranscriptReceived(object? sender, SpeechTranscriptEventArgs e)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            var entry = new TranscriptEntry(
                Guid.NewGuid(),
                e.Source,
                e.DeviceName,
                e.Text,
                DateTimeOffset.Now,
                e.Confidence);

            _entries.Add(entry);
            if (_entries.Count > MaxTranscriptEntries)
            {
                _entries.RemoveAt(0);
            }

            if (e.Source == AudioSourceKind.Microphone)
            {
                _liveMicText = string.Empty;
            }
            else
            {
                _liveSystemText = string.Empty;
            }

            PublishStateLocked();
        }
    }

    private void OnEngineError(object? sender, string message)
    {
        _loggingService.Warning("Speech transcription warning: {Message}", message);
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _errorMessage = message;
            PublishStateLocked();
        }
    }

    private void PublishStateLocked()
    {
        var status = !_engineAvailable
            ? "Speech recognition unavailable"
            : !_isEnabled
                ? "Live transcription paused"
                : (_micTranscribing, _systemTranscribing) switch
                {
                    (true, true) => "Transcribing Microphone + System Audio",
                    (true, false) => "Transcribing Microphone",
                    (false, true) => "Transcribing System Audio",
                    _ => "Waiting for audio source (Start Mic or System Audio)"
                };

        _state = new TranscriptionState
        {
            IsEngineAvailable = _engineAvailable,
            IsEnabled = _isEnabled,
            RecognizerDisplayName = _recognizerDisplayName,
            IsMicrophoneTranscribing = _micTranscribing,
            IsSystemAudioTranscribing = _systemTranscribing,
            LiveMicrophoneText = _liveMicText,
            LiveSystemAudioText = _liveSystemText,
            Entries = _entries.ToArray(),
            ErrorMessage = _errorMessage,
            StatusText = status
        };

        StateChanged?.Invoke(this, _state);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _microphoneService.SetPcmSink(null);
        _systemAudioService.SetPcmSink(null);

        _microphoneService.StateChanged -= OnMicrophoneStateChanged;
        _systemAudioService.StateChanged -= OnSystemAudioStateChanged;
        _backend.HypothesisReceived -= OnHypothesisReceived;
        _backend.FinalTranscriptReceived -= OnFinalTranscriptReceived;
        _backend.EngineError -= OnEngineError;
        _backend.Dispose();
    }
}
