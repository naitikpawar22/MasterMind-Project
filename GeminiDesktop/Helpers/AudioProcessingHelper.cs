using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GeminiDesktop.Helpers;

/// <summary>
/// Callback receiving 16 kHz, 16-bit signed mono PCM bytes converted cleanly from live WASAPI audio.
/// </summary>
public delegate void Pcm16kSpanHandler(ReadOnlySpan<byte> pcm16kMonoBytes);

/// <summary>
/// Shared audio DSP, WASAPI capture session lifecycle, and real-time PCM resampling utilities
/// used identically by physical microphone capture and Windows system-audio loopback capture.
/// </summary>
public static class AudioProcessingHelper
{
    public const int TargetSpeechSampleRate = 16000;

    /// <summary>
    /// Returns true if the WaveFormat (including WAVE_FORMAT_EXTENSIBLE) carries 32-bit IEEE float samples.
    /// </summary>
    public static bool IsIeeeFloatFormat(WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        if (format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            return true;
        }

        return format is WaveFormatExtensible ext &&
               ext.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT;
    }

    /// <summary>
    /// Computes normalized [0.0, 1.0] peak amplitude from raw WASAPI PCM or IEEE float audio frames.
    /// Properly handles WAVE_FORMAT_EXTENSIBLE float and PCM subformats used by Windows audio engines.
    /// </summary>
    public static double ComputePeakAmplitude(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        if (buffer is null || bytesRecorded <= 0 || format is null)
        {
            return 0.0;
        }

        return ComputePeakAmplitude(buffer.AsSpan(0, Math.Min(bytesRecorded, buffer.Length)), format);
    }

    /// <summary>
    /// Zero-copy overload computing normalized [0.0, 1.0] peak amplitude directly over a WASAPI audio span.
    /// </summary>
    public static double ComputePeakAmplitude(ReadOnlySpan<byte> buffer, WaveFormat format)
    {
        if (buffer.IsEmpty || format is null)
        {
            return 0.0;
        }

        float max = 0f;

        if (IsIeeeFloatFormat(format) && format.BitsPerSample == 32)
        {
            for (var i = 0; i + 3 < buffer.Length; i += 4)
            {
                var sample = Math.Abs(BinaryPrimitives.ReadSingleLittleEndian(buffer.Slice(i, 4)));
                if (!float.IsNaN(sample) && !float.IsInfinity(sample) && sample > max)
                {
                    max = sample;
                }
            }

            return Math.Clamp(max, 0.0, 1.0);
        }

        if (format.BitsPerSample == 16)
        {
            for (var i = 0; i + 1 < buffer.Length; i += 2)
            {
                var raw = BinaryPrimitives.ReadInt16LittleEndian(buffer.Slice(i, 2));
                var sample = Math.Abs(raw == short.MinValue ? 1.0f : raw / 32768f);
                if (sample > max)
                {
                    max = sample;
                }
            }

            return Math.Clamp(max, 0.0, 1.0);
        }

        if (format.BitsPerSample == 24)
        {
            for (var i = 0; i + 2 < buffer.Length; i += 3)
            {
                var b0 = buffer[i];
                var b1 = buffer[i + 1];
                var b2 = (sbyte)buffer[i + 2];
                var raw = b0 | (b1 << 8) | (b2 << 16);
                var sample = Math.Abs(raw / 8388608f);
                if (sample > max)
                {
                    max = sample;
                }
            }

            return Math.Clamp(max, 0.0, 1.0);
        }

        if (format.BitsPerSample == 32)
        {
            for (var i = 0; i + 3 < buffer.Length; i += 4)
            {
                var raw = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(i, 4));
                var sample = Math.Abs(raw == int.MinValue ? 1.0f : raw / 2147483648f);
                if (sample > max)
                {
                    max = sample;
                }
            }

            return Math.Clamp(max, 0.0, 1.0);
        }

        return 0.0;
    }

    /// <summary>
    /// Downmixes multi-channel audio (Float32 / PCM16 / PCM24 / PCM32) to mono and resamples
    /// to 16 kHz normalized float samples [-1.0, 1.0] using boxcar anti-aliasing when downsampling.
    /// </summary>
    public static int ConvertToFloat16kMono(
        ReadOnlySpan<byte> input,
        WaveFormat format,
        ref double phasePosition,
        ref float previousSample,
        ref float[] floatOutputBuffer)
    {
        if (input.IsEmpty || format is null || format.SampleRate <= 0 || format.Channels <= 0)
        {
            return 0;
        }

        var channels = format.Channels;
        var bytesPerSample = format.BitsPerSample / 8;
        var blockAlign = Math.Max(format.BlockAlign, channels * bytesPerSample);
        if (blockAlign <= 0)
        {
            return 0;
        }

        var frameCount = input.Length / blockAlign;
        if (frameCount <= 0)
        {
            return 0;
        }

        var isFloat32 = IsIeeeFloatFormat(format) && format.BitsPerSample == 32;
        float[] rentedMono = ArrayPool<float>.Shared.Rent(frameCount);
        try
        {
            for (var f = 0; f < frameCount; f++)
            {
                var frameOffset = f * blockAlign;
                float sum = 0f;

                for (var ch = 0; ch < channels; ch++)
                {
                    var sampleOffset = frameOffset + (ch * bytesPerSample);
                    if (sampleOffset + bytesPerSample > input.Length)
                    {
                        break;
                    }

                    sum += DecodeMonoChannelSample(input.Slice(sampleOffset, bytesPerSample), format.BitsPerSample, isFloat32);
                }

                rentedMono[f] = Math.Clamp(sum / channels, -1.0f, 1.0f);
            }

            var step = (double)format.SampleRate / TargetSpeechSampleRate;
            var maxOutSamples = (int)Math.Ceiling(frameCount / step) + 4;
            if (floatOutputBuffer.Length < maxOutSamples)
            {
                floatOutputBuffer = new float[maxOutSamples];
            }

            var outIdx = 0;
            if (step >= 2.2)
            {
                // Light 3-tap [0.25, 0.50, 0.25] zero-phase anti-aliasing filter preserving 0-7.5 kHz speech consonants.
                while (phasePosition < frameCount)
                {
                    var idx = (int)Math.Floor(phasePosition);
                    var frac = (float)(phasePosition - idx);

                    var s0 = idx <= 0 ? previousSample : rentedMono[idx - 1];
                    var s1 = rentedMono[Math.Clamp(idx, 0, frameCount - 1)];
                    var s2 = rentedMono[Math.Clamp(idx + 1, 0, frameCount - 1)];
                    var filtered = (0.25f * s0) + (0.50f * s1) + (0.25f * s2);
                    var interp = s0 + ((s1 - s0) * frac);
                    floatOutputBuffer[outIdx++] = Math.Clamp((0.6f * interp) + (0.4f * filtered), -1.0f, 1.0f);
                    phasePosition += step;
                }
            }
            else
            {
                while (phasePosition < frameCount)
                {
                    var idx = (int)Math.Floor(phasePosition);
                    var frac = (float)(phasePosition - idx);

                    var s0 = idx <= 0 ? previousSample : rentedMono[idx - 1];
                    var s1 = rentedMono[Math.Clamp(idx, 0, frameCount - 1)];
                    floatOutputBuffer[outIdx++] = Math.Clamp(s0 + ((s1 - s0) * frac), -1.0f, 1.0f);
                    phasePosition += step;
                }
            }

            phasePosition -= frameCount;
            previousSample = rentedMono[frameCount - 1];
            return outIdx;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rentedMono);
        }
    }

    /// <summary>
    /// Downmixes multi-channel audio (Float32 / PCM16 / PCM24 / PCM32) to mono and resamples
    /// cleanly via anti-aliased decimation / linear interpolation into 16 kHz, 16-bit signed PCM bytes.
    /// </summary>
    public static int ConvertToPcm16kMono(
        ReadOnlySpan<byte> input,
        WaveFormat format,
        ref double phasePosition,
        ref float previousSample,
        ref byte[] scratchBuffer)
    {
        var floatBuf = ArrayPool<float>.Shared.Rent(Math.Max(64, (input.Length / Math.Max(1, format?.BlockAlign ?? 2)) + 8));
        try
        {
            var sampleCount = ConvertToFloat16kMono(input, format!, ref phasePosition, ref previousSample, ref floatBuf);
            if (sampleCount <= 0)
            {
                return 0;
            }

            return EncodeFloatToPcm16(floatBuf.AsSpan(0, sampleCount), ref scratchBuffer);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(floatBuf);
        }
    }

    public static int EncodeFloatToPcm16(ReadOnlySpan<float> samples, ref byte[] scratchBuffer)
    {
        if (samples.IsEmpty)
        {
            return 0;
        }

        var requiredBytes = samples.Length * 2;
        if (scratchBuffer.Length < requiredBytes)
        {
            scratchBuffer = new byte[requiredBytes];
        }

        for (var i = 0; i < samples.Length; i++)
        {
            var scaled = (int)Math.Round(Math.Clamp(samples[i], -1.0f, 1.0f) * 32767.0f);
            var pcmSample = (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(scratchBuffer.AsSpan(i * 2, 2), pcmSample);
        }

        return requiredBytes;
    }

    private static float DecodeMonoChannelSample(ReadOnlySpan<byte> slice, int bitsPerSample, bool isFloat32)
    {
        if (isFloat32 && bitsPerSample == 32)
        {
            var val = BinaryPrimitives.ReadSingleLittleEndian(slice);
            return float.IsNaN(val) || float.IsInfinity(val) ? 0f : val;
        }

        return bitsPerSample switch
        {
            16 => BinaryPrimitives.ReadInt16LittleEndian(slice) / 32768f,
            24 => ((slice[0] | (slice[1] << 8) | ((sbyte)slice[2] << 16)) / 8388608f),
            32 => BinaryPrimitives.ReadInt32LittleEndian(slice) / 2147483648f,
            _ => 0f
        };
    }
}

/// <summary>
/// Thread-safe, bounded continuous 16 kHz PCM stream designed for streaming speech recognition.
/// Provides real-time paced silence when live capture is idle (e.g., silent loopback output or muted)
/// so System.Speech.Recognition never freezes waiting for audio packets and completes cleanly on stop.
/// </summary>
public sealed class ContinuousPcmAudioStream : Stream
{
    private const int BytesPerMillisecond16kMono = 32; // 16000 * 2 bytes/sample / 1000 ms
    private readonly byte[] _ringBuffer;
    private readonly object _lock = new();
    private readonly AutoResetEvent _dataSignal = new(false);

    private int _readPos;
    private int _writePos;
    private int _count;
    private long _totalBytesRead;
    private volatile bool _completed;

    public ContinuousPcmAudioStream(int capacityBytes = 320000)
    {
        _ringBuffer = new byte[Math.Max(6400, capacityBytes)];
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _completed ? Volatile.Read(ref _totalBytesRead) : 1_000_000_000L;
    public override long Position
    {
        get => Volatile.Read(ref _totalBytesRead);
        set { }
    }

    public int BufferedBytes
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    public void WritePcm(ReadOnlySpan<byte> pcmBytes)
    {
        if (_completed || pcmBytes.IsEmpty)
        {
            return;
        }

        lock (_lock)
        {
            if (_completed)
            {
                return;
            }

            if (pcmBytes.Length >= _ringBuffer.Length)
            {
                pcmBytes = pcmBytes[^_ringBuffer.Length..];
                _readPos = 0;
                _writePos = 0;
                _count = 0;
            }

            var free = _ringBuffer.Length - _count;
            if (pcmBytes.Length > free)
            {
                var overflow = pcmBytes.Length - free;
                _readPos = (_readPos + overflow) % _ringBuffer.Length;
                _count -= overflow;
            }

            var firstCopy = Math.Min(pcmBytes.Length, _ringBuffer.Length - _writePos);
            pcmBytes[..firstCopy].CopyTo(_ringBuffer.AsSpan(_writePos, firstCopy));
            var remainder = pcmBytes.Length - firstCopy;
            if (remainder > 0)
            {
                pcmBytes[firstCopy..].CopyTo(_ringBuffer.AsSpan(0, remainder));
            }

            _writePos = (_writePos + pcmBytes.Length) % _ringBuffer.Length;
            _count += pcmBytes.Length;
        }

        _dataSignal.Set();
    }

    public void Complete()
    {
        _completed = true;
        _dataSignal.Set();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        if (count == 0)
        {
            return 0;
        }

        // Always fulfill the exact requested byte count while running: COM IStream / SAPI treats
        // short reads (bytesRead < count) as end-of-stream and terminates continuous recognition.
        var totalWritten = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var exactDurationMs = Math.Clamp((int)Math.Round((double)count / BytesPerMillisecond16kMono), 15, 100);

        while (totalWritten < count)
        {
            if (_completed)
            {
                return 0;
            }

            lock (_lock)
            {
                if (_count > 0)
                {
                    var toRead = Math.Min(count - totalWritten, _count);
                    var firstPart = Math.Min(toRead, _ringBuffer.Length - _readPos);
                    Array.Copy(_ringBuffer, _readPos, buffer, offset + totalWritten, firstPart);
                    var secondPart = toRead - firstPart;
                    if (secondPart > 0)
                    {
                        Array.Copy(_ringBuffer, 0, buffer, offset + totalWritten + firstPart, secondPart);
                    }

                    _readPos = (_readPos + toRead) % _ringBuffer.Length;
                    _count -= toRead;
                    totalWritten += toRead;
                    continue;
                }
            }

            // Allow +35 ms jitter headroom once real audio has started filling this frame so OS scheduling jitter never injects mid-word zero gaps.
            var deadlineMs = totalWritten > 0 ? exactDurationMs + 35 : exactDurationMs;
            var remainingWaitMs = deadlineMs - (int)sw.ElapsedMilliseconds;
            if (remainingWaitMs <= 0)
            {
                Array.Clear(buffer, offset + totalWritten, count - totalWritten);
                totalWritten = count;
                break;
            }

            _dataSignal.WaitOne(Math.Min(remainingWaitMs, 15));
        }

        Interlocked.Add(ref _totalBytesRead, totalWritten);
        return totalWritten;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => Volatile.Read(ref _totalBytesRead);
    public override void SetLength(long value) { }
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Complete();
            _dataSignal.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Callback receiving 16 kHz normalized mono float samples [-1.0, 1.0].
/// </summary>
public delegate void Float16kSpanHandler(ReadOnlySpan<float> float16kMonoSamples);

/// <summary>
/// Gentle automatic speech gain normalizer (AGC) that lifts quiet or Windows-ducked VoIP voices
/// (Google Meet, Zoom, Teams) smoothly without pumping syllables or distorting speech formants.
/// </summary>
public sealed class AdaptiveSpeechGainProcessor
{
    private const float NoiseFloorRms = 0.0020f;
    private const float TargetRms = 0.14f;
    private const float MaxGain = 3.0f;
    private const float PeakCeiling = 0.90f;

    private float _currentGain = 1.0f;
    private float _smoothedRms = 0.05f;

    public void ProcessInPlace(Span<float> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        double sumSq = 0.0;
        float blockPeak = 0f;
        for (var i = 0; i < samples.Length; i++)
        {
            var s = samples[i];
            sumSq += (double)s * s;
            var abs = MathF.Abs(s);
            if (abs > blockPeak)
            {
                blockPeak = abs;
            }
        }

        var rms = MathF.Sqrt((float)(sumSq / samples.Length));
        if (rms < NoiseFloorRms)
        {
            _currentGain += (1.0f - _currentGain) * 0.02f;
            ApplyLinearGain(samples, _currentGain);
            return;
        }

        var coeff = rms > _smoothedRms ? 0.08f : 0.015f;
        _smoothedRms += (rms - _smoothedRms) * coeff;

        var desiredGain = Math.Clamp(TargetRms / Math.Max(_smoothedRms, 0.008f), 1.0f, MaxGain);
        if (blockPeak * desiredGain > PeakCeiling && blockPeak > 0.001f)
        {
            desiredGain = Math.Min(desiredGain, PeakCeiling / blockPeak);
        }

        var gainRate = desiredGain < _currentGain ? 0.20f : 0.03f;
        _currentGain += (desiredGain - _currentGain) * gainRate;

        ApplyLinearGain(samples, _currentGain);
    }

    public void Reset()
    {
        _currentGain = 1.0f;
        _smoothedRms = 0.05f;
    }

    private static void ApplyLinearGain(Span<float> samples, float gain)
    {
        if (MathF.Abs(gain - 1.0f) < 0.02f)
        {
            return;
        }

        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = Math.Clamp(samples[i] * gain, -0.98f, 0.98f);
        }
    }
}

/// <summary>
/// Reusable shared WASAPI stream recorder wrapper handling level smoothing, polling loopback with fallback,
/// anti-aliased format conversion to 16 kHz PCM/float, and deadlock-free teardown.
/// </summary>
public sealed class WasapiStreamCaptureSession : IDisposable
{
    private readonly object _lock = new();
    private readonly bool _isLoopback;
    private readonly Action<double> _onLevel;
    private readonly Action<Exception?> _onStoppedUnexpectedly;
    private readonly AdaptiveSpeechGainProcessor _speechGain = new();

    private WasapiRecorder? _recorder;
    private WaveFormat? _activeWaveFormat;
    private MMDevice? _activeDevice;
    private Pcm16kSpanHandler? _pcmSink;
    private Float16kSpanHandler? _floatSink;
    private double _smoothedLevel;
    private long _lastEmitTickMs;
    private bool _lastEmittedZero = true;
    private double _resamplePhase;
    private float _previousMonoSample;
    private float[] _floatResampleBuffer = new float[2048];
    private byte[] _resampleBuffer = new byte[4096];
    private volatile bool _capturing;
    private volatile bool _suppressOutput;
    private bool _disposed;

    public WasapiStreamCaptureSession(
        bool isLoopback,
        Action<double> onLevel,
        Action<Exception?> onStoppedUnexpectedly)
    {
        _isLoopback = isLoopback;
        _onLevel = onLevel;
        _onStoppedUnexpectedly = onStoppedUnexpectedly;
    }

    public MMDevice? ActiveDevice
    {
        get
        {
            lock (_lock)
            {
                return _activeDevice;
            }
        }
    }

    public void SetSuppressOutput(bool suppress) => _suppressOutput = suppress;

    public void SetPcmSink(Pcm16kSpanHandler? sink) => Volatile.Write(ref _pcmSink, sink);

    public void SetFloatSink(Float16kSpanHandler? sink) => Volatile.Write(ref _floatSink, sink);

    public void Start(MMDevice device, Action<MMDevice>? configureDevice = null)
    {
        ArgumentNullException.ThrowIfNull(device);

        Stop();

        lock (_lock)
        {
            if (_disposed)
            {
                device.Dispose();
                return;
            }

            var expectedFlow = _isLoopback ? DataFlow.Render : DataFlow.Capture;
            if (device.DataFlow != expectedFlow)
            {
                device.Dispose();
                throw new InvalidOperationException(_isLoopback
                    ? "System audio loopback capture requires a Windows playback (Render) device."
                    : "Microphone capture requires a physical Windows input (Capture) device.");
            }

            _activeDevice = device;
            configureDevice?.Invoke(device);

            // Use PollingSync for WASAPI loopback (standard Microsoft/NAudio practice to prevent driver
            // EventSync hangs on Realtek / Intel SST / Bluetooth HFP endpoints), with EventSync fallback.
            var preferPolling = _isLoopback;
            WasapiRecorder? recorder = null;
            try
            {
                recorder = StartRecorderWithMode(device, _isLoopback, preferPolling);
            }
            catch
            {
                recorder?.Dispose();
                recorder = StartRecorderWithMode(device, _isLoopback, !preferPolling);
            }

            _recorder = recorder;
            _smoothedLevel = 0.0;
            _lastEmitTickMs = 0;
            _lastEmittedZero = true;
            _resamplePhase = 0.0;
            _previousMonoSample = 0f;
            _speechGain.Reset();
            Volatile.Write(ref _activeWaveFormat, recorder.WaveFormat);
            _capturing = true;
        }
    }

    private WasapiRecorder StartRecorderWithMode(MMDevice device, bool isLoopback, bool usePolling)
    {
        var builder = new WasapiRecorderBuilder()
            .WithDevice(device)
            .WithSharedMode();
        if (usePolling)
        {
            builder.WithPollingSync();
        }
        else
        {
            builder.WithEventSync();
        }

        if (isLoopback)
        {
            builder.WithLoopbackCapture();
        }

        var recorder = builder.Build();
        recorder.DataAvailable += OnDataAvailable;
        recorder.RecordingStopped += OnRecordingStopped;
        try
        {
            recorder.StartRecording();
            return recorder;
        }
        catch
        {
            recorder.DataAvailable -= OnDataAvailable;
            recorder.RecordingStopped -= OnRecordingStopped;
            recorder.Dispose();
            throw;
        }
    }

    public void Stop(Action<MMDevice>? cleanupDevice = null)
    {
        _capturing = false;
        WasapiRecorder? oldRecorder;
        MMDevice? oldDevice;

        lock (_lock)
        {
            oldRecorder = _recorder;
            _recorder = null;
            oldDevice = _activeDevice;
            _activeDevice = null;
            Volatile.Write(ref _activeWaveFormat, null);
            _smoothedLevel = 0.0;
            _lastEmittedZero = true;
        }

        if (oldRecorder is not null)
        {
            oldRecorder.DataAvailable -= OnDataAvailable;
            oldRecorder.RecordingStopped -= OnRecordingStopped;
            try
            {
                oldRecorder.StopRecording();
            }
            catch
            {
                // Ignore stop errors when device already disconnected.
            }

            try
            {
                oldRecorder.Dispose();
            }
            catch
            {
                // Ignore COM teardown errors on removed endpoints.
            }
        }

        if (oldDevice is not null)
        {
            try
            {
                cleanupDevice?.Invoke(oldDevice);
            }
            catch
            {
                // Ignore unsubscribe cleanup issues.
            }

            oldDevice.Dispose();
        }
    }

    private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (!_capturing)
        {
            return;
        }

        var format = Volatile.Read(ref _activeWaveFormat);
        if (format is null)
        {
            return;
        }

        var now = Environment.TickCount64;
        var isSilent = _suppressOutput || (flags & AudioClientBufferFlags.Silent) != 0 || buffer.IsEmpty;
        var rawPeak = isSilent
            ? 0.0
            : AudioProcessingHelper.ComputePeakAmplitude(buffer, format);

        var perceptual = rawPeak > 0.001 ? Math.Pow(rawPeak, 0.55) : 0.0;
        _smoothedLevel = Math.Max(perceptual, _smoothedLevel * 0.72);
        var currentLevel = _smoothedLevel < 0.002 ? 0.0 : Math.Clamp(_smoothedLevel, 0.0, 1.0);

        if (now - _lastEmitTickMs >= 33)
        {
            var isZero = currentLevel <= 0.0;
            if (!isZero || !_lastEmittedZero)
            {
                _lastEmitTickMs = now;
                _lastEmittedZero = isZero;
                _onLevel(currentLevel);
            }
        }

        var floatSink = Volatile.Read(ref _floatSink);
        var pcmSink = Volatile.Read(ref _pcmSink);
        if (floatSink is null && pcmSink is null || isSilent)
        {
            return;
        }

        var floatCount = AudioProcessingHelper.ConvertToFloat16kMono(
            buffer,
            format,
            ref _resamplePhase,
            ref _previousMonoSample,
            ref _floatResampleBuffer);

        if (floatCount > 0)
        {
            var floatSpan = _floatResampleBuffer.AsSpan(0, floatCount);
            if (floatSink is not null)
            {
                floatSink(floatSpan);
            }

            if (pcmSink is not null)
            {
                _speechGain.ProcessInPlace(floatSpan);
                var pcmBytes = AudioProcessingHelper.EncodeFloatToPcm16(floatSpan, ref _resampleBuffer);
                if (pcmBytes > 0)
                {
                    pcmSink(_resampleBuffer.AsSpan(0, pcmBytes));
                }
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null && _capturing)
        {
            _onStoppedUnexpectedly(e.Exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}

/// <summary>
/// Thread-safe bounded ring buffer for 16 kHz mono normalized float frames with automatic latency cap
/// to prevent synchronization drift between live microphone and system audio capture streams.
/// </summary>
public sealed class FloatSampleRingBuffer
{
    private readonly float[] _buffer;
    private readonly object _lock = new();
    private readonly int _trimTargetSamples;

    private int _readPos;
    private int _writePos;
    private int _count;

    public FloatSampleRingBuffer(int capacitySamples = 3200, int trimTargetSamples = 640)
    {
        _buffer = new float[Math.Max(640, capacitySamples)];
        _trimTargetSamples = Math.Clamp(trimTargetSamples, 160, _buffer.Length / 2);
    }

    public int BufferedSamples
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    public void Write(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        lock (_lock)
        {
            if (samples.Length >= _buffer.Length)
            {
                samples = samples[^_trimTargetSamples..];
                _readPos = 0;
                _writePos = 0;
                _count = 0;
            }

            // Cap latency if a source bursts ahead of playback consumption.
            var free = _buffer.Length - _count;
            if (samples.Length > free)
            {
                var targetKeep = Math.Min(_trimTargetSamples, _buffer.Length - samples.Length);
                var drop = Math.Max(0, _count - targetKeep);
                if (drop > 0)
                {
                    _readPos = (_readPos + drop) % _buffer.Length;
                    _count -= drop;
                }
            }

            var firstCopy = Math.Min(samples.Length, _buffer.Length - _writePos);
            samples[..firstCopy].CopyTo(_buffer.AsSpan(_writePos, firstCopy));
            var remainder = samples.Length - firstCopy;
            if (remainder > 0)
            {
                samples[firstCopy..].CopyTo(_buffer.AsSpan(0, remainder));
            }

            _writePos = (_writePos + samples.Length) % _buffer.Length;
            _count += samples.Length;
        }
    }

    public int ReadInto(Span<float> destination)
    {
        destination.Clear();
        if (destination.IsEmpty)
        {
            return 0;
        }

        lock (_lock)
        {
            var toRead = Math.Min(destination.Length, _count);
            if (toRead == 0)
            {
                return 0;
            }

            var firstPart = Math.Min(toRead, _buffer.Length - _readPos);
            _buffer.AsSpan(_readPos, firstPart).CopyTo(destination);
            var secondPart = toRead - firstPart;
            if (secondPart > 0)
            {
                _buffer.AsSpan(0, secondPart).CopyTo(destination.Slice(firstPart));
            }

            _readPos = (_readPos + toRead) % _buffer.Length;
            _count -= toRead;
            return toRead;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _readPos = 0;
            _writePos = 0;
            _count = 0;
        }
    }
}

/// <summary>
/// Real-time NAudio sample-provider graph mixing synchronized Microphone + System Audio loopback streams
/// via BufferedWaveProvider -> WdlResamplingSampleProvider -> MonoToStereoSampleProvider -> VolumeSampleProvider -> MixingSampleProvider
/// at uniform 48 kHz Stereo IEEE Float with anti-clipping limiting and Echo/Feedback Guard support.
/// </summary>
public sealed class SynchronizedMixedAudioWaveProvider : NAudio.Wave.IWaveProvider
{
    private const int MaxSourceBufferedBytes = 16000 * sizeof(float) * 160 / 1000; // 160ms cap at 16kHz float mono

    private readonly NAudio.Wave.BufferedWaveProvider _micBuffer;
    private readonly NAudio.Wave.BufferedWaveProvider _sysBuffer;
    private readonly NAudio.Wave.SampleProviders.VolumeSampleProvider _micVolume;
    private readonly NAudio.Wave.SampleProviders.VolumeSampleProvider _sysVolume;
    private readonly NAudio.Wave.SampleProviders.MixingSampleProvider _mixer;
    private readonly NAudio.Wave.SampleProviders.SampleToWaveProvider _outputWaveProvider;
    private readonly Action<double>? _onLevelMeasured;
    private readonly object _mixLock = new();

    private byte[] _micPushBytes = new byte[4096];
    private byte[] _sysPushBytes = new byte[4096];
    private double _smoothedLevel;
    private long _lastLevelTickMs;
    private bool _lastEmittedZero = true;

    private volatile float _micGain = 1.0f;
    private volatile float _sysGain = 1.0f;
    private volatile bool _micMuted;
    private volatile bool _sysMuted;
    private volatile bool _echoGuardSuppressed;

    public SynchronizedMixedAudioWaveProvider(WaveFormat targetFormat, Action<double>? onLevelMeasured = null)
    {
        ArgumentNullException.ThrowIfNull(targetFormat);

        var targetRate = targetFormat.SampleRate > 0 ? targetFormat.SampleRate : 48000;
        var mixerFormat = WaveFormat.CreateIeeeFloatWaveFormat(targetRate, 2);
        WaveFormat = mixerFormat;
        _onLevelMeasured = onLevelMeasured;

        var srcFormat = WaveFormat.CreateIeeeFloatWaveFormat(AudioProcessingHelper.TargetSpeechSampleRate, 1);
        _micBuffer = new NAudio.Wave.BufferedWaveProvider(srcFormat)
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
        _sysBuffer = new NAudio.Wave.BufferedWaveProvider(srcFormat)
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };

        var micSample = new NAudio.Wave.SampleProviders.WaveToSampleProvider(_micBuffer);
        var micResampled = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(micSample, targetRate);
        var micStereo = new NAudio.Wave.SampleProviders.MonoToStereoSampleProvider(micResampled);
        _micVolume = new NAudio.Wave.SampleProviders.VolumeSampleProvider(micStereo) { Volume = 1.0f };

        var sysSample = new NAudio.Wave.SampleProviders.WaveToSampleProvider(_sysBuffer);
        var sysResampled = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(sysSample, targetRate);
        var sysStereo = new NAudio.Wave.SampleProviders.MonoToStereoSampleProvider(sysResampled);
        _sysVolume = new NAudio.Wave.SampleProviders.VolumeSampleProvider(sysStereo) { Volume = 1.0f };

        _mixer = new NAudio.Wave.SampleProviders.MixingSampleProvider([_micVolume, _sysVolume])
        {
            ReadFully = true
        };

        var limitedSample = new SoftLimitingMeterSampleProvider(_mixer, OnPeakMeasured);
        _outputWaveProvider = new NAudio.Wave.SampleProviders.SampleToWaveProvider(limitedSample);
    }

    public WaveFormat WaveFormat { get; }

    public void SetMicrophoneMix(double volumePercent, bool muted)
    {
        _micGain = (float)Math.Clamp(volumePercent / 100.0, 0.0, 1.5);
        _micMuted = muted;
        _micVolume.Volume = muted ? 0f : _micGain;
        if (muted)
        {
            _micBuffer.ClearBuffer();
        }
    }

    public void SetSystemAudioMix(double volumePercent, bool muted)
    {
        _sysGain = (float)Math.Clamp(volumePercent / 100.0, 0.0, 1.5);
        _sysMuted = muted;
        UpdateEffectiveSystemVolume();
        if (muted)
        {
            _sysBuffer.ClearBuffer();
        }
    }

    public void SetEchoGuardSuppressed(bool suppressed)
    {
        _echoGuardSuppressed = suppressed;
        UpdateEffectiveSystemVolume();
        _sysBuffer.ClearBuffer();
    }

    public void PushMicrophoneFloat16k(ReadOnlySpan<float> samples)
    {
        if (_micMuted || samples.IsEmpty)
        {
            return;
        }

        lock (_mixLock)
        {
            if (_micBuffer.BufferedBytes > MaxSourceBufferedBytes)
            {
                _micBuffer.ClearBuffer();
            }

            var byteCount = samples.Length * sizeof(float);
            if (_micPushBytes.Length < byteCount)
            {
                _micPushBytes = new byte[byteCount];
            }

            System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples).CopyTo(_micPushBytes);
            _micBuffer.AddSamples(_micPushBytes, 0, byteCount);
        }
    }

    public void PushSystemAudioFloat16k(ReadOnlySpan<float> samples)
    {
        if (_sysMuted || _echoGuardSuppressed || samples.IsEmpty)
        {
            return;
        }

        lock (_mixLock)
        {
            if (_sysBuffer.BufferedBytes > MaxSourceBufferedBytes)
            {
                _sysBuffer.ClearBuffer();
            }

            var byteCount = samples.Length * sizeof(float);
            if (_sysPushBytes.Length < byteCount)
            {
                _sysPushBytes = new byte[byteCount];
            }

            System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples).CopyTo(_sysPushBytes);
            _sysBuffer.AddSamples(_sysPushBytes, 0, byteCount);
        }
    }

    public void Reset()
    {
        lock (_mixLock)
        {
            _micBuffer.ClearBuffer();
            _sysBuffer.ClearBuffer();
            _smoothedLevel = 0.0;
            _lastEmittedZero = true;
        }
    }

    public int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        // Pull synchronized audio through NAudio MixingSampleProvider -> SoftLimiter -> IEEE Float bytes
        return _outputWaveProvider.Read(buffer);
    }

    private void UpdateEffectiveSystemVolume() =>
        _sysVolume.Volume = (_sysMuted || _echoGuardSuppressed) ? 0f : _sysGain;

    private void OnPeakMeasured(float peak)
    {
        if (_onLevelMeasured is null)
        {
            return;
        }

        var perceptual = peak > 0.001 ? Math.Pow(peak, 0.55) : 0.0;
        _smoothedLevel = Math.Max(perceptual, _smoothedLevel * 0.72);
        var current = _smoothedLevel < 0.002 ? 0.0 : Math.Clamp(_smoothedLevel, 0.0, 1.0);

        var now = Environment.TickCount64;
        if (now - _lastLevelTickMs >= 33)
        {
            var isZero = current <= 0.0;
            if (!isZero || !_lastEmittedZero)
            {
                _lastLevelTickMs = now;
                _lastEmittedZero = isZero;
                _onLevelMeasured(current);
            }
        }
    }

    private sealed class SoftLimitingMeterSampleProvider(
        NAudio.Wave.ISampleProvider source,
        Action<float> onPeak) : NAudio.Wave.ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public int Read(Span<float> buffer)
        {
            var read = source.Read(buffer);
            float peak = 0f;
            for (var i = 0; i < read; i++)
            {
                var limited = SoftLimitSample(buffer[i]);
                buffer[i] = limited;
                var abs = MathF.Abs(limited);
                if (abs > peak)
                {
                    peak = abs;
                }
            }

            onPeak(peak);
            return read;
        }

        private static float SoftLimitSample(float x)
        {
            if (x is >= -0.85f and <= 0.85f)
            {
                return x;
            }

            var sign = x < 0f ? -1f : 1f;
            var excess = MathF.Abs(x) - 0.85f;
            var compressed = 0.85f + (0.13f * (excess / (excess + 0.13f)));
            return sign * Math.Clamp(compressed, 0f, 0.98f);
        }
    }
}

/// <summary>
/// Shared virtual audio cable identifiers used to prevent capture-playback feedback loops
/// and locate signed virtual microphone routing targets.
/// </summary>
public static class VirtualAudioDriverCatalog
{
    public static bool IsVirtualAudioRenderSink(string? friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName))
        {
            return false;
        }

        return friendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase)
            || friendlyName.Contains("CABLE In 16ch", StringComparison.OrdinalIgnoreCase)
            || friendlyName.Contains("Hi-Fi Cable Input", StringComparison.OrdinalIgnoreCase)
            || friendlyName.Contains("VoiceMeeter Input", StringComparison.OrdinalIgnoreCase)
            || friendlyName.Contains("VoiceMeeter Aux Input", StringComparison.OrdinalIgnoreCase)
            || friendlyName.Contains("VoiceMeeter VAIO", StringComparison.OrdinalIgnoreCase)
            || friendlyName.Contains("Virtual Audio Cable", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsVirtualLoopbackCaptureDevice(string? friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName))
        {
            return false;
        }

        return friendlyName.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase)
            || friendlyName.Contains("Hi-Fi Cable Output", StringComparison.OrdinalIgnoreCase)
            || friendlyName.Contains("VoiceMeeter Output", StringComparison.OrdinalIgnoreCase)
            || friendlyName.Contains("VoiceMeeter Aux Output", StringComparison.OrdinalIgnoreCase)
            || friendlyName.Contains("VoiceMeeter VAIO", StringComparison.OrdinalIgnoreCase)
            || friendlyName.Contains("Virtual Audio Cable", StringComparison.OrdinalIgnoreCase)
            || friendlyName.Contains("Stereo Mix", StringComparison.OrdinalIgnoreCase);
    }

    public static string InferPairedCaptureMicHint(string? renderFriendlyName)
    {
        if (string.IsNullOrWhiteSpace(renderFriendlyName))
        {
            return "CABLE Output (VB-Audio Virtual Cable)";
        }

        if (renderFriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
        {
            return renderFriendlyName.Replace("CABLE Input", "CABLE Output", StringComparison.OrdinalIgnoreCase);
        }

        if (renderFriendlyName.Contains("Hi-Fi Cable Input", StringComparison.OrdinalIgnoreCase))
        {
            return renderFriendlyName.Replace("Hi-Fi Cable Input", "Hi-Fi Cable Output", StringComparison.OrdinalIgnoreCase);
        }

        if (renderFriendlyName.Contains("VoiceMeeter Aux Input", StringComparison.OrdinalIgnoreCase))
        {
            return renderFriendlyName.Replace("VoiceMeeter Aux Input", "VoiceMeeter Aux Output", StringComparison.OrdinalIgnoreCase);
        }

        if (renderFriendlyName.Contains("VoiceMeeter Input", StringComparison.OrdinalIgnoreCase))
        {
            return renderFriendlyName.Replace("VoiceMeeter Input", "VoiceMeeter Output", StringComparison.OrdinalIgnoreCase);
        }

        return renderFriendlyName;
    }
}
