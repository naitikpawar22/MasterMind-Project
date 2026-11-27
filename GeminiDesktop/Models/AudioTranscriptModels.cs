namespace GeminiDesktop.Models;

/// <summary>
/// Distinguishes audio capture sources for metering, controls, and speech transcript attribution.
/// </summary>
public enum AudioSourceKind
{
    Microphone = 0,
    SystemAudio = 1
}

/// <summary>
/// Snapshot of Windows playback (WASAPI loopback) capture, mute state, and real-time audio activity.
/// </summary>
public sealed class SystemAudioState
{
    public string ActiveDeviceId { get; init; } = string.Empty;

    public string ActiveDeviceName { get; init; } = "No playback device detected";

    public bool IsListening { get; init; }

    public bool IsMuted { get; init; }

    public double OutputLevel { get; init; }

    public int OutputLevelPercent => (int)Math.Round(Math.Clamp(OutputLevel, 0.0, 1.0) * 100.0);

    public bool IsDeviceAvailable { get; init; }

    public bool HasError { get; init; }

    public string StatusText { get; init; } = "System audio ready";

    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Single finalized speech-to-text utterance clearly labeled by its audio capture source.
/// </summary>
public sealed record TranscriptEntry(
    Guid Id,
    AudioSourceKind Source,
    string DeviceName,
    string Text,
    DateTimeOffset Timestamp,
    double Confidence)
{
    public string SourceLabel => Source == AudioSourceKind.Microphone ? "Microphone" : "System Audio";

    public bool IsMicrophone => Source == AudioSourceKind.Microphone;

    public string TimeLabel => Timestamp.ToLocalTime().ToString("HH:mm:ss");

    public string FormattedLine => $"[{TimeLabel}] [{SourceLabel}] ({DeviceName}): {Text}";
}

/// <summary>
/// Live snapshot of dual-source real-time speech-to-text recognition.
/// </summary>
public sealed class TranscriptionState
{
    public bool IsEngineAvailable { get; init; } = true;

    public bool IsEnabled { get; init; } = true;

    public string RecognizerDisplayName { get; init; } = "Windows Speech Recognition";

    public bool IsMicrophoneTranscribing { get; init; }

    public bool IsSystemAudioTranscribing { get; init; }

    public string LiveMicrophoneText { get; init; } = string.Empty;

    public string LiveSystemAudioText { get; init; } = string.Empty;

    public IReadOnlyList<TranscriptEntry> Entries { get; init; } = [];

    public string StatusText { get; init; } = "Transcription ready";

    public string? ErrorMessage { get; init; }
}
