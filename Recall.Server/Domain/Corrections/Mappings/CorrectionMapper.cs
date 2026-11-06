namespace Recall.Server.Domain.Corrections.Mappings;

using Dtos;
using Riok.Mapperly.Abstractions;

[Mapper]
public static partial class CorrectionMapper
{
    public static partial IQueryable<CorrectionDto> ToCorrectionDtoQueryable(this IQueryable<Correction> queryable);

    [MapperIgnoreSource(nameof(Correction.TenantId))]
    [MapperIgnoreSource(nameof(Correction.UserId))]
    [MapperIgnoreSource(nameof(Correction.LastModifiedOn))]
    [MapperIgnoreSource(nameof(Correction.IsDeleted))]
    [MapperIgnoreSource(nameof(Correction.DomainEvents))]
    public static partial CorrectionDto ToCorrectionDto(this Correction correction);
}
