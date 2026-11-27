namespace GeminiDesktop.Models;

/// <summary>
/// Maps to documented Win32 SetWindowDisplayAffinity flags in user32.dll.
/// </summary>
public enum WindowDisplayAffinityMode : uint
{
    None = 0x00000000,
    MonitorOnly = 0x00000001,
    ExcludeFromCapture = 0x00000011
}

/// <summary>
/// Represents the transparent, real-time privacy and capture-exclusion capabilities of the desktop window.
/// </summary>
public sealed class PrivacyStatus
{
    public bool IsCaptureExclusionActive { get; init; }

    public WindowDisplayAffinityMode AppliedAffinity { get; init; } = WindowDisplayAffinityMode.None;

    public bool IsExcludeFromCaptureSupported { get; init; }

    public bool IsMonitorAffinitySupported { get; init; }

    public bool IsAlwaysOnTopActive { get; init; }

    public string OsVersionDescription { get; init; } = string.Empty;

    public string CapabilitySummary { get; init; } = string.Empty;

    public string StatusSummary { get; init; } = "Privacy Mode: Off (Standard Visibility)";

    public string SharingExplanation { get; init; } = string.Empty;

    public string LimitationNotice { get; init; } = string.Empty;

    public int LastWin32ErrorCode { get; init; }

    public string? LastErrorMessage { get; init; }

    public bool HasAnyActivePrivacyMode => IsCaptureExclusionActive || IsAlwaysOnTopActive;
}
