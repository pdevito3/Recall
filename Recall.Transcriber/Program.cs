using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Recall.Transcriber;
using SherpaOnnx;

const int SamplesPerSecond = 16_000;
const int BytesPerSample = 2;
const int MaxChunkSeconds = 20;
const int FeatureDim = 80;
const int ExitBadArguments = 2;
const int ExitParentGone = 3;
const int LowPriority = 10;

if (args.Length != 4)
    return ExitBadArguments;

var (wavPath, modelFolder, threads, startSample) =
    (args[0], args[1], int.Parse(args[2], CultureInfo.InvariantCulture), long.Parse(args[3], CultureInfo.InvariantCulture));

MakeThisTheFirstToDie();
ExitWhenTheParentIsGone();

var config = new OfflineRecognizerConfig();
config.FeatConfig.SampleRate = SamplesPerSecond;
config.FeatConfig.FeatureDim = FeatureDim;
config.ModelConfig.Transducer.Encoder = Path.Combine(modelFolder, "encoder.int8.onnx");
config.ModelConfig.Transducer.Decoder = Path.Combine(modelFolder, "decoder.int8.onnx");
config.ModelConfig.Transducer.Joiner = Path.Combine(modelFolder, "joiner.int8.onnx");
config.ModelConfig.Tokens = Path.Combine(modelFolder, "tokens.txt");
config.ModelConfig.NumThreads = threads;
config.ModelConfig.ModelType = "nemo_transducer";
config.ModelConfig.Provider = "cpu";
config.DecodingMethod = "greedy_search";

using var recognizer = new OfflineRecognizer(config);
using var wav = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.Read);
var (dataStart, totalSamples) = FindSamples(wav);

var maxSamples = MaxChunkSeconds * SamplesPerSecond;
var windowSamples = maxSamples + 1;
var bytes = new byte[windowSamples * BytesPerSample];
var samples = new float[windowSamples];
using var output = Console.OpenStandardOutput();
using var lines = new StreamWriter(output) { AutoFlush = true };

var position = startSample;
while (position < totalSamples)
{
    wav.Seek(dataStart + position * BytesPerSample, SeekOrigin.Begin);
    var read = wav.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false) / BytesPerSample;
    for (var i = 0; i < read; i++)
        samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * BytesPerSample)) / 32768f;

    if (read == 0)
        break;

    var length = ChunkCutter.CutLength(samples.AsSpan(0, read), maxSamples);
    var text = Recognize(recognizer, samples.AsSpan(0, length).ToArray());
    position += length;
    lines.WriteLine(new JsonObject
    {
        ["endSample"] = position,
        ["totalSamples"] = totalSamples,
        ["text"] = text,
    }.ToJsonString());
}

return 0;

static string Recognize(OfflineRecognizer recognizer, float[] chunk)
{
    using var stream = recognizer.CreateStream();
    stream.AcceptWaveform(SamplesPerSecond, chunk);
    recognizer.Decode(stream);
    return stream.Result.Text.Trim();
}

static (long DataStart, long TotalSamples) FindSamples(FileStream wav)
{
    Span<byte> header = stackalloc byte[12];
    wav.ReadExactly(header);
    Span<byte> chunkHeader = stackalloc byte[8];
    while (true)
    {
        wav.ReadExactly(chunkHeader);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
        if (chunkHeader[..4].SequenceEqual("data"u8))
            return (wav.Position, Math.Min(size, wav.Length - wav.Position) / BytesPerSample);

        wav.Seek(size + size % 2, SeekOrigin.Current);
    }
}

// The kernel then kills this process first when memory runs out, and the server stays up.
static void MakeThisTheFirstToDie()
{
    try
    {
        File.WriteAllText("/proc/self/oom_score_adj", "1000");
        _ = SetPriority(0, 0, LowPriority);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
    {
        // Only Linux has this file, so a worker on a laptop runs with its normal rank.
    }
}

// A server that dies closes this pipe, so a worker never runs on with no one to read its text.
static void ExitWhenTheParentIsGone()
{
    var watch = new Thread(() =>
    {
        using var input = Console.OpenStandardInput();
        var buffer = new byte[1];
        while (input.Read(buffer, 0, 1) > 0)
        {
        }

        Environment.Exit(ExitParentGone);
    })
    {
        IsBackground = true,
    };
    watch.Start();
}

[DllImport("libc", EntryPoint = "setpriority")]
static extern int SetPriority(int which, int who, int priority);
