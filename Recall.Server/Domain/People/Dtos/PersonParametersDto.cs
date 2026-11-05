namespace Recall.Server.Domain.People.Dtos;

using Resources;

public sealed class PersonParametersDto : BasePaginationParameters
{
    public static PersonParametersDto From(int? pageNumber, int? pageSize)
    {
        var parameters = new PersonParametersDto();
        parameters.SetPage(pageNumber, pageSize);
        return parameters;
    }
}
