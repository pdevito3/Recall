namespace Recall.Server.Resources.Entrypoints.Http;

/// <summary>Kestrel throws when a body passes its limit, and a 413 with no body gets the same answer.</summary>
public static class RequestTooLargeProblem
{
    public const string Detail = "The file is too large to send. Choose a smaller file.";

    public static IApplicationBuilder UseRequestTooLargeProblem(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge && !context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            }

            if (context.Response.HasStarted || context.Response.StatusCode != StatusCodes.Status413PayloadTooLarge)
                return;

            await context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails =
                {
                    Status = StatusCodes.Status413PayloadTooLarge,
                    Detail = context.GetEndpoint()?.Metadata.GetMetadata<RequestTooLargeDetail>()?.Detail ?? Detail,
                },
            });
        });
}

public sealed record RequestTooLargeDetail(string Detail);
