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
        public Float16kSpanHandler? MonitorFloatSink { get; private set; }
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
        public void SetMonitorFloatSink(Float16kSpanHandler? sink) => MonitorFloatSink = sink;
        public void EmitLevel(double level) => AudioLevelMeasured?.Invoke(this, level);
        public void PushPcm(byte[] pcm) { if (!IsMutedSuppressed) PcmSink?.Invoke(pcm); }
        public void PushFloat(float[] samples) { if (!IsMutedSuppressed) MonitorFloatSink?.Invoke(samples); }
        public void Dispose()
        {
            _ = CaptureStoppedUnexpectedly;
            _ = DevicesTopologyChanged;
            _ = ExternalMuteChanged;
        }
    }

    private sealed class FakeSystemAudioBackend : ISystemAudioBackend
    {
        public List<SystemAudioDevice> AvailableDevices { get; } =
        [
            new SystemAudioDevice("spk-default", "Speakers (Realtek Audio)", IsDefaultMultimedia: true, IsDefaultCommunications: true)
        ];

        public IReadOnlyList<string> StartedIds { get; private set; } = [];
        public string? StartedId => StartedIds.Count > 0 ? StartedIds[0] : null;
        public bool IsMutedSuppressed { get; private set; }
        public bool ThrowUnauthorizedOnStart { get; set; }
        public Pcm16kSpanHandler? PcmSink { get; private set; }
        public Float16kSpanHandler? MonitorFloatSink { get; private set; }

        public event EventHandler<double>? AudioLevelMeasured;
        public event EventHandler<string>? ActiveEndpointDetected;
        public event EventHandler<Exception?>? CaptureStoppedUnexpectedly;
        public event EventHandler? DevicesTopologyChanged;

        public IReadOnlyList<SystemAudioDevice> EnumerateRenderDevices() => [.. AvailableDevices];

        public IReadOnlyList<string> ResolveTargetRenderDevices(
            string selectedOptionId,
            out string displaySummary,
            out string? fallbackNotice)
        {
            fallbackNotice = null;
            if (AvailableDevices.Count == 0)
            {
                displaySummary = "No playback device detected";
                return [];
            }

            if (string.Equals(selectedOptionId, SystemAudioService.CommunicationsDefaultOptionId, StringComparison.OrdinalIgnoreCase))
            {
                var comm = AvailableDevices.FirstOrDefault(d => d.IsDefaultCommunications) ?? AvailableDevices[0];
                displaySummary = comm.FriendlyName;
                return [comm.Id];
            }

            if (string.Equals(selectedOptionId, SystemAudioService.MultimediaDefaultOptionId, StringComparison.OrdinalIgnoreCase))
            {
                var mult = AvailableDevices.FirstOrDefault(d => d.IsDefaultMultimedia) ?? AvailableDevices[0];
                displaySummary = mult.FriendlyName;
                return [mult.Id];
            }

            if (!string.IsNullOrWhiteSpace(selectedOptionId))
            {
                var exact = AvailableDevices.FirstOrDefault(d => string.Equals(d.Id, selectedOptionId, StringComparison.OrdinalIgnoreCase));
                if (exact is not null)
                {
                    displaySummary = exact.FriendlyName;
                    return [exact.Id];
                }

                fallbackNotice = $"Selected playback device disconnected — using {AvailableDevices[0].FriendlyName}";
            }

            // Auto prioritizes Communications first, then Multimedia, then others cleanly
            var ordered = AvailableDevices
                .OrderByDescending(d => d.IsDefaultCommunications)
                .ThenByDescending(d => d.IsDefaultMultimedia)
                .ToList();
            displaySummary = ordered.Count == 1
                ? ordered[0].FriendlyName
                : $"Auto ({ordered.Count} outputs: {ordered[0].FriendlyName} + {ordered.Count - 1} more)";
            return ordered.Select(d => d.Id).ToList();
        }

        public void StartLoopbackCapture(IReadOnlyList<string> deviceIds, out IReadOnlyList<string> startedDeviceNames)
        {
            if (ThrowUnauthorizedOnStart)
            {
                throw new UnauthorizedAccessException("OS denied audio loopback.");
            }

            StartedIds = [.. deviceIds];
            startedDeviceNames = deviceIds
                .Select(id => AvailableDevices.FirstOrDefault(d => d.Id == id)?.FriendlyName ?? id)
                .ToList();
        }

        public void StopCapture() => StartedIds = [];
        public void SetMutedSuppression(bool muted) => IsMutedSuppressed = muted;
        public void SetPcmSink(Pcm16kSpanHandler? sink) => PcmSink = sink;
        public void SetMonitorFloatSink(Float16kSpanHandler? sink) => MonitorFloatSink = sink;
        public void EmitLevel(double level) => AudioLevelMeasured?.Invoke(this, level);
        public void EmitActiveEndpoint(string name) => ActiveEndpointDetected?.Invoke(this, name);
        public void EmitDisconnect(Exception ex) => CaptureStoppedUnexpectedly?.Invoke(this, ex);
        public void EmitTopologyChange() => DevicesTopologyChanged?.Invoke(this, EventArgs.Empty);
        public void PushPcm(byte[] pcm) { if (!IsMutedSuppressed) PcmSink?.Invoke(pcm); }
        public void PushFloat(float[] samples) { if (!IsMutedSuppressed) MonitorFloatSink?.Invoke(samples); }
        public void Dispose() { }
    }

    private sealed class FakeDualAudioMixerBackend : IDualAudioMixerBackend
    {
        public List<VirtualAudioOutputDevice> VirtualOutputs { get; } = [];
        public IWaveProvider? ActiveProvider { get; private set; }
        public string? RoutedDeviceId { get; private set; }

        public event EventHandler<Exception?>? RoutingStoppedUnexpectedly;
        public event EventHandler? DevicesTopologyChanged;

        public IReadOnlyList<VirtualAudioOutputDevice> EnumerateVirtualOutputs() => [.. VirtualOutputs];
        public WaveFormat ResolveOutputWaveFormat(string deviceId) => WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        public void StartRouting(string deviceId, IWaveProvider mixedWaveProvider, out string friendlyName, out string pairedCaptureHint)
        {
            var dev = VirtualOutputs.First(d => d.Id == deviceId);
            RoutedDeviceId = deviceId;
            ActiveProvider = mixedWaveProvider;
            friendlyName = dev.FriendlyName;
            pairedCaptureHint = dev.PairedCaptureHint;
        }

        public void StopRouting()
        {
            RoutedDeviceId = null;
            ActiveProvider = null;
        }

        public void EmitTopologyChange() => DevicesTopologyChanged?.Invoke(this, EventArgs.Empty);
        public void Dispose() => _ = RoutingStoppedUnexpectedly;
    }

    private sealed class FakeBrowserService : IGeminiBrowserService
    {
        private BrowserState _state = new();

        public BrowserState State => _state;

        public event EventHandler<BrowserState>? StateChanged;
        public event EventHandler<string>? MicrophonePermissionBlocked;
        public event EventHandler<bool>? DocumentPlayingAudioChanged;

        public void EmitDocumentPlayingAudio(bool isPlaying)
        {
            _state = new BrowserState
            {
                IsInitialized = _state.IsInitialized,
                CurrentUrl = _state.CurrentUrl,
                IsDocumentPlayingAudio = isPlaying,
                IsBrowserAudioMuted = _state.IsBrowserAudioMuted,
                StatusText = _state.StatusText
            };
            StateChanged?.Invoke(this, _state);
            DocumentPlayingAudioChanged?.Invoke(this, isPlaying);
        }

        public void SetBrowserAudioMuted(bool muted)
        {
            _state = new BrowserState
            {
                IsInitialized = _state.IsInitialized,
                CurrentUrl = _state.CurrentUrl,
                IsDocumentPlayingAudio = _state.IsDocumentPlayingAudio,
                IsBrowserAudioMuted = muted,
                StatusText = _state.StatusText
            };
            StateChanged?.Invoke(this, _state);
        }

        public bool CheckWebView2Availability() => true;
        public Task InitializeWorkspaceAsync(Microsoft.Web.WebView2.Wpf.IWebView2 control) => Task.CompletedTask;
        public void NavigateHome() { }
        public void Reload() { }
        public void GoBack() { }
        public void GoForward() { }
        public Task ClearCacheAsync() => Task.CompletedTask;
        public Task ResetWorkspaceAsync() => Task.CompletedTask;
        public Task ResetWorkspaceAsync(Microsoft.Web.WebView2.Wpf.IWebView2 control) => Task.CompletedTask;
        public void OpenWebView2InstallGuide() { }
        public void Dispose() => _ = MicrophonePermissionBlocked;
    }

    private sealed class FakeMeetingRecorderBackend : IMeetingRecorderBackend
    {
        public event EventHandler<string>? RecordingCompleted;
        public event EventHandler<string>? RecordingFailed;

        public string? LastOutputPath { get; private set; }
        public string? LastMixCaptureHint { get; private set; }
        public bool LastPreferVirtualCableInput { get; private set; }
        public bool ThrowOnStart { get; set; }

        public void StartRecording(string outputFilePath, string? preferredMixCaptureHint, bool preferVirtualCableInput)
        {
            if (ThrowOnStart)
            {
                throw new InvalidOperationException("Encoder unavailable.");
            }

            LastOutputPath = outputFilePath;
            LastMixCaptureHint = preferredMixCaptureHint;
            LastPreferVirtualCableInput = preferVirtualCableInput;
        }

        public void StopRecording()
        {
            if (!string.IsNullOrWhiteSpace(LastOutputPath))
            {
                RecordingCompleted?.Invoke(this, LastOutputPath);
            }
        }

        public void TriggerFailure(string reason) => RecordingFailed?.Invoke(this, reason);
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

        // Stop & restart independently resets waveform when stopped, and resumes on live level
        sysService.StopListening();
        Assert.False(sysService.CurrentState.IsListening);
        Assert.Null(sysBackend.StartedId);
        Assert.All(sysService.CurrentState.WaveformBars, h => Assert.Equal(3.0, h));

        sysService.StartListening();
        Assert.True(sysService.CurrentState.IsListening);
        Assert.Equal("spk-default", sysBackend.StartedId);

        sysBackend.EmitLevel(0.65);
        Assert.Equal(20, sysService.CurrentState.WaveformBars.Count);
        Assert.Contains(sysService.CurrentState.WaveformBars, h => h > 3.0);

        // Hot-plug switch from speakers to Bluetooth headphones (Dual output: Multimedia + Communications)
        sysBackend.AvailableDevices[0] = new SystemAudioDevice("spk-default", "Speakers (Realtek Audio)", IsDefaultMultimedia: true, IsDefaultCommunications: false);
        sysBackend.AvailableDevices.Add(new SystemAudioDevice("bt-hfp", "Headset Earphone (LOQ Hands-Free)", IsDefaultMultimedia: false, IsDefaultCommunications: true));
        sysService.RefreshDevice();
        Assert.Equal(2, sysBackend.StartedIds.Count);
        Assert.Contains("bt-hfp", sysBackend.StartedIds);
        Assert.Contains("spk-default", sysBackend.StartedIds);

        // Selecting Windows Call Default targets only Communications output & persists to AppSettings
        sysService.SelectDevice(SystemAudioService.CommunicationsDefaultOptionId);
        Assert.Equal(["bt-hfp"], sysBackend.StartedIds);
        Assert.Equal(SystemAudioService.CommunicationsDefaultOptionId, settings.CurrentSettings.PreferredSystemAudioDeviceId);

        // Explicit Bluetooth disconnect falls back gracefully to Speakers
        sysBackend.AvailableDevices.RemoveAll(d => d.Id == "bt-hfp");
        sysService.RefreshDevice();
        Assert.Equal("spk-default", sysService.CurrentState.ActiveDeviceId);

        // Zero playback devices available reports cleanly
        sysBackend.AvailableDevices.Clear();
        sysService.RefreshDevice();
        Assert.False(sysService.CurrentState.IsDeviceAvailable);
        Assert.False(sysService.CurrentState.IsListening);
    }

    [Fact]
    public void AdaptiveSpeechGainProcessor_LiftsQuietCallVoiceWithoutAmplifyingNoiseFloor()
    {
        var agc = new AdaptiveSpeechGainProcessor();
        var buffer = new float[1600]; // 100ms @ 16kHz

        // Quiet speech (~ -30 dBFS, peak 0.03) should be lifted gently over continuous speech frames
        for (var frame = 0; frame < 45; frame++)
        {
            for (var i = 0; i < buffer.Length; i++)
            {
                buffer[i] = 0.03f * MathF.Sin(2f * MathF.PI * 300f * i / 16000f);
            }

            agc.ProcessInPlace(buffer);
        }

        var boostedPeak = buffer.Max(MathF.Abs);
        Assert.InRange(boostedPeak, 0.06f, 0.45f);

        // Pure sub-threshold noise floor (< 0.001) should stay silent without runaway hiss gain
        var floor = new float[320];
        Array.Fill(floor, 0.0004f);
        agc.ProcessInPlace(floor);
        Assert.All(floor, s => Assert.True(MathF.Abs(s) < 0.005f));
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

    [Fact]
    public void VirtualAudioDriverCatalog_PreventsSpeakerAndCaptureFeedbackLoops()
    {
        // Only isolated virtual cable playback endpoints may receive mixed output (and remain excluded from SystemAudioService loopback)
        Assert.True(VirtualAudioDriverCatalog.IsVirtualAudioRenderSink("CABLE Input (VB-Audio Virtual Cable)"));
        Assert.True(VirtualAudioDriverCatalog.IsVirtualAudioRenderSink("CABLE In 16ch (VB-Audio Virtual Cable)"));
        Assert.True(VirtualAudioDriverCatalog.IsVirtualAudioRenderSink("VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)"));
        Assert.False(VirtualAudioDriverCatalog.IsVirtualAudioRenderSink("Speakers (Realtek(R) Audio)"));
        Assert.False(VirtualAudioDriverCatalog.IsVirtualAudioRenderSink("Headphones (Buds Pro3)"));

        // Paired virtual capture microphones are excluded from auto/default physical microphone capture
        Assert.True(VirtualAudioDriverCatalog.IsVirtualLoopbackCaptureDevice("CABLE Output (VB-Audio Virtual Cable)"));
        Assert.True(VirtualAudioDriverCatalog.IsVirtualLoopbackCaptureDevice("VoiceMeeter Output (VB-Audio VoiceMeeter VAIO)"));
        Assert.True(VirtualAudioDriverCatalog.IsVirtualLoopbackCaptureDevice("Stereo Mix (Realtek(R) Audio)"));
        Assert.False(VirtualAudioDriverCatalog.IsVirtualLoopbackCaptureDevice("Microphone (Realtek(R) Audio)"));

        Assert.Equal("CABLE Output (VB-Audio Virtual Cable)", VirtualAudioDriverCatalog.InferPairedCaptureMicHint("CABLE Input (VB-Audio Virtual Cable)"));
    }

    [Fact]
    public void SynchronizedMixedAudioWaveProvider_MixesBothStreamsWithIndependentVolumesEchoGuardAndNoClipping()
    {
        double latestLevel = 0;
        var provider = new SynchronizedMixedAudioWaveProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(48000, 2),
            lvl => latestLevel = lvl);

        // Push 20ms @ 16kHz (320 samples) from Mic (440 Hz @ 0.80) and System (880 Hz @ 0.80) -> sum raw 1.60 soft-limited safely!
        var micFrames = new float[320];
        var sysFrames = new float[320];
        for (var i = 0; i < 320; i++)
        {
            micFrames[i] = 0.80f * MathF.Sin(2f * MathF.PI * 440f * i / 16000f);
            sysFrames[i] = 0.80f * MathF.Sin(2f * MathF.PI * 880f * i / 16000f);
        }

        provider.PushMicrophoneFloat16k(micFrames);
        provider.PushSystemAudioFloat16k(sysFrames);

        // Read 20ms @ 48kHz 2ch Float32 = 960 frames * 8 bytes = 7680 bytes
        var outBytes = new byte[7680];
        var read = provider.Read(outBytes, 0, outBytes.Length);
        Assert.Equal(outBytes.Length, read);

        float maxOutSample = 0f;
        for (var i = 0; i + 3 < read; i += 4)
        {
            var val = MathF.Abs(BinaryPrimitives.ReadSingleLittleEndian(outBytes.AsSpan(i, 4)));
            if (val > maxOutSample) maxOutSample = val;
        }

        Assert.InRange(maxOutSample, 0.85f, 0.985f);
        Assert.True(latestLevel > 0.5);

        // Muting Mic mix passes ONLY System Audio cleanly (150% gain clamp check included via 0-150 range)
        provider.SetMicrophoneMix(100, muted: true);
        provider.SetSystemAudioMix(50, muted: false);
        provider.PushMicrophoneFloat16k(micFrames);
        provider.PushSystemAudioFloat16k(sysFrames);
        Array.Clear(outBytes, 0, outBytes.Length);
        read = provider.Read(outBytes, 0, outBytes.Length);
        Assert.Equal(outBytes.Length, read);
        float maxMutedMicSample = 0f;
        for (var i = 0; i + 3 < read; i += 4)
        {
            var val = MathF.Abs(BinaryPrimitives.ReadSingleLittleEndian(outBytes.AsSpan(i, 4)));
            if (val > maxMutedMicSample) maxMutedMicSample = val;
        }

        Assert.InRange(maxMutedMicSample, 0.35f, 0.45f);

        // Echo Guard suppresses ONLY System Audio while keeping Physical Microphone active
        provider.SetMicrophoneMix(100, muted: false);
        provider.SetSystemAudioMix(100, muted: false);
        provider.SetEchoGuardSuppressed(true);
        provider.PushMicrophoneFloat16k(Enumerable.Repeat(0.25f, 320).ToArray());
        provider.PushSystemAudioFloat16k(Enumerable.Repeat(0.25f, 320).ToArray());
        read = provider.Read(outBytes, 0, outBytes.Length);
        Assert.Equal(outBytes.Length, read);
        var micOnlySample = BinaryPrimitives.ReadSingleLittleEndian(outBytes.AsSpan(400, 4));
        Assert.InRange(micOnlySample, 0.20f, 0.30f);
    }

    [Fact]
    public void DualAudioMixerService_HandlesMissingDriverRoutesVirtualCableAndActivates400msEchoGuard()
    {
        using var log = new LoggingService(_tempRoot);
        var settings = new SettingsService(log, _tempRoot, allowRegistryStartupChanges: false);
        var micBackend = new FakeMicBackend();
        var sysBackend = new FakeSystemAudioBackend();
        var mixedBackend = new FakeDualAudioMixerBackend();
        var browser = new FakeBrowserService();
        var txBackend = new StubSpeechBackend();

        using var micService = new MicrophoneService(micBackend, settings, log);
        using var sysService = new SystemAudioService(sysBackend, settings, log);
        using var txService = new SpeechTranscriptionService(txBackend, micService, sysService, settings, log);
        using var mixedService = new DualAudioMixerService(mixedBackend, micService, sysService, browser, settings, log);

        micService.Initialize();
        sysService.Initialize();
        txService.Initialize();
        mixedService.Initialize();

        // Step 1: When no virtual audio cable is installed, honestly report dependency and refuse fake streaming
        Assert.False(mixedService.CurrentState.IsDriverInstalled);
        Assert.False(mixedService.CurrentState.IsStreaming);
        Assert.False(string.IsNullOrWhiteSpace(mixedService.CurrentState.DependencyMessage));

        mixedService.SetEnabled(true);
        Assert.False(mixedService.CurrentState.IsStreaming);
        Assert.Contains("VB-Audio Virtual Cable", mixedService.CurrentState.ErrorMessage);

        // Verify volume clamping is strictly 0..150%, then restore 100% for unity-gain mix assertion
        mixedService.SetMicrophoneMixVolume(200);
        Assert.Equal(150, mixedService.CurrentState.MicrophoneVolumePercent);
        mixedService.SetMicrophoneMixVolume(100);
        Assert.Equal(100, mixedService.CurrentState.MicrophoneVolumePercent);

        // Step 2: Simulate signed VB-Audio Virtual Cable installed -> starts routing cleanly without breaking transcription
        using var routingStartedGate = new ManualResetEventSlim(false);
        using var echoGuardReleasedGate = new ManualResetEventSlim(false);
        mixedService.StateChanged += (_, s) =>
        {
            if (s.IsStreaming)
            {
                routingStartedGate.Set();
            }

            if (s.IsStreaming && !s.IsEchoGuardActive && routingStartedGate.IsSet)
            {
                echoGuardReleasedGate.Set();
            }
        };
        mixedBackend.VirtualOutputs.Add(new VirtualAudioOutputDevice(
            "vbcable-in",
            "CABLE Input (VB-Audio Virtual Cable)",
            "CABLE Output (VB-Audio Virtual Cable)"));
        mixedBackend.EmitTopologyChange();
        Assert.True(routingStartedGate.Wait(TimeSpan.FromSeconds(3)), "Expected debounced topology update to start virtual routing.");

        Assert.True(mixedService.CurrentState.IsDriverInstalled);
        Assert.True(mixedService.CurrentState.IsStreaming);
        Assert.Equal("vbcable-in", mixedBackend.RoutedDeviceId);
        Assert.NotNull(mixedBackend.ActiveProvider);

        // Step 3: Push simultaneous Mic + System float audio and verify mixed wave provider delivers combined audio (~0.50f)
        var micFloat = Enumerable.Repeat(0.25f, 320).ToArray();
        var sysFloat = Enumerable.Repeat(0.25f, 320).ToArray();
        micBackend.PushFloat(micFloat);
        sysBackend.PushFloat(sysFloat);

        var buf = new byte[7680];
        var read = mixedBackend.ActiveProvider!.Read(buf);
        Assert.Equal(7680, read);
        var firstMixedSample = BinaryPrimitives.ReadSingleLittleEndian(buf.AsSpan(400, 4));
        Assert.InRange(firstMixedSample, 0.45f, 0.55f);

        // Step 4: Trigger Echo Guard when Gemini speaks -> System Audio suppressed, Mic continues; unmutes 400ms after silence
        echoGuardReleasedGate.Reset();
        browser.EmitDocumentPlayingAudio(true);
        Assert.True(mixedService.CurrentState.IsEchoGuardActive);
        micBackend.PushFloat(micFloat);
        sysBackend.PushFloat(sysFloat);
        Array.Clear(buf, 0, buf.Length);
        read = mixedBackend.ActiveProvider.Read(buf);
        Assert.Equal(7680, read);
        var guardedSample = BinaryPrimitives.ReadSingleLittleEndian(buf.AsSpan(400, 4));
        Assert.InRange(guardedSample, 0.20f, 0.30f);

        echoGuardReleasedGate.Reset();
        browser.EmitDocumentPlayingAudio(false);
        Assert.True(mixedService.CurrentState.IsEchoGuardActive, "Echo Guard should hold briefly during the 400ms tail delay.");
        Assert.True(echoGuardReleasedGate.Wait(TimeSpan.FromSeconds(3)), "Echo Guard did not release 400ms after Gemini stopped speaking.");
        Assert.False(mixedService.CurrentState.IsEchoGuardActive);

        // Right-side transcription continues unaffected concurrently
        Assert.Contains(AudioSourceKind.Microphone, txBackend.RunningSources);
        Assert.Contains(AudioSourceKind.SystemAudio, txBackend.RunningSources);
    }

    [Fact]
    public void MeetingRecorderService_SavesTimestampedMp4InDocumentsMeetingRecordings()
    {
        using var log = new LoggingService(_tempRoot);
        var settings = new SettingsService(log, _tempRoot, allowRegistryStartupChanges: false);
        var micBackend = new FakeMicBackend();
        var sysBackend = new FakeSystemAudioBackend();
        var mixedBackend = new FakeDualAudioMixerBackend();
        mixedBackend.VirtualOutputs.Add(new VirtualAudioOutputDevice(
            "vbcable-in",
            "CABLE Input (VB-Audio Virtual Cable)",
            "CABLE Output (VB-Audio Virtual Cable)"));
        var browser = new FakeBrowserService();
        var recorderBackend = new FakeMeetingRecorderBackend();

        using var micService = new MicrophoneService(micBackend, settings, log);
        using var sysService = new SystemAudioService(sysBackend, settings, log);
        using var mixedService = new DualAudioMixerService(mixedBackend, micService, sysService, browser, settings, log);
        micService.Initialize();
        sysService.Initialize();
        mixedService.Initialize();
        mixedService.SetEnabled(true);

        var recordingsDir = Path.Combine(_tempRoot, "MeetingRecordings");
        using var recorderService = new MeetingRecorderService(recorderBackend, mixedService, log, recordingsDir);

        recorderService.StartRecording();
        Assert.True(recorderService.CurrentState.IsRecording);
        Assert.True(recorderBackend.LastPreferVirtualCableInput);
        Assert.StartsWith(recordingsDir, recorderBackend.LastOutputPath);
        Assert.Matches(@"Meeting_\d{8}_\d{6}\.mp4$", Path.GetFileName(recorderBackend.LastOutputPath!));

        recorderService.StopRecording();
        Assert.False(recorderService.CurrentState.IsRecording);
        Assert.Equal(recorderBackend.LastOutputPath, recorderService.CurrentState.LastCompletedFilePath);

        // Failure path leaves clean non-crashing error state
        recorderBackend.ThrowOnStart = true;
        recorderService.StartRecording();
        Assert.False(recorderService.CurrentState.IsRecording);
        Assert.Contains("Encoder unavailable", recorderService.CurrentState.ErrorMessage);
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
