namespace Recall.Server.Domain.ModelProviders;

public enum ModelProviderKind
{
    Anthropic = 0,
    OpenAiCompatible = 1,
}

public static class ModelProviderKinds
{
    /// <summary>
    /// The Anthropic Messages API takes text, images, and documents, and no audio. An OpenAI-compatible provider can serve
    /// <c>POST {base}/audio/transcriptions</c>, as OpenAI, Groq, and local speech servers do. Read on 7 November 2026.
    /// </summary>
    public static bool TakesAudio(this ModelProviderKind kind) => kind == ModelProviderKind.OpenAiCompatible;
}
