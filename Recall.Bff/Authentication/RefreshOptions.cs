using System.ComponentModel.DataAnnotations;

namespace Recall.Bff.Authentication;

/// <summary>
/// The settings of the refresh lock, from <c>Bff:Refresh</c>.
/// </summary>
public sealed class RefreshOptions
{
    public const string SectionName = "Bff:Refresh";

    [Range(typeof(TimeSpan), "00:00:00.100", "00:00:25")]
    public TimeSpan TokenRequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan CommitMargin = TimeSpan.FromSeconds(2);

    public TimeSpan InProcessWaitLimit => (3 * TokenRequestTimeout) + CommitMargin;
}
