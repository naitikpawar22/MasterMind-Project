using System.Diagnostics;
using System.IO;
using GeminiDesktop.Models;
using ScreenRecorderLib;

namespace GeminiDesktop.Services;

/// <summary>
/// Abstraction over synchronized screen + dual-audio MP4 recording for full unit testability.
/// </summary>
public interface IMeetingRecorderBackend : IDisposable
{
    event EventHandler<string>? RecordingCompleted;
    event EventHandler<string>? RecordingFailed;

    void StartRecording(string outputFilePath, string? preferredMixCaptureHint, bool preferVirtualCableInput);
    void StopRecording();
}

/// <summary>
/// ScreenRecorderLib-backed Windows Media Foundation recorder capturing the desktop display alongside
/// synchronized mixed audio (CABLE Output when active, or System Speakers + Physical Mic with silent padding) into MP4.
/// </summary>
public sealed class ScreenRecorderLibBackend : IMeetingRecorderBackend
{
    private readonly IMicrophoneService _microphoneService;
    private readonly ISystemAudioService _systemAudioService;
    private readonly object _lock = new();

    private Recorder? _recorder;
    private ManualResetEventSlim? _stopGate;
    private string? _lastError;
    private bool _disposed;

    public ScreenRecorderLibBackend(
        IMicrophoneService microphoneService,
        ISystemAudioService systemAudioService)
    {
        _microphoneService = microphoneService;
        _systemAudioService = systemAudioService;
    }

    public event EventHandler<string>? RecordingCompleted;
    public event EventHandler<string>? RecordingFailed;

    public void StartRecording(string outputFilePath, string? preferredMixCaptureHint, bool preferVirtualCableInput)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFilePath);

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (_recorder is not null)
            {
                throw new InvalidOperationException("A recording session is already in progress.");
            }

            var directory = Path.GetDirectoryName(outputFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var audioSources = BuildAudioSources(preferredMixCaptureHint, preferVirtualCableInput);
            var options = RecorderOptions.DefaultMainMonitor;
            options.AudioOptions.IsAudioEnabled = audioSources.Count > 0;
            options.AudioOptions.Channels = AudioChannels.Stereo;
            options.AudioOptions.Bitrate = AudioBitrate.bitrate_192kbps;
            options.AudioOptions.AudioSources = audioSources;
            options.VideoEncoderOptions.Framerate = 30;
            options.VideoEncoderOptions.Quality = 75;
            options.VideoEncoderOptions.IsMp4FastStartEnabled = true;

            _lastError = null;
            _stopGate = new ManualResetEventSlim(false);

            var recorder = Recorder.CreateRecorder(options);
            recorder.OnRecordingComplete += OnRecorderComplete;
            recorder.OnRecordingFailed += OnRecorderFailed;

            _recorder = recorder;
            recorder.Record(outputFilePath);
        }
    }

    public void StopRecording()
    {
        Recorder? recorder;
        ManualResetEventSlim? gate;

        lock (_lock)
        {
            recorder = _recorder;
            gate = _stopGate;
        }

        if (recorder is null)
        {
            return;
        }

        try
        {
            recorder.Stop();
            gate?.Wait(TimeSpan.FromSeconds(10));
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_recorder, recorder))
                {
                    recorder.OnRecordingComplete -= OnRecorderComplete;
                    recorder.OnRecordingFailed -= OnRecorderFailed;
                    recorder.Dispose();
                    _recorder = null;
                }

                _stopGate?.Dispose();
                _stopGate = null;
            }
        }
    }

    private List<AudioSourceBase> BuildAudioSources(string? preferredMixCaptureHint, bool preferVirtualCableInput)
    {
        var sources = new List<AudioSourceBase>();
        var captureDevices = Recorder.GetSystemAudioCaptureDevices();
        var loopbackDevices = Recorder.GetSystemAudioLoopbackDevices();

        if (preferVirtualCableInput && !string.IsNullOrWhiteSpace(preferredMixCaptureHint))
        {
            var cableOut = captureDevices.FirstOrDefault(d =>
                d.FriendlyName.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase) ||
                d.FriendlyName.Contains(preferredMixCaptureHint, StringComparison.OrdinalIgnoreCase));

            if (cableOut is not null)
            {
                // Virtual cable output already carries the mixed Microphone + Teams System Audio feed.
                sources.Add(new CaptureAudioSource(cableOut.DeviceName) { IsAudioCaptureEnabled = true });
                return sources;
            }
        }

        // Standard fallback: record active System Audio output (Speakers) + active physical Microphone independently
        var sysActiveId = _systemAudioService.CurrentState.ActiveDeviceId;
        var loopbackMatch = !string.IsNullOrWhiteSpace(sysActiveId)
            ? loopbackDevices.FirstOrDefault(d => string.Equals(d.DeviceName, sysActiveId, StringComparison.OrdinalIgnoreCase))
            : null;
        loopbackMatch ??= loopbackDevices.FirstOrDefault(d => !d.FriendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase))
            ?? loopbackDevices.FirstOrDefault();

        if (loopbackMatch is not null)
        {
            sources.Add(new LoopbackAudioSource(loopbackMatch.DeviceName) { IsAudioCaptureEnabled = true });
        }

        var micActiveId = _microphoneService.CurrentState.ActiveDeviceId;
        var micMatch = !string.IsNullOrWhiteSpace(micActiveId)
            ? captureDevices.FirstOrDefault(d => string.Equals(d.DeviceName, micActiveId, StringComparison.OrdinalIgnoreCase))
            : null;
        micMatch ??= captureDevices.FirstOrDefault(d =>
            !Helpers.VirtualAudioDriverCatalog.IsVirtualLoopbackCaptureDevice(d.FriendlyName));

        if (micMatch is not null)
        {
            sources.Add(new CaptureAudioSource(micMatch.DeviceName) { IsAudioCaptureEnabled = true });
        }

        return sources;
    }

    private void OnRecorderComplete(object? sender, RecordingCompleteEventArgs e)
    {
        _stopGate?.Set();
        RecordingCompleted?.Invoke(this, e.FilePath);
    }

    private void OnRecorderFailed(object? sender, RecordingFailedEventArgs e)
    {
        _lastError = e.Error;
        _stopGate?.Set();
        RecordingFailed?.Invoke(this, string.IsNullOrWhiteSpace(e.Error) ? "Screen recording failed." : e.Error);
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
            StopRecording();
        }
        catch
        {
            // Ignore shutdown errors.
        }
    }
}

/// <summary>
/// Manages synchronized screen + dual-audio MP4 recording in Documents\MeetingRecordings.
/// </summary>
public interface IMeetingRecorderService : IDisposable
{
    MeetingRecordingState CurrentState { get; }

    event EventHandler<MeetingRecordingState>? StateChanged;

    void StartRecording();
    void StopRecording();
    void ToggleRecording();
    void OpenRecordingsFolder();
}

public sealed class MeetingRecorderService : IMeetingRecorderService
{
    private readonly IMeetingRecorderBackend _backend;
    private readonly IDualAudioMixerService _mixerService;
    private readonly ILoggingService _loggingService;
    private readonly object _sync = new();
    private readonly Timer _elapsedTimer;
    private readonly string _recordingsDirectory;

    private MeetingRecordingState _state;
    private DateTimeOffset _startedAt;
    private string? _activeFilePath;
    private string? _lastCompletedPath;
    private bool _disposed;

    public MeetingRecorderService(
        IMeetingRecorderBackend backend,
        IDualAudioMixerService mixerService,
        ILoggingService loggingService,
        string? recordingsDirectoryOverride = null)
    {
        _backend = backend;
        _mixerService = mixerService;
        _loggingService = loggingService;

        _recordingsDirectory = !string.IsNullOrWhiteSpace(recordingsDirectoryOverride)
            ? recordingsDirectoryOverride
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MeetingRecordings");

        _state = new MeetingRecordingState
        {
            RecordingsDirectory = _recordingsDirectory,
            StatusText = "Recorder ready (Documents\\MeetingRecordings)"
        };

        _elapsedTimer = new Timer(OnElapsedTick, null, Timeout.Infinite, Timeout.Infinite);
        _backend.RecordingCompleted += OnBackendRecordingCompleted;
        _backend.RecordingFailed += OnBackendRecordingFailed;
    }

    public MeetingRecordingState CurrentState => _state;

    public event EventHandler<MeetingRecordingState>? StateChanged;

    public void StartRecording()
    {
        lock (_sync)
        {
            if (_disposed || _state.IsRecording || _state.IsStartingOrStopping)
            {
                return;
            }

            Directory.CreateDirectory(_recordingsDirectory);
            var fileName = $"Meeting_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
            var filePath = Path.Combine(_recordingsDirectory, fileName);

            _activeFilePath = filePath;
            _startedAt = DateTimeOffset.UtcNow;

            UpdateStateLocked(new MeetingRecordingState
            {
                IsRecording = false,
                IsStartingOrStopping = true,
                Elapsed = TimeSpan.Zero,
                RecordingsDirectory = _recordingsDirectory,
                CurrentFilePath = filePath,
                LastCompletedFilePath = _lastCompletedPath,
                StatusText = "Starting MP4 recording..."
            });

            try
            {
                var mixState = _mixerService.CurrentState;
                _backend.StartRecording(filePath, mixState.PairedCaptureMicHint, preferVirtualCableInput: mixState.IsStreaming);

                _elapsedTimer.Change(500, 500);
                UpdateStateLocked(new MeetingRecordingState
                {
                    IsRecording = true,
                    IsStartingOrStopping = false,
                    Elapsed = TimeSpan.Zero,
                    RecordingsDirectory = _recordingsDirectory,
                    CurrentFilePath = filePath,
                    LastCompletedFilePath = _lastCompletedPath,
                    StatusText = $"Recording → {fileName}"
                });

                _loggingService.Information("Meeting MP4 recording started: {Path} (PreferVirtualCable={UseCable})",
                    filePath, mixState.IsStreaming);
            }
            catch (Exception ex)
            {
                _activeFilePath = null;
                _elapsedTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _loggingService.Warning("Failed to start MP4 recording: {Message}", ex.Message);

                UpdateStateLocked(new MeetingRecordingState
                {
                    IsRecording = false,
                    IsStartingOrStopping = false,
                    Elapsed = TimeSpan.Zero,
                    RecordingsDirectory = _recordingsDirectory,
                    LastCompletedFilePath = _lastCompletedPath,
                    StatusText = "Recording failed to start",
                    ErrorMessage = $"Could not start screen + audio MP4 recording: {ex.Message}"
                });
            }
        }
    }

    public void StopRecording()
    {
        lock (_sync)
        {
            if (_disposed || !_state.IsRecording)
            {
                return;
            }

            _elapsedTimer.Change(Timeout.Infinite, Timeout.Infinite);
            UpdateStateLocked(new MeetingRecordingState
            {
                IsRecording = false,
                IsStartingOrStopping = true,
                Elapsed = DateTimeOffset.UtcNow - _startedAt,
                RecordingsDirectory = _recordingsDirectory,
                CurrentFilePath = _activeFilePath,
                LastCompletedFilePath = _lastCompletedPath,
                StatusText = "Finalizing MP4..."
            });
        }

        try
        {
            _backend.StopRecording();
        }
        catch (Exception ex)
        {
            _loggingService.Warning("Error while stopping MP4 recording: {Message}", ex.Message);
        }
    }

    public void ToggleRecording()
    {
        if (_state.IsRecording)
        {
            StopRecording();
        }
        else
        {
            StartRecording();
        }
    }

    public void OpenRecordingsFolder()
    {
        try
        {
            Directory.CreateDirectory(_recordingsDirectory);
            Process.Start(new ProcessStartInfo(_recordingsDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _loggingService.Warning("Unable to open MeetingRecordings folder: {Error}", ex.Message);
        }
    }

    private void OnElapsedTick(object? state)
    {
        if (!Monitor.TryEnter(_sync))
        {
            return;
        }

        try
        {
            if (_disposed || !_state.IsRecording)
            {
                return;
            }

            var elapsed = DateTimeOffset.UtcNow - _startedAt;
            UpdateStateLocked(new MeetingRecordingState
            {
                IsRecording = true,
                IsStartingOrStopping = false,
                Elapsed = elapsed,
                RecordingsDirectory = _recordingsDirectory,
                CurrentFilePath = _activeFilePath,
                LastCompletedFilePath = _lastCompletedPath,
                StatusText = _state.StatusText
            });
        }
        finally
        {
            Monitor.Exit(_sync);
        }
    }

    private void OnBackendRecordingCompleted(object? sender, string filePath)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _elapsedTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _lastCompletedPath = string.IsNullOrWhiteSpace(filePath) ? _activeFilePath : filePath;
            _activeFilePath = null;
            var fileName = Path.GetFileName(_lastCompletedPath ?? "recording.mp4");

            UpdateStateLocked(new MeetingRecordingState
            {
                IsRecording = false,
                IsStartingOrStopping = false,
                Elapsed = TimeSpan.Zero,
                RecordingsDirectory = _recordingsDirectory,
                LastCompletedFilePath = _lastCompletedPath,
                StatusText = $"Saved {fileName}"
            });

            _loggingService.Information("Meeting MP4 recording saved: {Path}", _lastCompletedPath);
        }
    }

    private void OnBackendRecordingFailed(object? sender, string error)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _elapsedTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _activeFilePath = null;

            UpdateStateLocked(new MeetingRecordingState
            {
                IsRecording = false,
                IsStartingOrStopping = false,
                Elapsed = TimeSpan.Zero,
                RecordingsDirectory = _recordingsDirectory,
                LastCompletedFilePath = _lastCompletedPath,
                StatusText = "Recording error",
                ErrorMessage = error
            });

            _loggingService.Warning("Meeting MP4 recording failed: {Error}", error);
        }
    }

    private void UpdateStateLocked(MeetingRecordingState next)
    {
        _state = next;
        StateChanged?.Invoke(this, next);
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
            _elapsedTimer.Dispose();
        }

        _backend.RecordingCompleted -= OnBackendRecordingCompleted;
        _backend.RecordingFailed -= OnBackendRecordingFailed;
        _backend.Dispose();
    }
}
