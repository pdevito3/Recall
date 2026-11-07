namespace Recall.UnitTests.Domain.Recordings;

using Recall.Server.Domain.Recordings;
using Recall.Server.Domain.Recordings.Transcription;
using Shouldly;

public class AudioPartsTests
{
    [Test]
    public void the_samples_of_a_part_fill_the_file_after_its_header()
    {
        // Arrange and act
        var samples = AudioParts.MaxSamplesIn(25_000_000);

        // Assert
        (AudioParts.WavHeaderBytes + (samples * AudioParts.BytesPerSample)).ShouldBeLessThanOrEqualTo(25_000_000);
        (AudioParts.WavHeaderBytes + ((samples + 1) * AudioParts.BytesPerSample)).ShouldBeGreaterThan(25_000_000);
    }

    [Test]
    [Arguments(16_000L, 16_000L, true)]
    [Arguments(16_001L, 16_000L, false)]
    public void the_rest_that_fits_takes_no_cut_and_one_sample_more_does(long total, long maxSamples, bool fits)
    {
        // Arrange and act
        var window = AudioParts.WindowBeforeLimit(0, total, maxSamples);

        // Assert
        (window is null).ShouldBe(fits);
    }

    [Test]
    public void a_window_ends_at_the_limit_so_a_cut_never_passes_it()
    {
        // Arrange and act
        var window = AudioParts.WindowBeforeLimit(1_000, 1_000_000, 200_000);

        // Assert
        window.ShouldNotBeNull();
        (window.Value.From + window.Value.Length).ShouldBe(1_000 + 200_000);
        window.Value.Length.ShouldBe(AudioParts.WindowSamples);
    }

    [Test]
    [Arguments(200_001L)]
    [Arguments(200_000L + AudioParts.MinPartSamples)]
    public void a_window_leaves_at_least_the_shortest_part_after_its_end(long rest)
    {
        // Arrange and act
        var window = AudioParts.WindowBeforeLimit(1_000, 1_000 + rest, 200_000);

        // Assert
        window.ShouldNotBeNull();
        (1_000 + rest - (window.Value.From + window.Value.Length)).ShouldBeGreaterThanOrEqualTo(AudioParts.MinPartSamples);
        (window.Value.From - 1_000).ShouldBeGreaterThanOrEqualTo(AudioParts.MinPartSamples);
    }

    [Test]
    public void a_cut_falls_in_the_middle_of_the_quietest_frame()
    {
        // Arrange
        var window = Enumerable.Repeat((short)900, 10 * AudioParts.FrameSamples).ToArray();
        Array.Clear(window, 3 * AudioParts.FrameSamples, AudioParts.FrameSamples);

        // Act
        var cut = AudioParts.CutIn(5_000, window);

        // Assert
        cut.ShouldBe(5_000 + (3 * AudioParts.FrameSamples) + (AudioParts.FrameSamples / 2));
    }

    [Test]
    public void with_no_pause_the_cut_falls_in_the_last_frame_so_a_part_is_as_long_as_it_can_be()
    {
        // Arrange
        var window = Enumerable.Repeat((short)900, 10 * AudioParts.FrameSamples).ToArray();

        // Act
        var cut = AudioParts.CutIn(0, window);

        // Assert
        cut.ShouldBe((9 * AudioParts.FrameSamples) + (AudioParts.FrameSamples / 2));
        cut.ShouldBeLessThan(window.Length);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(5_001)]
    public void each_part_of_a_file_starts_where_the_last_ended_and_together_they_are_the_file(int extra)
    {
        // Arrange
        var samples = (3 * Recording.SamplesPerSecond) + extra;
        var pcm = new byte[samples * AudioParts.BytesPerSample];
        new Random(7).NextBytes(pcm);
        var path = Path.Combine(Path.GetTempPath(), $"audio-parts-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, AudioParts.WavOf(pcm));
        const long maxSamples = Recording.SamplesPerSecond;

        // Act
        var joined = new List<byte>();
        var parts = 0;
        using (var audio = PcmAudio.Open(path))
        {
            for (long start = 0; start < audio.Samples; parts++)
            {
                var end = audio.EndOfPart(start, maxSamples);
                (end - start).ShouldBeLessThanOrEqualTo(maxSamples);
                end.ShouldBeGreaterThan(start);
                joined.AddRange(audio.ReadBytes(start, end));
                start = end;
            }
        }

        File.Delete(path);

        // Assert
        joined.ToArray().ShouldBe(pcm);
        parts.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Test]
    public void a_wav_with_a_chunk_before_its_data_and_a_size_past_its_end_reads_to_the_end()
    {
        // Arrange
        var pcm = new byte[] { 1, 0, 2, 0, 3, 0 };
        var wav = AudioParts.WavOf(pcm);
        var withList = wav[..36]
            .Concat("LIST"u8.ToArray()).Concat(new byte[] { 4, 0, 0, 0 }).Concat("INFO"u8.ToArray())
            .Concat("data"u8.ToArray()).Concat(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F })
            .Concat(pcm).ToArray();
        var path = Path.Combine(Path.GetTempPath(), $"audio-parts-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, withList);

        // Act
        long samples;
        byte[] read;
        using (var audio = PcmAudio.Open(path))
        {
            samples = audio.Samples;
            read = audio.ReadBytes(0, audio.Samples);
        }

        File.Delete(path);

        // Assert
        samples.ShouldBe(3);
        read.ShouldBe(pcm);
    }

    [Test]
    public void a_file_that_is_not_a_wav_reads_as_unreadable_audio()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), $"audio-parts-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, "not a wav file at all"u8.ToArray());

        // Act
        var thrown = Should.Throw<TranscriptionEngineException>(() => PcmAudio.Open(path).Dispose());
        File.Delete(path);

        // Assert
        thrown.Problem.ShouldBe(RecordingProblem.AudioUnreadable);
    }
}
