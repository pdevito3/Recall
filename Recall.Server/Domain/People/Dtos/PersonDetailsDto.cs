namespace Recall.Server.Domain.People.Dtos;

using Resources;

/// <summary>The details replace the stored details. A null or blank detail clears it.</summary>
public sealed record PersonDetailsDto
{
    [SchemaMaxLength(typeof(Person), nameof(Person.FullNameMaxLength))]
    public string? FullName { get; init; }

    [SchemaMaxLength(typeof(Person), nameof(Person.NotesMaxLength))]
    public string? Notes { get; init; }

    public required uint Version { get; init; }
}
