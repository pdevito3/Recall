namespace Recall.UnitTests.Databases;

using Recall.ArchitectureFixtures;
using Recall.ArchitectureFixtures.DomainEvents;
using Recall.Server.Databases;
using Recall.Server.Domain;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

public sealed class DomainEventRegistrationTests
{
    [Test]
    public async Task internal_handler_is_registered_for_its_event()
    {
        // Arrange
        await using var provider = new ServiceCollection()
            .AddDomainEvents(typeof(FixtureMarker).Assembly)
            .BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        // Act
        var handlers = scope.ServiceProvider.GetServices<IDomainEventHandler<CleanEventOfInternalHandler>>();

        // Assert
        handlers.Select(handler => handler.GetType().Name).ShouldBe(["CleanInternalHandler"]);
    }
}
