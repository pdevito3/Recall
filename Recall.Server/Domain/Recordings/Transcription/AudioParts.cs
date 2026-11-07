namespace Recall.Server.Domain.Recordings.Transcription;

using System.Buffers.Binary;
using System.Text;

/// <summary>
/// Where a provider with a limit on the size of a file gets its parts. A part is a WAV file of 16 kHz mono PCM, and the next part
/// starts at the sample where the last one ended, so the text of each part joins once, in order, with no gap. A part that must
/// end before the rest of the audio ends at the quietest frame near the limit, so a cut falls in a pause and not in a word.
/// </summary>
public static class AudioParts
{
    public const int WavHeaderBytes = 44;
    public const int BytesPerSample = 2;

    /// <summary>50 ms, shorter than a pause between two words.</summary>
    public const int FrameSamples = Recording.SamplesPerSecond / 20;

    /// <summary>The last 5 s before a limit, where a cut can fall.</summary>
    public const int WindowSamples = 5 * Recording.SamplesPerSecond;

    public static long MaxSamplesIn(long maxFileBytes) => (maxFileBytes - WavHeaderBytes) / BytesPerSample;

    /// <summary>The window to read before a cut, or null when the rest from <paramref name="start"/> fits in one part.</summary>
    public static (long From, int Length)? WindowBeforeLimit(long start, long total, long maxSamples)
    {
        if (total - start <= maxSamples)
            return null;

        var length = (int)Math.Min(WindowSamples, maxSamples / 2);
        return (start + maxSamples - length, length);
    }

    /// <summary>The end of a part: the middle of the quietest frame of the window. The later frame wins a tie, so a part is as long as it can be.</summary>
    public static long CutIn(long windowFrom, ReadOnlySpan<short> window)
    {
        var frame = Math.Min(FrameSamples, window.Length);
        var bestStart = 0;
        var bestEnergy = long.MaxValue;
        for (var at = 0; at + frame <= window.Length; at += frame)
        {
            long energy = 0;
            foreach (var sample in window.Slice(at, frame))
                energy += (long)sample * sample;
            if (energy <= bestEnergy)
            {
                bestEnergy = energy;
                bestStart = at;
            }
        }

        return windowFrom + bestStart + (frame + 1) / 2;
    }

    public static byte[] WavOf(ReadOnlySpan<byte> pcm)
    {
        var wav = new byte[WavHeaderBytes + pcm.Length];
        var header = wav.AsSpan(0, WavHeaderBytes);
        Encoding.ASCII.GetBytes("RIFF", header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], 36 + pcm.Length);
        Encoding.ASCII.GetBytes("WAVEfmt ", header[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], Recording.SamplesPerSecond);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], Recording.SamplesPerSecond * BytesPerSample);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], BytesPerSample);
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], 16);
        Encoding.ASCII.GetBytes("data", header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], pcm.Length);
        pcm.CopyTo(wav.AsSpan(WavHeaderBytes));
        return wav;
    }
}

/// <summary>The samples of the WAV file that <see cref="WorkAudio"/> wrote.</summary>
public sealed class PcmAudio : IDisposable
{
    private readonly FileStream _file;
    private readonly long _dataStart;

    private PcmAudio(FileStream file, long dataStart, long samples)
    {
        _file = file;
        _dataStart = dataStart;
        Samples = samples;
    }

    public long Samples { get; }

    /// <summary>Finds the <c>data</c> chunk. A size that runs past the end of the file, as a stream writer leaves it, counts to the end.</summary>
    public static PcmAudio Open(string wavPath)
    {
        var file = File.OpenRead(wavPath);
        try
        {
            Span<byte> head = stackalloc byte[12];
            file.ReadExactly(head);
            if (!head[..4].SequenceEqual("RIFF"u8) || !head[8..12].SequenceEqual("WAVE"u8))
                throw new TranscriptionEngineException(RecordingProblem.AudioUnreadable);

            Span<byte> chunk = stackalloc byte[8];
            while (file.Position + 8 <= file.Length)
            {
                file.ReadExactly(chunk);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
                if (chunk[..4].SequenceEqual("data"u8))
                {
                    var bytes = Math.Min(size, file.Length - file.Position);
                    return new PcmAudio(file, file.Position, bytes / AudioParts.BytesPerSample);
                }

                file.Position += size + (size & 1);
            }

            throw new TranscriptionEngineException(RecordingProblem.AudioUnreadable);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public byte[] ReadBytes(long fromSample, long toSample)
    {
        var bytes = new byte[(toSample - fromSample) * AudioParts.BytesPerSample];
        _file.Position = _dataStart + fromSample * AudioParts.BytesPerSample;
        _file.ReadExactly(bytes);
        return bytes;
    }

    public short[] ReadSamples(long fromSample, int count)
    {
        var bytes = ReadBytes(fromSample, fromSample + count);
        var samples = new short[count];
        for (var i = 0; i < count; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * AudioParts.BytesPerSample));
        return samples;
    }

    /// <summary>The end of the part that starts at <paramref name="start"/>.</summary>
    public long EndOfPart(long start, long maxSamples)
        => AudioParts.WindowBeforeLimit(start, Samples, maxSamples) is var (from, length)
            ? AudioParts.CutIn(from, ReadSamples(from, length))
            : Samples;

    public void Dispose() => _file.Dispose();
}
