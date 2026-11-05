namespace Recall.Server.Resources.Entrypoints.Auth;

using Microsoft.Net.Http.Headers;

/// <summary>
/// A redirect that writes the header itself. <c>Results.Redirect</c> logs its destination, and a destination here holds
/// a state, a code, or a challenge.
/// </summary>
public sealed class AuthRedirect(string location) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = StatusCodes.Status302Found;
        httpContext.Response.Headers.Location = location;
        httpContext.Response.Headers.CacheControl = "no-store";
        return Task.CompletedTask;
    }
}

/// <summary>Reads a small form body. Only the urlencoded form, because the multipart reader can spool a large body to disk.</summary>
public static class AuthForm
{
    private const long MaxBytes = 16 * 1024;

    public static async Task<IFormCollection?> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
            || !contentType.MediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
            || request.ContentLength > MaxBytes)
            return null;

        var limit = request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
            limit.MaxRequestBodySize = MaxBytes;

        try
        {
            return await request.ReadFormAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or BadHttpRequestException)
        {
            return null;
        }
    }

    public static string? OneValue(IFormCollection form, string name)
        => form.TryGetValue(name, out var values) && values.Count == 1 ? values[0] : null;
}
