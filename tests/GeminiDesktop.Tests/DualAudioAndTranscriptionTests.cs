using System.Buffers.Binary;
using System.IO;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using GeminiDesktop.Helpers;
using GeminiDesktop.Models;
using GeminiDesktop.Services;
using NAudio.Wave;
using Xunit;

namespace GeminiDesktop.Tests;

public sealed class DualAudioAndTranscriptionTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "MyLaptopDualAudioTests_" + Guid.NewGuid().ToString("N"));

    private sealed class FakeMicBackend : IMicrophoneBackend
    {
        public Pcm16kSpanHandler? PcmSink { get; private set; }
        public bool IsMutedSuppressed { get; private set; }
        public string? StartedId { get; private set; }

        public event EventHandler<double>? AudioLevelMeasured;
        public event EventHandler<Exception?>? CaptureStoppedUnexpectedly;
        public event EventHandler? DevicesTopologyChanged;
        public event EventHandler<bool>? ExternalMuteChanged;

        public MicrophonePermissionState CheckWindowsPermission() => MicrophonePermissionState.Allowed;
        public IReadOnlyList<MicrophoneDevice> EnumerateCaptureDevices() =>
            [new MicrophoneDevice("mic-1", "Laptop Built-in Mic", IsDefault: true)];
        public string? ResolveDefaultCaptureDeviceId() => "mic-1";
        public bool TryGetEndpointMute(string deviceId, out bool isMuted) { isMuted = false; return true; }
        public bool TrySetEndpointMute(string deviceId, bool muted) => true;
        public void StartCapture(string deviceId) => StartedId = deviceId;
        public void StopCapture() => StartedId = null;
        public void SetMutedSuppression(bool muted) => IsMutedSuppressed = muted;
        public void SetPcmSink(Pcm16kSpanHandler? sink) => PcmSink = sink;
        public void EmitLevel(double level) => AudioLevelMeasured?.Invoke(this, level);
        public void PushPcm(byte[] pcm) { if (!IsMutedSuppressed) PcmSink?.Invoke(pcm); }
        public void Dispose()
        {
            _ = CaptureStoppedUnexpectedly;
            _ = DevicesTopologyChanged;
            _ = ExternalMuteChanged;
        }
    }

    private sealed class FakeSystemAudioBackend : ISystemAudioBackend
    {
        public string? DefaultId { get; set; } = "spk-default";
        public string FriendlyName { get; set; } = "Speakers (Realtek Audio)";
        public string? StartedId { get; private set; }
        public bool IsMutedSuppressed { get; private set; }
        public bool ThrowUnauthorizedOnStart { get; set; }
        public Pcm16kSpanHandler? PcmSink { get; private set; }

        public event EventHandler<double>? AudioLevelMeasured;
        public event EventHandler<Exception?>? CaptureStoppedUnexpectedly;
        public event EventHandler? DevicesTopologyChanged;

        public string? ResolveDefaultRenderDevice(out string friendlyName)
        {
            friendlyName = FriendlyName;
            return DefaultId;
        }

        public void StartLoopbackCapture(string deviceId)
        {
            if (ThrowUnauthorizedOnStart)
            {
                throw new UnauthorizedAccessException("OS denied audio loopback.");
            }

            StartedId = deviceId;
        }

        public void StopCapture() => StartedId = null;
        public void SetMutedSuppression(bool muted) => IsMutedSuppressed = muted;
        public void SetPcmSink(Pcm16kSpanHandler? sink) => PcmSink = sink;
        public void EmitLevel(double level) => AudioLevelMeasured?.Invoke(this, level);
        public void EmitDisconnect(Exception ex) => CaptureStoppedUnexpectedly?.Invoke(this, ex);
        public void EmitTopologyChange() => DevicesTopologyChanged?.Invoke(this, EventArgs.Empty);
        public void PushPcm(byte[] pcm) { if (!IsMutedSuppressed) PcmSink?.Invoke(pcm); }
        public void Dispose() { }
    }

    private sealed class StubSpeechBackend : ISpeechTranscriptionBackend
    {
        public HashSet<AudioSourceKind> RunningSources { get; } = [];
        public Dictionary<AudioSourceKind, long> ReceivedBytes { get; } = new()
        {
            [AudioSourceKind.Microphone] = 0,
            [AudioSourceKind.SystemAudio] = 0
        };

        public bool ForceUnavailable { get; set; }

        public event EventHandler<SpeechTranscriptEventArgs>? HypothesisReceived;
        public event EventHandler<SpeechTranscriptEventArgs>? FinalTranscriptReceived;
        public event EventHandler<string>? EngineError;

        public bool TryInitialize(out string recognizerDisplayName, out string? errorMessage)
        {
            if (ForceUnavailable)
            {
                recognizerDisplayName = "Unavailable";
                errorMessage = "Speech pack not installed.";
                return false;
            }

            recognizerDisplayName = "Microsoft Speech Recognizer 8.0 (en-US)";
            errorMessage = null;
            return true;
        }

        public bool StartSource(AudioSourceKind source, string deviceName)
        {
            RunningSources.Add(source);
            return true;
        }

        public void StopSource(AudioSourceKind source) => RunningSources.Remove(source);

        public void PushPcm16k(AudioSourceKind source, ReadOnlySpan<byte> pcm16kMonoBytes)
        {
            if (RunningSources.Contains(source))
            {
                ReceivedBytes[source] += pcm16kMonoBytes.Length;
            }
        }

        public void EmitHypothesis(AudioSourceKind src, string device, string text) =>
            HypothesisReceived?.Invoke(this, new SpeechTranscriptEventArgs(src, device, text, 0.72));

        public void EmitFinal(AudioSourceKind src, string device, string text) =>
            FinalTranscriptReceived?.Invoke(this, new SpeechTranscriptEventArgs(src, device, text, 0.89));

        public void Dispose() => _ = EngineError;
    }

    [Fact]
    public void AudioProcessingHelper_HandlesExtensibleFloatAndResamples48kStereoTo16kMono()
    {
        // Windows shared WASAPI devices frequently report WAVE_FORMAT_EXTENSIBLE with IEEE Float 48kHz 2ch.
        var extensibleFmt = new WaveFormatExtensible(48000, 32, 2);
        Assert.True(AudioProcessingHelper.IsIeeeFloatFormat(extensibleFmt));

        const int inputFrames = 4800; // 100 ms @ 48 kHz
        var inputBytes = new byte[inputFrames * 8]; // 2 channels * 4 bytes/sample
        for (var i = 0; i < inputFrames; i++)
        {
            var sample = 0.5f * MathF.Sin(2f * MathF.PI * 440f * i / 48000f);
            BinaryPrimitives.WriteSingleLittleEndian(inputBytes.AsSpan(i * 8, 4), sample);
            BinaryPrimitives.WriteSingleLittleEndian(inputBytes.AsSpan((i * 8) + 4, 4), sample);
        }

        var peak = AudioProcessingHelper.ComputePeakAmplitude(inputBytes, extensibleFmt);
        Assert.InRange(peak, 0.48, 0.52);

        double phase = 0;
        float prev = 0;
        var scratch = new byte[64];
        var outBytes = AudioProcessingHelper.ConvertToPcm16kMono(inputBytes, extensibleFmt, ref phase, ref prev, ref scratch);

        // 100 ms @ 16 kHz mono 16-bit = 1600 samples * 2 bytes = 3200 bytes (±1 sample interpolation edge)
        Assert.InRange(outBytes, 3196, 3204);

        var outPeak = AudioProcessingHelper.ComputePeakAmplitude(scratch, outBytes, new WaveFormat(16000, 16, 1));
        Assert.InRange(outPeak, 0.45, 0.55);
    }

    [Fact]
    public void ContinuousPcmAudioStream_SuppliesPacedSilenceAndCompletesCleanly()
    {
        using var stream = new ContinuousPcmAudioStream(capacityBytes: 8192);
        var buf = new byte[1280];

        // When no audio packets have arrived yet, Read fulfills exact requested count with digital silence
        var silentRead = stream.Read(buf, 0, buf.Length);
        Assert.Equal(buf.Length, silentRead);
        Assert.All(buf, b => Assert.Equal((byte)0, b));

        // Write 4 bytes and verify full requested count (4 real bytes + silence padding) preserves exact payload
        byte[] payload = [0x11, 0x22, 0x33, 0x44];
        stream.WritePcm(payload);
        var smallRead = new byte[4];
        var realRead = stream.Read(smallRead, 0, smallRead.Length);
        Assert.Equal(4, realRead);
        Assert.Equal(payload, smallRead);

        // Completing wakes readers immediately with 0 bytes
        stream.Complete();
        Assert.Equal(0, stream.Read(buf, 0, buf.Length));
    }

    [Fact]
    public void SystemAudioService_IndependentStartStopMuteAndHotPlugRebind()
    {
        using var log = new LoggingService(_tempRoot);
        var settings = new SettingsService(log, _tempRoot, allowRegistryStartupChanges: false);
        var sysBackend = new FakeSystemAudioBackend();
        using var sysService = new SystemAudioService(sysBackend, settings, log);

        sysService.Initialize();
        Assert.True(sysService.CurrentState.IsListening);
        Assert.Equal("spk-default", sysBackend.StartedId);

        sysBackend.EmitLevel(0.75);
        Assert.Equal(75, sysService.CurrentState.OutputLevelPercent);

        // Mute suppresses app capture & level WITHOUT muting speakers
        sysService.SetMuted(true);
        Assert.True(sysService.CurrentState.IsMuted);
        Assert.True(sysBackend.IsMutedSuppressed);
        Assert.Equal(0, sysService.CurrentState.OutputLevelPercent);
        sysBackend.EmitLevel(0.90);
        Assert.Equal(0, sysService.CurrentState.OutputLevelPercent);

        // Unmute resumes level activity
        sysService.SetMuted(false);
        Assert.False(sysBackend.IsMutedSuppressed);
        sysBackend.EmitLevel(0.45);
        Assert.Equal(45, sysService.CurrentState.OutputLevelPercent);

        // Stop & restart independently
        sysService.StopListening();
        Assert.False(sysService.CurrentState.IsListening);
        Assert.Null(sysBackend.StartedId);

        sysService.StartListening();
        Assert.True(sysService.CurrentState.IsListening);
        Assert.Equal("spk-default", sysBackend.StartedId);

        // Hot-plug switch from speakers to Bluetooth headphones
        sysBackend.DefaultId = "bt-headphones";
        sysBackend.FriendlyName = "WH-1000XM5 Headphones";
        sysService.RefreshDevice();
        Assert.Equal("bt-headphones", sysService.CurrentState.ActiveDeviceId);
        Assert.Equal("WH-1000XM5 Headphones", sysService.CurrentState.ActiveDeviceName);

        // Zero playback devices available reports cleanly
        sysBackend.DefaultId = null;
        sysService.RefreshDevice();
        Assert.False(sysService.CurrentState.IsDeviceAvailable);
        Assert.False(sysService.CurrentState.IsListening);
    }

    [Fact]
    public void SpeechTranscriptionService_SeparatesAndRunsMicAndSystemSimultaneously()
    {
        using var log = new LoggingService(_tempRoot);
        var settings = new SettingsService(log, _tempRoot, allowRegistryStartupChanges: false);
        var micBackend = new FakeMicBackend();
        var sysBackend = new FakeSystemAudioBackend();
        var txBackend = new StubSpeechBackend();

        using var micService = new MicrophoneService(micBackend, settings, log);
        using var sysService = new SystemAudioService(sysBackend, settings, log);
        using var txService = new SpeechTranscriptionService(txBackend, micService, sysService, settings, log);

        micService.Initialize();
        sysService.Initialize();
        txService.Initialize();

        Assert.Contains(AudioSourceKind.Microphone, txBackend.RunningSources);
        Assert.Contains(AudioSourceKind.SystemAudio, txBackend.RunningSources);

        // Both streams deliver PCM independently
        micBackend.PushPcm(new byte[320]);
        sysBackend.PushPcm(new byte[640]);
        Assert.Equal(320, txBackend.ReceivedBytes[AudioSourceKind.Microphone]);
        Assert.Equal(640, txBackend.ReceivedBytes[AudioSourceKind.SystemAudio]);

        // Muting System Audio suppresses System Audio forwarding while Microphone keeps flowing
        sysService.SetMuted(true);
        sysBackend.PushPcm(new byte[640]);
        micBackend.PushPcm(new byte[320]);
        Assert.Equal(640, txBackend.ReceivedBytes[AudioSourceKind.Microphone]);
        Assert.Equal(640, txBackend.ReceivedBytes[AudioSourceKind.SystemAudio]);

        // Live hypothesis & finalized attributed transcript entries
        txBackend.EmitHypothesis(AudioSourceKind.Microphone, "Laptop Built-in Mic", "testing one");
        Assert.Equal("testing one", txService.CurrentState.LiveMicrophoneText);

        txBackend.EmitFinal(AudioSourceKind.Microphone, "Laptop Built-in Mic", "Testing one two");
        txBackend.EmitFinal(AudioSourceKind.SystemAudio, "Speakers (Realtek Audio)", "Welcome to the video tutorial");

        Assert.Equal(string.Empty, txService.CurrentState.LiveMicrophoneText);
        Assert.Equal(2, txService.CurrentState.Entries.Count);
        Assert.Equal("Microphone", txService.CurrentState.Entries[0].SourceLabel);
        Assert.Equal("System Audio", txService.CurrentState.Entries[1].SourceLabel);

        // Stopping Microphone leaves System Audio active cleanly
        micService.StopListening();
        Assert.DoesNotContain(AudioSourceKind.Microphone, txBackend.RunningSources);
        Assert.Contains(AudioSourceKind.SystemAudio, txBackend.RunningSources);

        txService.ClearTranscript();
        Assert.Empty(txService.CurrentState.Entries);
    }

    [Fact]
    public void SapiSpeechTranscriptionBackend_LiveIntegrationRecognizesDualSpeechStreams()
    {
        using var backend = new SapiSpeechTranscriptionBackend();
        if (!backend.TryInitialize(out _, out _))
        {
            // Skip cleanly only if running on an OS image without any SAPI desktop speech pack.
            return;
        }

        var fmt = new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono);
        static byte[] SynthesizeSentence(string sentence, SpeechAudioFormatInfo f)
        {
            using var synth = new SpeechSynthesizer();
            synth.Rate = -1;
            synth.Volume = 100;
            using var ms = new MemoryStream();
            synth.SetOutputToAudioStream(ms, f);
            synth.Speak(sentence);
            synth.SetOutputToNull();
            var raw = ms.ToArray();
            var padded = new byte[12800 + raw.Length + 38400];
            Buffer.BlockCopy(raw, 0, padded, 12800, raw.Length);
            return padded;
        }

        var micPcm = SynthesizeSentence("Please open settings.", fmt);
        var sysPcm = SynthesizeSentence("Please open home.", fmt);

        var errors = new List<string>();
        backend.EngineError += (_, msg) => errors.Add(msg);

        var finals = new List<SpeechTranscriptEventArgs>();
        var gate = new ManualResetEventSlim(false);
        backend.FinalTranscriptReceived += (_, e) =>
        {
            lock (finals)
            {
                finals.Add(e);
                if (finals.Any(x => x.Source == AudioSourceKind.Microphone) &&
                    finals.Any(x => x.Source == AudioSourceKind.SystemAudio))
                {
                    gate.Set();
                }
            }
        };

        Assert.True(backend.StartSource(AudioSourceKind.Microphone, "Test Mic"), $"Errors: {string.Join(" | ", errors)}");
        Assert.True(backend.StartSource(AudioSourceKind.SystemAudio, "Test Speakers"), $"Errors: {string.Join(" | ", errors)}");

        // Stream both sentences concurrently in 20ms PCM frames
        var maxLen = Math.Max(micPcm.Length, sysPcm.Length);
        const int chunk = 640;
        for (var offset = 0; offset < maxLen; offset += chunk)
        {
            if (offset < micPcm.Length)
            {
                backend.PushPcm16k(AudioSourceKind.Microphone, micPcm.AsSpan(offset, Math.Min(chunk, micPcm.Length - offset)));
            }

            if (offset < sysPcm.Length)
            {
                backend.PushPcm16k(AudioSourceKind.SystemAudio, sysPcm.AsSpan(offset, Math.Min(chunk, sysPcm.Length - offset)));
            }
        }

        var recognizedBoth = gate.Wait(TimeSpan.FromSeconds(10));
        backend.StopSource(AudioSourceKind.Microphone);
        backend.StopSource(AudioSourceKind.SystemAudio);

        Assert.True(recognizedBoth, $"Expected finalized text from both streams, got count={finals.Count}: {string.Join(" | ", finals.Select(f => $"{f.Source}:{f.Text}"))}");
        Assert.Contains(finals, f => f.Source == AudioSourceKind.Microphone && !string.IsNullOrWhiteSpace(f.Text));
        Assert.Contains(finals, f => f.Source == AudioSourceKind.SystemAudio && !string.IsNullOrWhiteSpace(f.Text));
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
