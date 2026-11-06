namespace Recall.Server.Domain.People.Dtos;

using Resources;

public sealed record PersonLabelDto
{
    [RequiredField]
    [SchemaMaxLength(typeof(Person), nameof(Person.LabelMaxLength))]
    public string Label { get; init; } = default!;
}
