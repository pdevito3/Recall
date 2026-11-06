namespace Recall.Server.Resources.Jobs;

using BackWave.Diagnostics;
using BackWave.Jobs;

/// <summary>
/// Throw it from a job when its text is safe for each person who can read a job row, a span, or a support bundle.
/// It takes no inner exception, because BackWave writes the text of an inner exception to the span.
/// </summary>
public class JobSafeTextException(string safeText) : Exception(safeText);

/// <summary>
/// What BackWave sees in place of a thrown exception: the type and the stack frames, with no message. BackWave has no
/// other seam, and it copies a message to the job row, the job history, the span, and the dashboard.
/// </summary>
// The type is the only text of the failure that a person can read, so it is a code name on purpose.
#pragma warning disable FSTMSG001
public sealed class JobFailureException(Exception thrown) : Exception(thrown.GetType().FullName)
#pragma warning restore FSTMSG001
{
    private readonly string? _frames = thrown.StackTrace;

    public override string? StackTrace => _frames;
}

/// <summary>No one, an admin included, reads the journal of another person, so each job runs inside this filter.</summary>
public static class JobFailureFilter
{
    public static JobModule Applied(JobModule module) => new()
    {
        Registrations = [.. module.Registrations.Select(Filtered)],
        Handlers = module.Handlers,
        ContainingTypes = module.ContainingTypes,
        SeedCodecs = module.SeedCodecs,
    };

    private static JobRegistration Filtered(JobRegistration job) => new()
    {
        WireName = job.WireName,
        JobType = job.JobType,
        Queue = job.Queue,
        DefaultTags = job.DefaultTags,
        Deserialize = job.Deserialize,
        Serialize = job.Serialize,
        OutputTypeInfo = job.OutputTypeInfo,
        Retry = job.Retry,
        Execute = (services, payload, context, cancellationToken) =>
            RunAsync(job, services, payload, context, cancellationToken),
    };

    private static async Task RunAsync(JobRegistration job, IServiceProvider services, object payload, JobContext context, CancellationToken cancellationToken)
    {
        try
        {
            await job.Execute(services, payload, context, cancellationToken);
        }
        catch (Exception thrown) when (thrown is not (JobSafeTextException or InvariantViolationException))
        {
            if (thrown is OperationCanceledException && cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);

            throw new JobFailureException(thrown);
        }
    }
}
