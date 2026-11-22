namespace Recall.FunctionalTests.Auth;

using Serilog;
using Shouldly;

public class HostBootTests
{
    [Test]
    public async Task a_host_that_boots_after_the_first_test_is_refused_and_the_static_logger_stays()
    {
        // Arrange
        var logger = Log.Logger;
        await using var late = new AuthFlowFactory(new Dictionary<string, string?>()) { NoProvider = true };

        // Act
        var refusal = Should.Throw<InvalidOperationException>(() => late.Services);

        // Assert
        refusal.Message.ShouldContain(nameof(AuthHosts.Start));
        Log.Logger.ShouldBeSameAs(logger);
    }
}
