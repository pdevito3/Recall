namespace Recall.Server.Domain.People.Mappings;

using Dtos;
using Riok.Mapperly.Abstractions;

[Mapper]
public static partial class PersonMapper
{
    public static partial IQueryable<PersonDto> ToPersonDtoQueryable(this IQueryable<Person> queryable);

    [MapperIgnoreSource(nameof(Person.TenantId))]
    [MapperIgnoreSource(nameof(Person.UserId))]
    [MapperIgnoreSource(nameof(Person.LastModifiedOn))]
    [MapperIgnoreSource(nameof(Person.IsDeleted))]
    [MapperIgnoreSource(nameof(Person.DomainEvents))]
    public static partial PersonDto ToPersonDto(this Person person);
}
