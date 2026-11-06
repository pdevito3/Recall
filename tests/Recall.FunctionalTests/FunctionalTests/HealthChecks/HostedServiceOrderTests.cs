namespace Recall.FunctionalTests.FunctionalTests.HealthChecks;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Recall.Server.Databases;
using Recall.Server.Domain.Recordings.Jobs;
using Recall.Server.Resources.Extensions;
using Recall.Server.Resources.Jobs;
using Recall.Server.Resources.Storage;
using Shouldly;

public class HostedServiceOrderTests : TestBase
{
    [Test]
    public void the_refusal_of_the_data_directory_comes_before_the_refusal_of_the_file_store()
    {
        // Arrange
        var hostedServices = TestingWebApplicationFactory.Instance.Services.GetServices<IHostedService>()
            .Select(s => s.GetType())
            .ToList();

        // Act
        var migrations = hostedServices.FindIndex(t => t == typeof(MigrationHostedService<AppDbContext>));
        var fileStore = hostedServices.FindIndex(t => t.DeclaringType == typeof(FileStoreRegistration));

        // Assert
        migrations.ShouldBeGreaterThanOrEqualTo(0);
        fileStore.ShouldBeGreaterThan(migrations);
    }

    [Test]
    public void no_worker_starts_before_the_file_store_is_prepared()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Configuration["FileStore:Root"] = "/data/files";

        // Act
        builder.ConfigureServices();
        var hosted = builder.Services.Where(service => service.ServiceType == typeof(IHostedService)).ToList();
        var fileStore = hosted.FindIndex(service => service.ImplementationType?.DeclaringType == typeof(FileStoreRegistration));
        var worker = hosted.FindIndex(RunsAWorker);

        // Assert
        fileStore.ShouldBeGreaterThanOrEqualTo(0);
        worker.ShouldBeGreaterThan(fileStore);
    }

    private static bool RunsAWorker(ServiceDescriptor service)
    {
        var type = service.ImplementationType ?? service.ImplementationFactory?.Method.DeclaringType;
        return type?.Assembly.GetName().Name?.StartsWith("BackWave", StringComparison.Ordinal) == true
            || type == typeof(JobHostWatch)
            || type == typeof(RecordingSweepService);
    }
}
