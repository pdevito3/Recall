namespace Recall.UnitTests.Domain.Recordings;

using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using Recall.Server.Domain.Recordings;
using Recall.Server.Domain.Recordings.Transcription;
using Shouldly;

[UnsupportedOSPlatform("windows")]
public class LocalParakeetEngineTests
{
    private const string ChunkLine = """echo '{"endSample":16000,"totalSamples":16000,"text":"heard words"}'""";

    private sealed class Rig : IDisposable
    {
        public string Folder { get; } = Directory.CreateTempSubdirectory("recall_engine_").FullName;

        public void Dispose() => Directory.Delete(Folder, recursive: true);

        // Linux refuses to start a file that a process holds open for write ("Text file busy"), and a child that a parallel
        // test forks holds a copy of each open handle of this process until its exec. A shell writes the script, so this
        // process never holds a handle on it to copy.
        public string Script(string name, string body)
        {
            var path = Path.Combine(Folder, name);
            using var shell = Process.Start(new ProcessStartInfo("/bin/sh")
            {
                ArgumentList = { "-c", """printf '%s' "$1" > "$2" && chmod 700 "$2" """, "sh", $"#!/bin/sh\n{body}\n", path },
            })!;
            shell.WaitForExit();
            shell.ExitCode.ShouldBe(0);
            return path;
        }

        public string MemoryEvents => Path.Combine(Folder, "memory.events");

        public string KilledForMemory() => $"""printf 'oom_group_kill 0\noom_kill 3\n' > "{MemoryEvents}"; kill -9 $$""";

        public void WriteMemoryEvents() => File.WriteAllText(MemoryEvents, "low 0\noom 2\noom_kill 2\noom_group_kill 0\n");

        public string WorkingFfmpeg() => Script("ffmpeg", """for last; do :; done; head -c 100 /dev/zero > "$last" """);

        public LocalParakeetEngine Engine(string worker, string? ffmpeg = null, int stallMinutes = 10)
        {
            var settings = Options.Create(new TranscriptionOptions
            {
                ModelRoot = Folder,
                FfmpegPath = ffmpeg ?? WorkingFfmpeg(),
                WorkerPath = worker,
                StallMinutes = stallMinutes,
                MemoryEventsPath = MemoryEvents,
            });
            return new LocalParakeetEngine(new ModelStore(settings), new WorkAudio(settings), settings);
        }

        public TranscriptionInput Input(long start = 0) => new(Path.Combine(Folder, "audio.webm"), Path.Combine(Folder, "work.wav"), start);
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
    [Property("Rule", "REC-26")]
    public async Task the_word_times_of_the_worker_come_through_as_sample_spans()
    {
        // Arrange
        using var rig = new Rig();
        var engine = rig.Engine(rig.Script("worker",
            """echo '{"endSample":16000,"totalSamples":16000,"text":"heard words","wordTimes":[{"start":640,"end":4480},{"start":5120,"end":9600}]}'"""));

        // Act
        var chunks = await CollectAsync(engine, rig.Input());

        // Assert
        chunks.ShouldHaveSingleItem().Text.ShouldBe("heard words");
        chunks[0].WordTimes.ShouldBe([new WordTime(640, 4_480), new WordTime(5_120, 9_600)]);
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
    [Property("Rule", "REC-15")]
    public async Task a_worker_killed_for_memory_is_out_of_memory()
    {
        // Arrange
        using var rig = new Rig();
        rig.WriteMemoryEvents();
        var engine = rig.Engine(rig.Script("worker", rig.KilledForMemory()));

        // Act
        var problem = await ProblemOfAsync(engine, rig.Input());

        // Assert
        problem.ShouldBe(RecordingProblem.OutOfMemory);
    }

    [Test]
    [Property("Rule", "REC-15")]
    public async Task ffmpeg_killed_for_memory_is_out_of_memory()
    {
        // Arrange
        using var rig = new Rig();
        rig.WriteMemoryEvents();
        var engine = rig.Engine(rig.Script("worker", ChunkLine), rig.Script("ffmpeg", rig.KilledForMemory()));

        // Act
        var problem = await ProblemOfAsync(engine, rig.Input());

        // Assert
        problem.ShouldBe(RecordingProblem.OutOfMemory);
    }

    [Test]
    [Property("Rule", "REC-15")]
    public async Task a_kill_with_no_new_memory_kill_claims_no_cause()
    {
        // Arrange
        using var workerRig = new Rig();
        using var ffmpegRig = new Rig();
        workerRig.WriteMemoryEvents();
        ffmpegRig.WriteMemoryEvents();
        var worker = workerRig.Engine(workerRig.Script("worker", "kill -9 $$"));
        var ffmpeg = ffmpegRig.Engine(ffmpegRig.Script("worker", ChunkLine), ffmpegRig.Script("ffmpeg", "kill -9 $$"));

        // Act
        var ofWorker = await ProblemOfAsync(worker, workerRig.Input());
        var ofFfmpeg = await ProblemOfAsync(ffmpeg, ffmpegRig.Input());

        // Assert
        ofWorker.ShouldBe(RecordingProblem.EngineKilled);
        ofFfmpeg.ShouldBe(RecordingProblem.EngineKilled);
    }

    [Test]
    [Property("Rule", "REC-15")]
    public async Task a_kill_on_a_server_that_cannot_read_the_memory_count_claims_no_cause()
    {
        // Arrange
        using var rig = new Rig();
        var engine = rig.Engine(rig.Script("worker", "kill -9 $$"));

        // Act
        var problem = await ProblemOfAsync(engine, rig.Input());

        // Assert
        problem.ShouldBe(RecordingProblem.EngineKilled);
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
        var engine = rig.Engine(rig.Script("worker", ChunkLine), rig.Script("ffmpeg", "echo 'Error opening input files: Invalid data found when processing input' >&2; exit 183"));

        // Act
        var problem = await ProblemOfAsync(engine, rig.Input());

        // Assert
        problem.ShouldBe(RecordingProblem.AudioUnreadable);
        File.Exists(Path.Combine(rig.Folder, "work.wav")).ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "REC-15")]
    public async Task a_full_disk_during_the_decode_is_a_full_disk_and_a_later_try_works()
    {
        // Arrange
        using var rig = new Rig();
        var full = Path.Combine(rig.Folder, "full");
        await File.WriteAllTextAsync(full, "");
        var ffmpeg = rig.Script("ffmpeg", $"""
            if [ -e "{full}" ]; then
              echo '[out#0/wav @ 0x79e3af0] Error writing trailer: No space left on device' >&2
              exit 228
            fi
            for last; do :; done; head -c 100 /dev/zero > "$last"
            """);
        var engine = rig.Engine(rig.Script("worker", ChunkLine), ffmpeg);

        // Act
        var problem = await ProblemOfAsync(engine, rig.Input());
        var workFileLeft = File.Exists(Path.Combine(rig.Folder, "work.wav.part"));
        File.Delete(full);
        var chunks = await CollectAsync(engine, rig.Input());

        // Assert
        problem.ShouldBe(RecordingProblem.DiskFull);
        workFileLeft.ShouldBeFalse();
        chunks.Count.ShouldBe(1);
    }

    [Test]
    [Property("Rule", "REC-15")]
    public async Task a_disk_quota_during_the_decode_is_a_full_disk()
    {
        // Arrange
        using var rig = new Rig();
        var ffmpeg = rig.Script("ffmpeg", "echo 'Error closing file: Disk quota exceeded' >&2; exit 1");
        var engine = rig.Engine(rig.Script("worker", ChunkLine), ffmpeg);

        // Act
        var problem = await ProblemOfAsync(engine, rig.Input());

        // Assert
        problem.ShouldBe(RecordingProblem.DiskFull);
    }

    [Test]
    [Property("Rule", "REC-15")]
    public async Task a_full_disk_stays_in_view_past_a_long_error_stream()
    {
        // Arrange
        using var rig = new Rig();
        var ffmpeg = rig.Script("ffmpeg", """
            i=0; while [ $i -lt 2000 ]; do echo "a warning about a packet of the audio number $i" >&2; i=$((i+1)); done
            echo 'Error closing file: No space left on device' >&2
            exit 1
            """);
        var engine = rig.Engine(rig.Script("worker", ChunkLine), ffmpeg);

        // Act
        var problem = await ProblemOfAsync(engine, rig.Input());

        // Assert
        problem.ShouldBe(RecordingProblem.DiskFull);
    }

    [Test]
    [Property("Rule", "REC-8")]
    public async Task the_error_stream_of_ffmpeg_stays_out_of_the_fault()
    {
        // Arrange
        using var rig = new Rig();
        var ffmpeg = rig.Script("ffmpeg", "echo 'secret-title: No space left on device' >&2; exit 1");
        var engine = rig.Engine(rig.Script("worker", ChunkLine), ffmpeg);

        // Act
        var fault = await Should.ThrowAsync<TranscriptionEngineException>(() => CollectAsync(engine, rig.Input()));

        // Assert
        fault.ToString().ShouldNotContain("secret-title");
    }

    [Test]
    [Property("Rule", "REC-15")]
    [Arguments("Error opening output /work/work.wav.part: Read-only file system", 226)]
    [Arguments("Error opening output /work/work.wav.part: Permission denied", 243)]
    public async Task a_disk_that_refuses_the_decode_says_so_and_not_that_the_audio_is_bad(string words, int exitCode)
    {
        // Arrange
        using var rig = new Rig();
        var engine = rig.Engine(rig.Script("worker", ChunkLine), rig.Script("ffmpeg", $"echo '{words}' >&2; exit {exitCode}"));

        // Act
        var problem = await ProblemOfAsync(engine, rig.Input());

        // Assert
        problem.ShouldBe(RecordingProblem.DiskRefused);
    }

    [Test]
    [Property("Rule", "REC-15")]
    [Arguments("No space left on device", RecordingProblem.DiskFull)]
    [Arguments("Read-only file system", RecordingProblem.DiskRefused)]
    public async Task words_of_the_disk_after_words_of_bad_audio_name_the_disk(string diskWords, RecordingProblem problem)
    {
        // Arrange
        using var rig = new Rig();
        var words = $"Error opening input files: Invalid data found when processing input|Error opening output: {diskWords}".Replace('|', '\n');
        var engine = rig.Engine(rig.Script("worker", ChunkLine), rig.Script("ffmpeg", $"printf '{words}\n' >&2; exit 1"));

        // Act
        var actual = await ProblemOfAsync(engine, rig.Input());

        // Assert
        actual.ShouldBe(problem);
    }

    [Test]
    [Property("Rule", "REC-15")]
    public async Task a_disk_that_also_refuses_the_delete_of_the_work_file_still_says_that_it_refused()
    {
        // Arrange
        using var rig = new Rig();
        var worker = rig.Script("worker", ChunkLine);
        var ffmpeg = rig.Script("ffmpeg", """for last; do :; done; : > "$last"; chmod 555 "$(dirname "$last")"; echo 'Permission denied' >&2; exit 243""");
        var engine = rig.Engine(worker, ffmpeg);
        var writable = File.GetUnixFileMode(rig.Folder);

        try
        {
            // Act
            var problem = await ProblemOfAsync(engine, rig.Input());

            // Assert
            problem.ShouldBe(RecordingProblem.DiskRefused);
        }
        finally
        {
            File.SetUnixFileMode(rig.Folder, writable);
        }
    }

    [Test]
    [Property("Rule", "REC-15")]
    [Arguments("kill -TERM $$")]
    [Arguments("kill -SEGV $$")]
    [Arguments("kill -ABRT $$")]
    [Arguments("exit 255")]
    [Arguments("echo 'A wording that no run has seen' >&2; exit 1")]
    public async Task ffmpeg_that_stops_with_no_known_words_claims_nothing_about_the_audio(string script)
    {
        // Arrange
        using var rig = new Rig();
        var engine = rig.Engine(rig.Script("worker", ChunkLine), rig.Script("ffmpeg", script));

        // Act
        var problem = await ProblemOfAsync(engine, rig.Input());

        // Assert
        problem.ShouldBe(RecordingProblem.EngineCrashed);
    }

    [Test]
    [Property("Rule", "REC-15")]
    [Arguments("Error opening input files: Invalid data found when processing input", 183)]
    [Arguments("Error opening input files: End of file", 187)]
    [Arguments("[aost#0:0 @ 0x1] Decoding requested, but no decoder found for: flac", 234)]
    [Arguments("[out#0/wav @ 0x1] Output file does not contain any stream", 234)]
    [Arguments("Error opening output files: Not yet implemented in FFmpeg, patches welcome", 176)]
    [Arguments("[aist#0:0 @ 0x1] Error while opening decoder: Function not implemented", 218)]
    [Arguments("[af#0:0 @ 0x1] Task finished with error code: -22 (Invalid argument)", 234)]
    public async Task each_wording_that_ffmpeg_uses_for_bad_audio_is_unreadable(string words, int exitCode)
    {
        // Arrange
        using var rig = new Rig();
        var engine = rig.Engine(rig.Script("worker", ChunkLine), rig.Script("ffmpeg", $"echo '{words}' >&2; exit {exitCode}"));

        // Act
        var problem = await ProblemOfAsync(engine, rig.Input());

        // Assert
        problem.ShouldBe(RecordingProblem.AudioUnreadable);
    }

    [Test]
    [Property("Rule", "REC-15")]
    public async Task a_worker_killed_in_the_middle_of_a_line_is_killed_and_not_a_crash()
    {
        // Arrange
        using var rig = new Rig();
        var engine = rig.Engine(rig.Script("worker", """printf '{"endSample":16'; kill -9 $$"""));

        // Act
        var problem = await ProblemOfAsync(engine, rig.Input());

        // Assert
        problem.ShouldBe(RecordingProblem.EngineKilled);
    }

    [Test]
    [Property("Rule", "REC-15")]
    public async Task a_worker_killed_for_memory_in_the_middle_of_a_line_is_out_of_memory()
    {
        // Arrange
        using var rig = new Rig();
        rig.WriteMemoryEvents();
        var engine = rig.Engine(rig.Script("worker", $$"""printf '{"endSample":16'; {{rig.KilledForMemory()}}"""));

        // Act
        var problem = await ProblemOfAsync(engine, rig.Input());

        // Assert
        problem.ShouldBe(RecordingProblem.OutOfMemory);
    }

    [Test]
    [Property("Rule", "REC-15")]
    public async Task a_broken_line_with_more_after_it_is_a_crash_with_no_wait_for_the_worker()
    {
        // Arrange
        using var rig = new Rig();
        var engine = rig.Engine(rig.Script("worker", $"echo not-json; {ChunkLine}; sleep 60"));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // Act
        var problem = await ProblemOfAsync(engine, rig.Input());

        // Assert
        problem.ShouldBe(RecordingProblem.EngineCrashed);
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
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
    [Arguments("worker")]
    [Arguments("worker.dll")]
    public async Task a_machine_with_no_worker_says_that_the_engine_is_not_installed(string name)
    {
        // Arrange
        using var rig = new Rig();
        var engine = rig.Engine(Path.Combine(rig.Folder, "no-such-folder", name));

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
