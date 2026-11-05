namespace Recall.FunctionalTests.FunctionalTests.HealthChecks;

using System.Globalization;
using Microsoft.Extensions.Options;
using Recall.Server.Resources.Storage;
using Recall.SharedTestHelpers.Utilities;
using Serilog.Events;
using Shouldly;

public class FileStoreProofLogTests : TestBase
{
    [Test]
    public async Task a_root_that_stays_broken_logs_one_error_that_names_the_root_and_the_cause_and_a_repair_logs_it()
    {
        // Arrange
        var logs = TestingWebApplicationFactory.Instance.CapturedLogs;
        using var folder = new TemporaryFileRoot();
        Directory.CreateDirectory(folder.Path);
        var blocker = Path.Combine(folder.Path, "a-file");
        await File.WriteAllBytesAsync(blocker, [1]);
        var root = Path.Combine(blocker, "files");
        var store = new FileStore(Options.Create(new FileStoreOptions { Root = root }));

        // Act
        var cause = store.Prepare().NotWritableCause.ShouldNotBeNull();
        store.ProveWritable().ShouldNotBeNull();
        store.ProveWritable().ShouldNotBeNull();
        File.Delete(blocker);
        store.ProveWritable().ShouldBeNull();

        // Assert
        var messages = logs.Holding(root).Select(e => (e.Level, Message: e.RenderMessage(CultureInfo.InvariantCulture))).ToList();
        messages.Count.ShouldBe(2);
        messages[0].Level.ShouldBe(LogEventLevel.Error);
        messages[0].Message.ShouldContain(root);
        messages[0].Message.ShouldContain(cause);
        messages[1].Level.ShouldBe(LogEventLevel.Information);
    }
}
