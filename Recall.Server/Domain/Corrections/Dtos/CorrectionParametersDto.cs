namespace Recall.Server.Domain.Corrections.Dtos;

using Resources;

public sealed class CorrectionParametersDto : BasePaginationParameters
{
    public static CorrectionParametersDto From(int? pageNumber, int? pageSize)
    {
        var parameters = new CorrectionParametersDto();
        parameters.SetPage(pageNumber, pageSize);
        return parameters;
    }
}
