using System.IO;
using GeminiDesktop.Models;
using GeminiDesktop.Services;
using NAudio.Wave;
using Xunit;

namespace GeminiDesktop.Tests;

public sealed class MicrophoneServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "MyLaptopMicTests_" + Guid.NewGuid().ToString("N"));

    private sealed class FakeMicrophoneBackend : IMicrophoneBackend
    {
        public List<MicrophoneDevice> Devices { get; } =
        [
            new MicrophoneDevice("mic-default", "Built-in Microphone Array", IsDefault: true),
            new MicrophoneDevice("mic-usb", "USB Headset Microphone", IsDefault: false)
        ];

        public string? DefaultId { get; set; } = "mic-default";
        public MicrophonePermissionState WindowsPermission { get; set; } = MicrophonePermissionState.Allowed;
        public Dictionary<string, bool> MuteMap { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["mic-default"] = false,
            ["mic-usb"] = false
        };
        public bool ThrowUnauthorizedOnStart { get; set; }
        public string? StartedDeviceId { get; private set; }
        public int StopCalls { get; private set; }

        public event EventHandler<double>? AudioLevelMeasured;
        public event EventHandler<Exception?>? CaptureStoppedUnexpectedly;
        public event EventHandler? DevicesTopologyChanged;
        public event EventHandler<bool>? ExternalMuteChanged;

        public MicrophonePermissionState CheckWindowsPermission() => WindowsPermission;

        public IReadOnlyList<MicrophoneDevice> EnumerateCaptureDevices() => Devices.ToList();

        public string? ResolveDefaultCaptureDeviceId() => DefaultId;

        public bool TryGetEndpointMute(string deviceId, out bool isMuted)
        {
            if (MuteMap.TryGetValue(deviceId, out isMuted))
            {
                return true;
            }

            isMuted = false;
            return false;
        }

        public bool TrySetEndpointMute(string deviceId, bool muted)
        {
            MuteMap[deviceId] = muted;
            return true;
        }

        public void StartCapture(string deviceId)
        {
            if (ThrowUnauthorizedOnStart)
            {
                throw new UnauthorizedAccessException("Microphone access denied by OS.");
            }

            StartedDeviceId = deviceId;
        }

        public void StopCapture() => StopCalls++;

        public void EmitLevel(double level) => AudioLevelMeasured?.Invoke(this, level);
        public void EmitDisconnect(Exception ex) => CaptureStoppedUnexpectedly?.Invoke(this, ex);
        public void EmitTopologyChange() => DevicesTopologyChanged?.Invoke(this, EventArgs.Empty);
        public void EmitExternalMute(bool muted) => ExternalMuteChanged?.Invoke(this, muted);

        public void Dispose() { }
    }

    [Fact]
    public void ComputePeakAmplitude_SupportsFloat32AndPcm16()
    {
        // IEEE Float 32-bit sample at 0.5 and -0.75
        var floatBuf = new byte[8];
        Buffer.BlockCopy(BitConverter.GetBytes(0.50f), 0, floatBuf, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(-0.75f), 0, floatBuf, 4, 4);
        var floatFmt = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);

        var peakFloat = MicrophoneService.ComputePeakAmplitude(floatBuf, floatBuf.Length, floatFmt);
        Assert.Equal(0.75d, peakFloat, 2);

        // 16-bit PCM sample at half full-scale
        var pcmBuf = BitConverter.GetBytes((short)16384);
        var pcmFmt = new WaveFormat(44100, 16, 1);

        var peakPcm = MicrophoneService.ComputePeakAmplitude(pcmBuf, pcmBuf.Length, pcmFmt);
        Assert.Equal(0.50d, peakPcm, 2);
    }

    [Fact]
    public void InitializeAndSelectDevice_PersistsChoiceAndTracksLevelsAndMute()
    {
        using var log = new LoggingService(_tempRoot);
        var settingsService = new SettingsService(log, _tempRoot, allowRegistryStartupChanges: false);
        var backend = new FakeMicrophoneBackend();
        using var micService = new MicrophoneService(backend, settingsService, log);

        micService.Initialize();
        Assert.True(micService.CurrentState.IsMonitoring);
        Assert.Equal("mic-default", backend.StartedDeviceId);
        Assert.Equal(3, micService.CurrentState.Devices.Count); // System Default + 2 physical mics

        backend.EmitLevel(0.62);
        Assert.Equal(62, micService.CurrentState.InputLevelPercent);

        // Switch to USB microphone
        micService.SelectDevice("mic-usb");
        Assert.Equal("mic-usb", backend.StartedDeviceId);
        Assert.Equal("mic-usb", settingsService.CurrentSettings.PreferredMicrophoneId);

        // Mute zeros meter level and toggles endpoint mute
        micService.SetMuted(true);
        Assert.True(micService.CurrentState.IsMuted);
        Assert.True(backend.MuteMap["mic-usb"]);
        Assert.Equal(0, micService.CurrentState.InputLevelPercent);

        // Even if backend emits audio frames while muted, InputLevel remains 0
        backend.EmitLevel(0.85);
        Assert.Equal(0, micService.CurrentState.InputLevelPercent);

        // Unmute restores live metering
        micService.SetMuted(false);
        Assert.False(micService.CurrentState.IsMuted);
        Assert.False(backend.MuteMap["mic-usb"]);
    }

    [Fact]
    public void WindowsPermissionDenied_DisablesCaptureAndReportsHelpfulError()
    {
        using var log = new LoggingService(_tempRoot);
        var settingsService = new SettingsService(log, _tempRoot, allowRegistryStartupChanges: false);
        var backend = new FakeMicrophoneBackend
        {
            WindowsPermission = MicrophonePermissionState.DeniedByWindows
        };
        using var micService = new MicrophoneService(backend, settingsService, log);

        micService.Initialize();

        Assert.Equal(MicrophonePermissionState.DeniedByWindows, micService.CurrentState.PermissionState);
        Assert.False(micService.CurrentState.IsMonitoring);
        Assert.True(micService.CurrentState.HasError);
        Assert.Contains("Privacy Settings", micService.CurrentState.ErrorMessage);
    }

    [Fact]
    public void StartCaptureUnauthorized_HandlesAccessDeniedGracefully()
    {
        using var log = new LoggingService(_tempRoot);
        var settingsService = new SettingsService(log, _tempRoot, allowRegistryStartupChanges: false);
        var backend = new FakeMicrophoneBackend
        {
            ThrowUnauthorizedOnStart = true
        };
        using var micService = new MicrophoneService(backend, settingsService, log);

        micService.Initialize();

        Assert.Equal(MicrophonePermissionState.DeniedByWindows, micService.CurrentState.PermissionState);
        Assert.True(micService.CurrentState.HasError);
        Assert.False(micService.CurrentState.IsMonitoring);
    }

    [Fact]
    public void DeviceDisconnect_FallsBackCleanlyAndHandlesZeroDevices()
    {
        using var log = new LoggingService(_tempRoot);
        var settingsService = new SettingsService(log, _tempRoot, allowRegistryStartupChanges: false);
        var backend = new FakeMicrophoneBackend();
        using var micService = new MicrophoneService(backend, settingsService, log);

        micService.Initialize();
        micService.SelectDevice("mic-usb");
        Assert.Equal("mic-usb", micService.CurrentState.ActiveDeviceId);

        // Simulate unplugging the USB microphone while default mic remains active
        backend.Devices.RemoveAll(d => d.Id == "mic-usb");
        micService.RefreshDevices("Microphone disconnected — re-scanning devices...");

        Assert.Equal("mic-default", micService.CurrentState.ActiveDeviceId);
        Assert.True(micService.CurrentState.IsDeviceAvailable);

        // Simulate unplugging the last remaining microphone
        backend.Devices.Clear();
        backend.DefaultId = null;
        micService.RefreshDevices();

        Assert.False(micService.CurrentState.IsDeviceAvailable);
        Assert.False(micService.CurrentState.IsMonitoring);
        Assert.Equal(0, micService.CurrentState.InputLevelPercent);
        Assert.Contains("No", micService.CurrentState.ActiveDeviceName);
    }

    [Fact]
    public void Dispose_RestoresOriginalHardwareMuteStateIfModified()
    {
        using var log = new LoggingService(_tempRoot);
        var settingsService = new SettingsService(log, _tempRoot, allowRegistryStartupChanges: false);
        var backend = new FakeMicrophoneBackend();
        var micService = new MicrophoneService(backend, settingsService, log);

        micService.Initialize();
        Assert.False(backend.MuteMap["mic-default"]);

        micService.SetMuted(true);
        Assert.True(backend.MuteMap["mic-default"]);

        micService.Dispose();
        Assert.False(backend.MuteMap["mic-default"]);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors.
        }
    }
}
