namespace GeminiDesktop.Models;

/// <summary>
/// Represents an active physical Windows audio capture (microphone) device or the System Default selector.
/// </summary>
public sealed record MicrophoneDevice(
    string Id,
    string FriendlyName,
    bool IsDefault,
    bool IsSystemDefaultOption = false)
{
    public override string ToString() => FriendlyName;
}

/// <summary>
/// Represents combined OS and application microphone permission state.
/// </summary>
public enum MicrophonePermissionState
{
    Allowed = 0,
    DeniedByWindows = 1,
    DisabledByAppSetting = 2,
    Unknown = 3
}

/// <summary>
/// Snapshot of the physical microphone capture, device availability, mute state, and real-time audio level.
/// </summary>
public sealed class MicrophoneState
{
    public IReadOnlyList<MicrophoneDevice> Devices { get; init; } = [];

    public string SelectedDeviceId { get; init; } = string.Empty;

    public string ActiveDeviceId { get; init; } = string.Empty;

    public string ActiveDeviceName { get; init; } = "No microphone detected";

    public MicrophonePermissionState PermissionState { get; init; } = MicrophonePermissionState.Allowed;

    public bool IsWorkspaceMicrophoneAllowed { get; init; } = true;

    public bool IsMonitoring { get; init; }

    public bool IsMuted { get; init; }

    public double InputLevel { get; init; }

    public int InputLevelPercent => (int)Math.Round(Math.Clamp(InputLevel, 0.0, 1.0) * 100.0);

    public bool IsDeviceAvailable { get; init; }

    public bool HasError { get; init; }

    public string StatusText { get; init; } = "Microphone ready";

    public string? ErrorMessage { get; init; }
}
