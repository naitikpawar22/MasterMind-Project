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
/// Represents an active Windows playback (DataFlow.Render) device or smart loopback target.
/// </summary>
public sealed record SystemAudioDevice(
    string Id,
    string FriendlyName,
    bool IsDefaultMultimedia = false,
    bool IsDefaultCommunications = false,
    bool IsAutoOption = false);

/// <summary>
/// Snapshot of Windows playback (WASAPI loopback) capture, mute state, waveform bars, and real-time audio activity.
/// </summary>
public sealed class SystemAudioState
{
    public static IReadOnlyList<double> CreateSilentWaveform(int barCount = 20, double baselineHeight = 3.0) =>
        Enumerable.Repeat(baselineHeight, barCount).ToArray();

    public IReadOnlyList<SystemAudioDevice> Devices { get; init; } = [];

    public string SelectedDeviceId { get; init; } = string.Empty;

    public string ActiveDeviceId { get; init; } = string.Empty;

    public string ActiveDeviceName { get; init; } = "No playback device detected";

    public bool IsListening { get; init; }

    public bool IsMuted { get; init; }

    public double OutputLevel { get; init; }

    public int OutputLevelPercent => (int)Math.Round(Math.Clamp(OutputLevel, 0.0, 1.0) * 100.0);

    public IReadOnlyList<double> WaveformBars { get; init; } = CreateSilentWaveform();

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

/// <summary>
/// Represents a supported signed virtual audio playback device (e.g., VB-Audio Virtual Cable input)
/// capable of feeding mixed audio into a paired Windows virtual capture microphone for Gemini.
/// </summary>
public sealed record VirtualAudioOutputDevice(
    string Id,
    string FriendlyName,
    string PairedCaptureHint);

/// <summary>
/// Live state of synchronized Microphone + System Audio mixing into a signed Windows virtual microphone driver.
/// </summary>
public sealed class MixedAudioState
{
    public bool IsEnabled { get; init; }

    public bool IsStreaming { get; init; }

    public bool IsDriverInstalled { get; init; }

    public IReadOnlyList<VirtualAudioOutputDevice> AvailableOutputs { get; init; } = [];

    public string SelectedOutputDeviceId { get; init; } = string.Empty;

    public string ActiveOutputDeviceName { get; init; } = "No virtual audio driver detected";

    public string PairedCaptureMicHint { get; init; } = "CABLE Output (VB-Audio Virtual Cable)";

    public int MicrophoneVolumePercent { get; init; } = 100;

    public int SystemAudioVolumePercent { get; init; } = 100;

    public bool IsMicrophoneMixMuted { get; init; }

    public bool IsSystemAudioMixMuted { get; init; }

    public double MixedOutputLevel { get; init; }

    public int MixedOutputLevelPercent => (int)Math.Round(Math.Clamp(MixedOutputLevel, 0.0, 1.0) * 100.0);

    public string StatusText { get; init; } = "Mixed Gemini feed off";

    public string? DependencyMessage { get; init; }

    public string? ErrorMessage { get; init; }
}
