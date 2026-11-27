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
    /// seamlessly via phase-continuous linear interpolation into 16 kHz, 16-bit signed PCM bytes.
    /// </summary>
    public static int ConvertToPcm16kMono(
        ReadOnlySpan<byte> input,
        WaveFormat format,
        ref double phasePosition,
        ref float previousSample,
        ref byte[] scratchBuffer)
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
            var requiredBytes = maxOutSamples * 2;
            if (scratchBuffer.Length < requiredBytes)
            {
                scratchBuffer = new byte[requiredBytes];
            }

            var outIdx = 0;
            while (phasePosition < frameCount)
            {
                var idx = (int)Math.Floor(phasePosition);
                var frac = (float)(phasePosition - idx);

                var s0 = idx <= 0 ? previousSample : rentedMono[idx - 1];
                var s1 = rentedMono[Math.Clamp(idx, 0, frameCount - 1)];
                var interpolated = s0 + ((s1 - s0) * frac);

                var scaled = (int)Math.Round(Math.Clamp(interpolated, -1.0f, 1.0f) * 32767.0f);
                var pcmSample = (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(scratchBuffer.AsSpan(outIdx, 2), pcmSample);

                outIdx += 2;
                phasePosition += step;
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
/// Reusable shared WASAPI stream recorder wrapper handling level smoothing, format conversion to 16 kHz PCM,
/// and deadlock-free teardown for both DataFlow.Capture (microphone) and DataFlow.Render loopback (system audio).
/// </summary>
public sealed class WasapiStreamCaptureSession : IDisposable
{
    private readonly object _lock = new();
    private readonly bool _isLoopback;
    private readonly Action<double> _onLevel;
    private readonly Action<Exception?> _onStoppedUnexpectedly;

    private WasapiRecorder? _recorder;
    private WaveFormat? _activeWaveFormat;
    private MMDevice? _activeDevice;
    private Pcm16kSpanHandler? _pcmSink;
    private double _smoothedLevel;
    private long _lastEmitTickMs;
    private double _resamplePhase;
    private float _previousMonoSample;
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

            var builder = new WasapiRecorderBuilder()
                .WithDevice(device)
                .WithSharedMode()
                .WithEventSync();
            if (_isLoopback)
            {
                builder.WithLoopbackCapture();
            }

            var recorder = builder.Build();
            Volatile.Write(ref _activeWaveFormat, recorder.WaveFormat);
            recorder.DataAvailable += OnDataAvailable;
            recorder.RecordingStopped += OnRecordingStopped;

            _recorder = recorder;
            _smoothedLevel = 0.0;
            _lastEmitTickMs = 0;
            _resamplePhase = 0.0;
            _previousMonoSample = 0f;
            _capturing = true;

            recorder.StartRecording();
            Volatile.Write(ref _activeWaveFormat, recorder.WaveFormat);
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

        var isSilent = _suppressOutput || (flags & AudioClientBufferFlags.Silent) != 0 || buffer.IsEmpty;
        var rawPeak = isSilent
            ? 0.0
            : AudioProcessingHelper.ComputePeakAmplitude(buffer, format);

        var perceptual = rawPeak > 0.001 ? Math.Pow(rawPeak, 0.55) : 0.0;
        _smoothedLevel = Math.Max(perceptual, _smoothedLevel * 0.72);

        var now = Environment.TickCount64;
        if (now - _lastEmitTickMs >= 33)
        {
            _lastEmitTickMs = now;
            _onLevel(Math.Clamp(_smoothedLevel, 0.0, 1.0));
        }

        var sink = Volatile.Read(ref _pcmSink);
        if (sink is not null && !isSilent)
        {
            var pcmBytes = AudioProcessingHelper.ConvertToPcm16kMono(
                buffer,
                format,
                ref _resamplePhase,
                ref _previousMonoSample,
                ref _resampleBuffer);
            if (pcmBytes > 0)
            {
                sink(_resampleBuffer.AsSpan(0, pcmBytes));
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
