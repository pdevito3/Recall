namespace Recall.Server.Resources.Jobs;

public sealed class JobOptions
{
    public const string SectionName = "Jobs";

    /// <summary>
    /// How long a committed job can wait for a worker, because a transactional enqueue wakes no worker. BackWave reads it
    /// when the host starts. An environment variable sets it, because a test host adds its settings after that time.
    /// </summary>
    public int PollSeconds { get; init; } = 5;

    public HealthJobOptions HealthJob { get; init; } = new();
}

/// <summary>Settings that let a check of the host stop a health job in its run, or make it fail.</summary>
public sealed class HealthJobOptions
{
    public int HoldSeconds { get; init; }

    public bool FailOnPurpose { get; init; }
}
