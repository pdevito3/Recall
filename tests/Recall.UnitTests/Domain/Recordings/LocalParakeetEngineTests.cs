namespace Recall.UnitTests.Domain.Recordings;

using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using Recall.Server.Domain.Recordings;
using Recall.Server.Domain.Recordings.Transcription;
using Shouldly;

/// <summary>The subprocess rules, with shell scripts in the place of ffmpeg and the worker, so each run is real and fast.</summary>
[UnsupportedOSPlatform("windows")]
public class LocalParakeetEngineTests
{
    private const string ChunkLine = """echo '{"endSample":16000,"totalSamples":16000,"text":"heard words"}'""";

    private sealed class Rig : IDisposable
    {
        public string Folder { get; } = Directory.CreateTempSubdirectory("recall_engine_").FullName;

        public void Dispose() => Directory.Delete(Folder, recursive: true);

        public string Script(string name, string body)
        {
            var path = Path.Combine(Folder, name);
            File.WriteAllText(path, $"#!/bin/sh\n{body}\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }

        public string WorkingFfmpeg() => Script("ffmpeg", """for last; do :; done; head -c 100 /dev/zero > "$last" """);

        public LocalParakeetEngine Engine(string worker, string? ffmpeg = null, int stallMinutes = 10)
        {
            var settings = Options.Create(new TranscriptionOptions
            {
                ModelRoot = Folder,
                FfmpegPath = ffmpeg ?? WorkingFfmpeg(),
                WorkerPath = worker,
                StallMinutes = stallMinutes,
            });
            return new LocalParakeetEngine(new ModelStore(settings), settings);
        }

        public TranscriptionInput Input(long start = 0) => new(Path.Combine(Folder, "audio.webm"), Folder, start);
    }

    private static async Task<List<TranscriptChunk>> CollectAsync(ITranscriptionEngine engine, TranscriptionInput input, CancellationToken cancellationToken = default)
    {
        var chunks = new List<TranscriptChunk>();
        await foreach (var chunk in engine.RunAsync(input, cancellationToken))
            chunks.Add(chunk);
        return chunks;
    }

    private static async Task<RecordingProblem> ProblemOfAsync(ITranscriptionEngine engine, TranscriptionInput input)
        => (await Should.ThrowAsync<TranscriptionEngineException>(() => CollectAsync(engine, input))).Problem;

    [Test]
    public async Task the_chunks_of_the_worker_come_through_in_order()
    {
        // Arrange
        using var rig = new Rig();
        var engine = rig.Engine(rig.Script("worker", ChunkLine));

        // Act
        var chunks = await CollectAsync(engine, rig.Input());

        // Assert
        chunks.ShouldBe([new TranscriptChunk(16_000, 16_000, "heard words")]);
    }

    [Test]
    public async Task the_worker_gets_the_start_sample_of_the_attempt()
    {
        // Arrange
        using var rig = new Rig();
        var seen = Path.Combine(rig.Folder, "seen");
        var engine = rig.Engine(rig.Script("worker", $"""echo "$4" > "{seen}" """));

        // Act
        await CollectAsync(engine, rig.Input(start: 480_000));

        // Assert
        (await File.ReadAllTextAsync(seen)).Trim().ShouldBe("480000");
    }

    [Test]
    public async Task an_attempt_reuses_the_decoded_audio_of_the_attempt_before()
    {
        // Arrange
        using var rig = new Rig();
        var runs = Path.Combine(rig.Folder, "runs");
        var ffmpeg = rig.Script("ffmpeg", $"""echo run >> "{runs}"; for last; do :; done; head -c 100 /dev/zero > "$last" """);
        var engine = rig.Engine(rig.Script("worker", ChunkLine), ffmpeg);

        // Act
        await CollectAsync(engine, rig.Input());
        await CollectAsync(engine, rig.Input());

        // Assert
        (await File.ReadAllLinesAsync(runs)).Length.ShouldBe(1);
    }

    [Test]
    public async Task a_worker_killed_by_the_kernel_is_out_of_memory()
    {
        // Arrange
        using var rig = new Rig();
        var engine = rig.Engine(rig.Script("worker", "kill -9 $$"));

        // Act
        var problem = await ProblemOfAsync(engine, rig.Input());

        // Assert
        problem.ShouldBe(RecordingProblem.OutOfMemory);
    }

    [Test]
    [Property("Rule", "REC-8")]
    public async Task the_words_on_the_error_stream_of_a_worker_stay_out_of_the_fault()
    {
        // Arrange
        using var rig = new Rig();
        var engine = rig.Engine(rig.Script("worker", "echo secret-words >&2; exit 1"));

        // Act
        var fault = await Should.ThrowAsync<TranscriptionEngineException>(() => CollectAsync(engine, rig.Input()));

        // Assert
        fault.ToString().ShouldNotContain("secret-words");
    }

    [Test]
    public async Task a_worker_that_fails_is_a_crash()
    {
        // Arrange
        using var rig = new Rig();
        var engine = rig.Engine(rig.Script("worker", "exit 1"));

        // Act
        var problem = await ProblemOfAsync(engine, rig.Input());

        // Assert
        problem.ShouldBe(RecordingProblem.EngineCrashed);
    }

    [Test]
    public async Task a_worker_that_writes_a_broken_line_is_a_crash()
    {
        // Arrange
        using var rig = new Rig();
        var engine = rig.Engine(rig.Script("worker", "echo not-json"));

        // Act
        var problem = await ProblemOfAsync(engine, rig.Input());

        // Assert
        problem.ShouldBe(RecordingProblem.EngineCrashed);
    }

    [Test]
    public async Task audio_that_ffmpeg_cannot_read_is_unreadable_and_leaves_no_work_file()
    {
        // Arrange
        using var rig = new Rig();
        var engine = rig.Engine(rig.Script("worker", ChunkLine), rig.Script("ffmpeg", "exit 1"));

        // Act
        var problem = await ProblemOfAsync(engine, rig.Input());

        // Assert
        problem.ShouldBe(RecordingProblem.AudioUnreadable);
        File.Exists(Path.Combine(rig.Folder, "work.wav")).ShouldBeFalse();
    }

    [Test]
    public async Task a_machine_with_no_ffmpeg_says_that_the_engine_is_not_installed()
    {
        // Arrange
        using var rig = new Rig();
        var engine = rig.Engine(rig.Script("worker", ChunkLine), Path.Combine(rig.Folder, "no-such-program"));

        // Act
        var problem = await ProblemOfAsync(engine, rig.Input());

        // Assert
        problem.ShouldBe(RecordingProblem.EngineNotInstalled);
    }

    [Test]
    public async Task audio_past_the_limit_is_too_long()
    {
        // Arrange
        using var rig = new Rig();
        var tooLong = (long)Recording.MaxDuration.TotalSeconds * Recording.SamplesPerSecond * 2 + 46;
        await using (var wav = File.Create(Path.Combine(rig.Folder, "work.wav")))
            wav.SetLength(tooLong);
        var engine = rig.Engine(rig.Script("worker", ChunkLine));

        // Act
        var problem = await ProblemOfAsync(engine, rig.Input());

        // Assert
        problem.ShouldBe(RecordingProblem.TooLong);
    }

    [Test]
    [Property("Rule", "REC-13")]
    public async Task the_token_kills_the_worker()
    {
        // Arrange
        using var rig = new Rig();
        var pidFile = Path.Combine(rig.Folder, "pid");
        var engine = rig.Engine(rig.Script("worker", $"""echo $$ > "{pidFile}"; exec sleep 120"""));
        using var stop = new CancellationTokenSource();

        // Act
        var run = CollectAsync(engine, rig.Input(), stop.Token);
        while (!File.Exists(pidFile) || (await File.ReadAllTextAsync(pidFile)).Trim().Length == 0)
            await Task.Delay(20);
        var pid = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        await stop.CancelAsync();

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(() => run);
        using var worker = TryGetProcess(pid);
        if (worker is not null)
            (await WaitForExitAsync(worker)).ShouldBeTrue("The worker must end when the token ends");
    }

    [Test]
    [Property("Rule", "REC-9")]
    public async Task the_worker_does_not_get_the_secrets_of_the_server()
    {
        // Arrange
        using var rig = new Rig();
        var seen = Path.Combine(rig.Folder, "env");
        var engine = rig.Engine(rig.Script("worker", $"""env > "{seen}" """));
        Environment.SetEnvironmentVariable("RECALL_TEST_SECRET", "secret-value");

        try
        {
            // Act
            await CollectAsync(engine, rig.Input());

            // Assert
            (await File.ReadAllTextAsync(seen)).ShouldNotContain("secret-value");
        }
        finally
        {
            Environment.SetEnvironmentVariable("RECALL_TEST_SECRET", null);
        }
    }

    [Test]
    public async Task a_worker_that_goes_silent_is_stopped_as_a_stall()
    {
        // Arrange
        using var rig = new Rig();
        var engine = rig.Engine(rig.Script("worker", "exec sleep 120"), stallMinutes: 0);

        // Act
        var problem = await ProblemOfAsync(engine, rig.Input());

        // Assert
        problem.ShouldBe(RecordingProblem.EngineStalled);
    }

    private static System.Diagnostics.Process? TryGetProcess(int pid)
    {
        try
        {
            return System.Diagnostics.Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static async Task<bool> WaitForExitAsync(System.Diagnostics.Process process)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(limit.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
