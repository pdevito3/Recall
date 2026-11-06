namespace Recall.Server.Domain;

public sealed class EnumNamesAttribute<T> : FixedNamesAttribute where T : struct, Enum
{
    public override IReadOnlyList<string> Names => Enum.GetNames<T>();
}
