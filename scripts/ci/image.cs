#!/usr/bin/env dotnet

#:include job.cs

using System.Globalization;
using System.Net;
using System.Net.Sockets;

const long ImageBudgetBytes = 450_000_000;
const double IdleMemoryBudgetMib = 200;
const string Project = "recall-ci";
const string Tag = "ci";
const string FailedStart = "recall-ci-failed-start";
const string PersistMarker = "/data/persist-marker";
// The file store proves a write at most once in 30 s, so a change of the store shows after that time.
const int SecondsForAProbeToChange = 45;

var build = args switch
{
    [] => true,
    ["--no-build"] => false,
    _ => throw new ArgumentException("Usage: dotnet run scripts/ci/image.cs -- [--no-build]"),
};

// The .env and the shell of a person can name a real data folder, and this job writes to its folder.
Environment.SetEnvironmentVariable("COMPOSE_DISABLE_ENV_FILE", "true");
foreach (var setting in new[] { "COMPOSE_FILE", "COMPOSE_ENV_FILES", "RECALL_DATA", "PUID", "PGID" })
    Environment.SetEnvironmentVariable(setting, null);

var port = FreePort();
Environment.SetEnvironmentVariable("COMPOSE_PROJECT_NAME", Project);
Environment.SetEnvironmentVariable("RECALL_TAG", Tag);
Environment.SetEnvironmentVariable("RECALL_PORT", port.ToString(CultureInfo.InvariantCulture));
var site = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
var bindFolder = Directory.CreateTempSubdirectory("recall-ci-bind-").FullName;

Step[] buildSteps = build
    ?
    [
        new("Build the image with compose.yaml", copy => copy.Run("docker", "compose", "build", "recall")),
        new("Fail over the image size budget", CheckImageSize),
    ]
    : [];

int result;
try
{
    result = Job.Run("image",
    [
        .. buildSteps,
        new("Start one container and one volume", StartOnNamedVolume),
        new("Serve the first page, and offer setup with the code from the log", CheckFirstPage),
        new("Run as the user of the image, with dotnet as PID 1", CheckUserAndPid1),
        new("Ship no file that a published server does not read", CheckNoUnreadFiles),
        new("Keep the data in /data across a stop and a new container", CheckDataStays),
        new("Fail the probe when the file store cannot write", CheckProbeReadsBody),
        new("Exit with a nonzero code as PID 1 after a failed start", CheckFailedStartExits),
        new("Fail over the idle memory budget", CheckIdleMemory),
        OperatingSystem.IsMacOS()
            ? new("Refuse a folder of the Mac", CheckMacFolderRefused)
            : new("Write to a bind mount as another user", CheckBindMount),
    ]);
}
finally
{
    Docker("rm", "--force", FailedStart);
    Docker("compose", "--project-name", Project, "down", "--volumes", "--remove-orphans");
    if (build)
        Docker("image", "rm", "--force", $"ghcr.io/pdevito3/recall:{Tag}");
    Directory.Delete(bindFolder, recursive: true);
}

return result;

void CheckImageSize(JobCopy copy)
{
    var size = long.Parse(copy.Output("docker", "image", "inspect", "--format", "{{.Size}}", $"ghcr.io/pdevito3/recall:{Tag}").Trim(), CultureInfo.InvariantCulture);
    Console.WriteLine($"The image is {size / 1_000_000} MB. The budget is {ImageBudgetBytes / 1_000_000} MB.");
    Require(size <= ImageBudgetBytes, $"The image is {size} bytes, over the budget of {ImageBudgetBytes}.");
}

void StartOnNamedVolume(JobCopy copy)
{
    Up(copy);
    var containers = copy.Output("docker", "compose", "ps", "--quiet").Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Require(containers.Length == 1, $"The project has {containers.Length} containers.");
}

void CheckFirstPage(JobCopy copy)
{
    using var page = site.GetAsync("/").GetAwaiter().GetResult();
    Require(page.StatusCode == HttpStatusCode.OK, $"GET / answered {page.StatusCode}.");
    var policy = page.Headers.GetValues("Content-Security-Policy").Single();
    Require(policy.Contains("frame-ancestors 'none'", StringComparison.Ordinal), $"The policy of the first page is {policy}.");

    // With no provider, the first person sets up the install with the code from the log (PWD-2).
    using var setup = site.GetAsync("/auth/password/setup").GetAwaiter().GetResult();
    var setupPage = setup.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    Require(setup.StatusCode == HttpStatusCode.OK && setupPage.Contains("Set up Recall", StringComparison.Ordinal), $"GET /auth/password/setup answered {setup.StatusCode} with no setup form.");
    var logs = copy.Output("docker", "compose", "logs", "recall");
    Require(logs.Contains("give the setup code", StringComparison.Ordinal), "The log does not hold the setup code.");
}

void CheckUserAndPid1(JobCopy copy)
{
    var user = InContainer(copy, "id", "-u").Trim();
    Require(user == "1654", $"The user of the container is {user}.");
    var pid1 = InContainer(copy, "cat", "/proc/1/cmdline");
    Require(pid1.StartsWith("dotnet\0", StringComparison.Ordinal), $"PID 1 is {pid1}.");
}

void CheckNoUnreadFiles(JobCopy copy)
{
    var unread = InContainer(copy, "sh", "-c", "cd /app && ls -d appsettings.Development.json openapi web.config Recall.Server Recall.Server.staticwebassets.endpoints.json *.pdb *.xml 2>/dev/null || true").Trim();
    Require(unread.Length == 0, $"The image ships these files: {unread}");
}

void CheckDataStays(JobCopy copy)
{
    foreach (var path in new[] { "/data/recall.db", "/data/files", "/data/keys" })
        InContainer(copy, "test", "-e", path);

    InContainer(copy, "touch", PersistMarker);
    var before = DataIdentity(copy);

    var container = copy.Output("docker", "compose", "ps", "--quiet").Trim();
    copy.Run("docker", "compose", "stop");
    var exitCode = copy.Output("docker", "inspect", "--format", "{{.State.ExitCode}}", container).Trim();
    Require(exitCode == "0", $"A stop gave the exit code {exitCode}.");
    copy.Run("docker", "compose", "start");
    Up(copy);
    Require(DataIdentity(copy) == before, "The data of /data changed across a stop and a start.");

    copy.Run("docker", "compose", "up", "--detach", "--wait", "--wait-timeout", "180", "--no-build", "--force-recreate");
    var recreated = copy.Output("docker", "compose", "ps", "--quiet").Trim();
    Require(recreated != container, "The container did not change.");
    Require(DataIdentity(copy) == before, "The data of /data changed in a new container.");
}

string DataIdentity(JobCopy copy) => InContainer(copy, "sh", "-c", $"stat -c '%i %W' /data/recall.db && ls /data/keys && ls {PersistMarker}");

void CheckProbeReadsBody(JobCopy copy)
{
    InContainer(copy, "/app/healthcheck.sh", "8080", "0");

    InContainer(copy, "chmod", "-R", "a-w", "/data/files");
    var degraded = WaitForProbe(copy, wantedExit: 1);
    Require(degraded.Contains("Degraded", StringComparison.Ordinal), $"The probe printed {degraded}.");

    using var answer = site.GetAsync("/health").GetAwaiter().GetResult();
    Require(answer.StatusCode == HttpStatusCode.OK, $"A Degraded /health answered {answer.StatusCode}, so this step does not test the body.");

    InContainer(copy, "chmod", "-R", "u+w", "/data/files");
    WaitForProbe(copy, wantedExit: 0);
}

string WaitForProbe(JobCopy copy, int wantedExit)
{
    for (var attempt = 0; attempt < SecondsForAProbeToChange; attempt++)
    {
        var (exitCode, output, _) = copy.Capture("docker", "compose", "exec", "-T", "recall", "/app/healthcheck.sh", "8080", "0");
        if (exitCode == wantedExit)
            return output;
        Thread.Sleep(TimeSpan.FromSeconds(1));
    }

    throw new StepFailure($"The probe did not exit with {wantedExit} in {SecondsForAProbeToChange} s.");
}

void CheckFailedStartExits(JobCopy copy)
{
    copy.Run("docker", "compose", "run", "--detach", "--no-deps", "--name", FailedStart, "--env", "Auth__Providers__0__Issuer=http://provider.invalid", "recall");
    for (var attempt = 0; attempt < 60; attempt++)
    {
        var state = copy.Output("docker", "inspect", "--format", "{{.State.Status}} {{.State.ExitCode}}", FailedStart).Trim();
        if (state.StartsWith("exited", StringComparison.Ordinal))
        {
            Require(state != "exited 0", "A failed start gave the exit code 0.");
            Console.WriteLine($"A failed start gave the state {state}.");
            return;
        }

        Thread.Sleep(TimeSpan.FromSeconds(1));
    }

    throw new StepFailure("A failed start did not exit in 60 s, so PID 1 hangs.");
}

void CheckIdleMemory(JobCopy copy)
{
    var container = copy.Output("docker", "compose", "ps", "--quiet").Trim();
    Thread.Sleep(TimeSpan.FromSeconds(60));
    for (var sample = 1; sample <= 3; sample++)
    {
        var usage = copy.Output("docker", "stats", "--no-stream", "--format", "{{.MemUsage}}", container).Split('/')[0].Trim();
        var mib = usage.EndsWith("GiB", StringComparison.Ordinal)
            ? double.Parse(usage[..^3], CultureInfo.InvariantCulture) * 1024
            : double.Parse(usage[..^3], CultureInfo.InvariantCulture);
        Console.WriteLine($"Idle memory, sample {sample}: {mib:F1} MiB. The budget is {IdleMemoryBudgetMib} MiB.");
        Require(mib <= IdleMemoryBudgetMib, $"The idle memory is {mib:F1} MiB, over the budget of {IdleMemoryBudgetMib} MiB.");
        Thread.Sleep(TimeSpan.FromSeconds(10));
    }
}

void CheckBindMount(JobCopy copy)
{
    copy.Run("docker", "compose", "down");
    Environment.SetEnvironmentVariable("RECALL_DATA", bindFolder);
    Environment.SetEnvironmentVariable("PUID", copy.Output("id", "-u").Trim());
    Environment.SetEnvironmentVariable("PGID", copy.Output("id", "-g").Trim());
    Up(copy);
    Require(File.Exists(Path.Combine(bindFolder, "recall.db")), $"The folder {bindFolder} has no recall.db.");
    var user = InContainer(copy, "id", "-u").Trim();
    Require(user == Environment.GetEnvironmentVariable("PUID"), $"The user of the container is {user}.");
}

void CheckMacFolderRefused(JobCopy copy)
{
    copy.Run("docker", "compose", "down");
    Environment.SetEnvironmentVariable("RECALL_DATA", bindFolder);
    var (exitCode, output, errors) = copy.Capture("docker", "compose", "run", "--rm", "--no-deps", "recall");
    Require(exitCode != 0, "The Server started on a folder of the Mac.");
    Require($"{output}{errors}".Contains("fakeowner", StringComparison.Ordinal), $"The Server did not name the mount. Its output: {output}{errors}");
}

static void Up(JobCopy copy) => copy.Run("docker", "compose", "up", "--detach", "--wait", "--wait-timeout", "180", "--no-build");

static string InContainer(JobCopy copy, params string[] command) =>
    copy.Output("docker", ["compose", "exec", "-T", "recall", .. command]);

static void Require(bool condition, string failure)
{
    if (!condition)
        throw new StepFailure(failure);
}

static void Docker(params string[] arguments)
{
    using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("docker", arguments) { RedirectStandardOutput = true, RedirectStandardError = true })!;
    process.WaitForExit();
}

static int FreePort()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
}
