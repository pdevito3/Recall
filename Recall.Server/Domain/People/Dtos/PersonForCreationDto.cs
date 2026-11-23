namespace Recall.Server.Domain.People.Dtos;

using Resources;

public sealed record PersonForCreationDto
{
    [RequiredField]
    [SchemaMaxLength(typeof(Person), nameof(Person.LabelMaxLength))]
    public string Label { get; init; } = default!;

    [SchemaMaxLength(typeof(Person), nameof(Person.FullNameMaxLength))]
    public string? FullName { get; init; }

    [SchemaMaxLength(typeof(Person), nameof(Person.NotesMaxLength))]
    public string? Notes { get; init; }
}
