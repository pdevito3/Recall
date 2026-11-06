namespace Recall.FunctionalTests.FunctionalTests.HealthChecks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Recall.Server.Databases;
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
}
