namespace Recall.Server.Resources.Jobs;

public sealed class JobOptions
{
    public const string SectionName = "Jobs";

    /// <summary>How long a committed job can wait for a worker, because a transactional enqueue wakes no worker.</summary>
    public int PollSeconds { get; init; } = 5;

    /// <summary>A test host with no use for a worker runs none. It works only in Development and in the test environments.</summary>
    public bool RunWorkers { get; init; } = true;

    public HealthJobOptions HealthJob { get; set; } = new();

    /// <summary>
    /// Kills the process between the commit of a tenant delete and the delete of its folder, so a test can prove the
    /// sweep at the next start. It works only in Development and in the test environments.
    /// </summary>
    public bool KillBeforeTenantFolderDelete { get; set; }
}

/// <summary>
/// Settings that let a check of the host stop a health job in its run, or make it fail. They work only in Development
/// and in the test environments. <see cref="JobHosting"/> clears them in each other environment.
/// </summary>
public sealed class HealthJobOptions
{
    public int HoldSeconds { get; init; }

    public bool FailOnPurpose { get; init; }
}
