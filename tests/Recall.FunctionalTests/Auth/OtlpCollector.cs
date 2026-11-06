namespace Recall.FunctionalTests.Auth;

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Takes each OTLP export over gRPC and keeps its bytes. A string in protobuf is plain UTF-8, so a search of the bytes finds it.
/// </summary>
public sealed class OtlpCollector : IAsyncDisposable
{
    private const string LogsService = "LogsService";
    private const string TraceService = "TraceService";

    // A gRPC message with no content: no compression flag and a length of 0.
    private static readonly byte[] EmptyMessage = new byte[5];

    private readonly ConcurrentQueue<(string Path, byte[] Body)> _exports = new();
    private readonly WebApplication _app;

    private OtlpCollector()
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        _app = builder.Build();
        _app.Run(TakeAsync);
    }

    public string Endpoint => _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    public static OtlpCollector Start()
    {
        var collector = new OtlpCollector();
        collector._app.StartAsync().GetAwaiter().GetResult();
        return collector;
    }

    public bool Holds(string text) => Holds(_ => true, text);

    public bool LogsHold(string text) => Holds(path => path.Contains(LogsService, StringComparison.Ordinal), text);

    public bool TracesHold(string text) => Holds(path => path.Contains(TraceService, StringComparison.Ordinal), text);

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private bool Holds(Func<string, bool> ofPath, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return _exports.Any(export => ofPath(export.Path) && export.Body.AsSpan().IndexOf(bytes) >= 0);
    }

    private async Task TakeAsync(HttpContext context)
    {
        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body);
        _exports.Enqueue((context.Request.Path, body.ToArray()));

        context.Response.ContentType = "application/grpc";
        await context.Response.Body.WriteAsync(EmptyMessage);
        context.Response.AppendTrailer("grpc-status", "0");
    }
}
