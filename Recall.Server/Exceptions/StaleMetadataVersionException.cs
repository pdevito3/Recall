namespace Recall.Server.Exceptions;

public sealed class StaleMetadataVersionException() : Exception(StaleVersionException.StaleMessage)
{
    public const string Field = "metadataVersion";
}
