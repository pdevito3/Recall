namespace Recall.Server.Domain.ModelProviders;

public static class ModelProviderQueries
{
    /// <summary>The first one is the one that the app uses: a person chose it, it has a model, and its choice is the newest of the tenant.</summary>
    public static IQueryable<ModelProvider> ActiveFirst(this IQueryable<ModelProvider> providers)
        => providers.Where(p => p.ActivatedOn != null && p.Model != null).NewestActivationFirst();

    /// <summary>Each provider for a list. A provider that a person did not choose comes last.</summary>
    public static IQueryable<ModelProvider> NewestActivationFirst(this IQueryable<ModelProvider> providers)
        => providers.OrderByDescending(p => p.ActivatedOn).ThenByDescending(p => p.Id);

    /// <summary>The first one gets the audio of the tenant. With none, the local engine makes the text.</summary>
    public static IQueryable<ModelProvider> TranscriptionFirst(this IQueryable<ModelProvider> providers)
        => providers.Where(p => p.TranscriptionChosenOn != null && p.TranscriptionModel != null)
            .OrderByDescending(p => p.TranscriptionChosenOn).ThenByDescending(p => p.Id);
}
