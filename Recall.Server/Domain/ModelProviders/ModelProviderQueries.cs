namespace Recall.Server.Domain.ModelProviders;

public static class ModelProviderQueries
{
    /// <summary>The one that the app uses. The newest activation of the tenant wins.</summary>
    public static IQueryable<ModelProvider> NewestActivationFirst(this IQueryable<ModelProvider> providers)
        => providers.OrderByDescending(p => p.ActivatedOn).ThenByDescending(p => p.Id);
}
