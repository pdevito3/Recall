namespace Recall.UnitTests.Architecture;

using System.Reflection;
using BackWave.Jobs;
using Recall.Server.Resources;
using Shouldly;

/// <summary>
/// BackWave stores a payload as plain text. A job gets the IDs of its rows and reads the text from
/// the database, so a job row holds no journal text.
/// </summary>
public class JobPayloadTests
{
    [Test]
    public void each_job_of_the_server_takes_only_ids()
    {
        // Act
        var violations = Violations(typeof(PagedList).Assembly);

        // Assert
        violations.ShouldBeEmpty("A job payload holds IDs only. See docs/backend/background-jobs.md.");
    }

    [Test]
    public void the_rule_refuses_text_in_a_method_and_in_a_record()
    {
        // Act
        var violations = Violations(typeof(JobPayloadTests).Assembly);

        // Assert
        violations.ShouldBe(
        [
            "SampleJobs.SampleAsync(text)",
            "SampleJobs.SampleAsync(said)",
            "SampleRecord.Transcript",
        ], ignoreOrder: true);
    }

    private static List<string> Violations(Assembly assembly)
    {
        var violations = new List<string>();
        foreach (var type in assembly.GetTypes())
        {
            if (type.GetCustomAttribute<JobAttribute>() is not null)
            {
                violations.AddRange(type.GetProperties()
                    .Where(property => !IsIdOnly(property.PropertyType, property.Name))
                    .Select(property => $"{type.Name}.{property.Name}"));
            }

            var jobMethods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttribute<JobAttribute>() is not null);
            foreach (var method in jobMethods)
            {
                violations.AddRange(method.GetParameters()
                    .Where(parameter => !IsIdOnly(parameter.ParameterType, parameter.Name!))
                    .Select(parameter => $"{type.Name}.{method.Name}({parameter.Name})"));
            }
        }

        return violations;
    }

    private static bool IsIdOnly(Type type, string name)
    {
        if (type == typeof(CancellationToken) || type == typeof(JobContext) || type == typeof(Guid) || type == typeof(int) || type == typeof(long)
            || type == typeof(bool) || type.IsEnum)
            return true;

        return type == typeof(string) && (name == "id" || name.EndsWith("Id", StringComparison.Ordinal));
    }

    public sealed class SampleJobs
    {
        [Job("sample-method", Queue = "sample")]
        public static Task SampleAsync(string entryId, string text, string said, Guid attemptId, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    [Job("sample-record", Queue = "sample")]
    public sealed record SampleRecord(string EntryId, string Transcript);

    public sealed class SampleRecordHandler : IJobHandler<SampleRecord>
    {
        public Task HandleAsync(SampleRecord job, JobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
