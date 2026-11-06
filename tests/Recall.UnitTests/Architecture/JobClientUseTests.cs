namespace Recall.UnitTests.Architecture;

using System.Reflection;
using BackWave;
using Recall.Server.Resources;
using Recall.Server.Resources.Jobs;
using Shouldly;

/// <summary>
/// A BackWave client has a connection of its own. Inside a save, that connection waits for the write lock of the save.
/// A feature enqueues through <c>AppDbContext.SaveAndEnqueueAsync</c>, so only the job host takes a client.
/// </summary>
public class JobClientUseTests
{
    private const BindingFlags AnyInstanceField = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Test]
    public void only_the_job_host_holds_a_backwave_client()
    {
        // Act
        var holders = typeof(PagedList).Assembly.GetTypes()
            .Where(type => type.Namespace != typeof(JobEnqueuer).Namespace)
            .Where(HoldsClient)
            .Select(type => type.FullName)
            .ToList();

        // Assert
        holders.ShouldBeEmpty("Enqueue through AppDbContext.SaveAndEnqueueAsync. See docs/backend/background-jobs.md.");
    }

    [Test]
    public void the_rule_sees_a_held_client()
    {
        // Act
        var holds = HoldsClient(typeof(HolderOfAClient));

        // Assert
        holds.ShouldBeTrue();
    }

    private static bool HoldsClient(Type type) =>
        type.GetFields(AnyInstanceField).Any(field => field.FieldType == typeof(BackWaveClient))
        || type.GetConstructors(AnyInstanceField).Any(constructor =>
            constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(BackWaveClient)));

    private sealed class HolderOfAClient(BackWaveClient client)
    {
        public BackWaveClient Client => client;
    }
}
