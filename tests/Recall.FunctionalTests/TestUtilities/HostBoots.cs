namespace Recall.FunctionalTests.TestUtilities;

/// <summary>
/// The boot of a host points the static logger of the process at the new host for some seconds, so the static lines of each
/// other host go there, and a test that reads them misses lines (#165). So each host of the assembly boots before the first test.
/// </summary>
public static class HostBoots
{
    private static volatile bool _closed;

    /// <summary>The first test starts after this.</summary>
    public static void Close() => _closed = true;

    public static void ThrowIfClosed()
    {
        if (_closed)
            throw new InvalidOperationException("A host booted after the first test. Boot it in AuthHosts.Start, before the first test.");
    }
}
