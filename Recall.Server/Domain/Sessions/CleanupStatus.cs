namespace Recall.Server.Domain.Sessions;

public enum CleanupStatus
{
    NotRun,
    Running,
    Clean,
    Stale,
    Failed,
}
