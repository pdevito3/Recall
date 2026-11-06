namespace Recall.UnitTests.Resources.Jobs;

using System.Diagnostics;
using BackWave.Jobs;
using Recall.Server.Resources;
using Recall.Server.Resources.Jobs;
using Shouldly;

public class JobFailureFilterTests
{
    private const string Marker = "text of a person";

    private static JobRegistration JobThatRuns(Func<Task> execute) => new()
    {
        WireName = "a-job",
        JobType = typeof(string),
        Queue = "a-queue",
        Deserialize = _ => null!,
        Serialize = _ => [],
        Execute = (_, _, _, _) => execute(),
    };

    private static Task RunAsync(Func<Task> execute, CancellationToken cancellationToken = default)
    {
        var filtered = JobFailureFilter.Applied(new JobModule
        {
            Registrations = [JobThatRuns(execute)],
            Handlers = [],
            ContainingTypes = [],
        });
        return filtered.Registrations[0].Execute(null!, null!, null!, cancellationToken);
    }

    private static Exception Thrown(Func<Exception> make)
    {
        try
        {
            throw make();
        }
        catch (Exception thrown)
        {
            return thrown;
        }
    }

    [Test]
    public async Task an_exception_loses_its_message_and_its_inner_exception_and_keeps_its_type_and_frames()
    {
        // Arrange
        var thrown = Thrown(() => new InvalidOperationException(Marker, new ArgumentException(Marker)));

        // Act
        var seen = await Should.ThrowAsync<JobFailureException>(() => RunAsync(() => Task.FromException(thrown)));

        // Assert
        seen.Message.ShouldBe(typeof(InvalidOperationException).FullName);
        seen.InnerException.ShouldBeNull();
        seen.StackTrace.ShouldBe(thrown.StackTrace);
        seen.StackTrace!.ShouldContain(nameof(Thrown));
        seen.ToString().ShouldNotContain(Marker);
    }

    private static string TextOf(Activity span) => string.Join('\n', new[] { span.StatusDescription ?? "" }
        .Concat(span.Events.SelectMany(spanEvent => spanEvent.Tags.Select(tag => $"{tag.Value}"))));

    [Test]
    public async Task a_child_span_in_a_job_records_the_type_and_no_message()
    {
        // Arrange
        var span = new Activity("a-child-span");
        var thrown = Thrown(() => new InvalidOperationException(Marker));

        // Act
        await RunAsync(() =>
        {
            Telemetry.MarkFailed(span, thrown);
            return Task.CompletedTask;
        });

        // Assert
        TextOf(span).ShouldNotContain(Marker);
        TextOf(span).ShouldContain(typeof(InvalidOperationException).FullName!);
        TextOf(span).ShouldContain(nameof(Thrown));
    }

    [Test]
    public void a_span_outside_a_job_keeps_the_message()
    {
        // Arrange
        var span = new Activity("a-span");

        // Act
        Telemetry.MarkFailed(span, new InvalidOperationException(Marker));

        // Assert
        TextOf(span).ShouldContain(Marker);
    }

    [Test]
    public async Task a_child_span_in_a_job_keeps_a_text_that_is_safe()
    {
        // Arrange
        var span = new Activity("a-child-span");

        // Act
        await RunAsync(() =>
        {
            Telemetry.MarkFailed(span, new JobSafeTextException("A safe text."));
            return Task.CompletedTask;
        });

        // Assert
        TextOf(span).ShouldContain("A safe text.");
    }

    [Test]
    public async Task a_job_that_throws_before_its_first_await_also_loses_its_message()
    {
        // Act
        var seen = await Should.ThrowAsync<JobFailureException>(() => RunAsync(() => throw new InvalidOperationException(Marker)));

        // Assert
        seen.ToString().ShouldNotContain(Marker);
    }

    [Test]
    public async Task a_type_that_states_its_text_is_safe_keeps_its_text()
    {
        // Act
        var seen = await Should.ThrowAsync<JobSafeTextException>(() =>
            RunAsync(() => Task.FromException(new JobSafeTextException("The audio file is missing."))));

        // Assert
        seen.Message.ShouldBe("The audio file is missing.");
    }

    [Test]
    public async Task a_cancel_of_the_job_stays_a_cancel_with_no_text_of_the_job()
    {
        // Arrange
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        // Act
        var seen = await Should.ThrowAsync<OperationCanceledException>(() =>
            RunAsync(() => Task.FromException(new OperationCanceledException(Marker)), stop.Token));

        // Assert
        seen.Message.ShouldNotContain(Marker);
        seen.CancellationToken.ShouldBe(stop.Token);
    }

    [Test]
    public async Task a_cancel_that_is_not_of_the_job_is_a_failure_with_no_text()
    {
        // Act
        var seen = await Should.ThrowAsync<JobFailureException>(() => RunAsync(() => Task.FromException(new TaskCanceledException(Marker))));

        // Assert
        seen.Message.ShouldBe(typeof(TaskCanceledException).FullName);
    }

    [Test]
    public void the_filter_keeps_each_field_of_a_job_and_the_lists_of_the_module()
    {
        // Arrange
        var job = JobThatRuns(() => Task.CompletedTask);
        var module = new JobModule { Registrations = [job], Handlers = [], ContainingTypes = [] };

        // Act
        var filtered = JobFailureFilter.Applied(module);

        // Assert
        filtered.Handlers.ShouldBeSameAs(module.Handlers);
        filtered.ContainingTypes.ShouldBeSameAs(module.ContainingTypes);
        var seen = filtered.Registrations.ShouldHaveSingleItem();
        seen.WireName.ShouldBe(job.WireName);
        seen.Retry.ShouldBe(job.Retry);
        seen.Queue.ShouldBe(job.Queue);
        seen.JobType.ShouldBe(job.JobType);
        seen.Serialize.ShouldBeSameAs(job.Serialize);
        seen.Deserialize.ShouldBeSameAs(job.Deserialize);
    }
}
