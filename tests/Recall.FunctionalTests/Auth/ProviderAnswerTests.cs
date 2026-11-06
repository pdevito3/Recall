namespace Recall.FunctionalTests.Auth;

using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Recall.Server.Auth.Entities;
using Serilog.Events;
using Shouldly;

internal static class AnswerFaults
{
    private const int OverTheLimit = 2 * 1024 * 1024;
    private const int UnderTheLimit = 900 * 1024;

    public const string Discovery = "/.well-known/openid-configuration";

    public static int SizeOf(bool over) => over ? OverTheLimit : UnderTheLimit;

    /// <summary>The same answer with one more member, so only the size can make it wrong. A gzip body goes through the stream that the real handler makes.</summary>
    public static Func<string, HttpResponseMessage, CancellationToken, Task<HttpResponseMessage>> Padded(string path, int decodedBytes, bool gzip)
        => async (at, answer, cancellationToken) =>
        {
            if (at != path)
                return answer;

            var json = JsonNode.Parse(await answer.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
            json["padding"] = new string('a', decodedBytes);
            var bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = gzip ? Gzipped(bytes) : new ByteArrayContent(bytes) };
        };

    public static Func<string, HttpResponseMessage, CancellationToken, Task<HttpResponseMessage>> Status(string path, HttpStatusCode status)
        => (at, answer, _) => Task.FromResult(at == path ? new HttpResponseMessage(status) { Content = new StringContent("not for the log") } : answer);

    public static Func<string, HttpResponseMessage, CancellationToken, Task<HttpResponseMessage>> Cut(string path)
        => (at, answer, _) => at == path
            ? throw new HttpRequestException(HttpRequestError.ResponseEnded, "not for the log")
            : Task.FromResult(answer);

    public static Func<string, HttpResponseMessage, CancellationToken, Task<HttpResponseMessage>> NoAnswer(string path)
        => async (at, answer, cancellationToken) =>
        {
            if (at == path)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            return answer;
        };

    private static StreamContent Gzipped(byte[] bytes)
    {
        var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(bytes);
        compressed.Length.ShouldBeLessThan(bytes.Length / 100, "the bytes on the wire are under the limit, and only the decoded bytes are over it");
        compressed.Position = 0;
        return new StreamContent(new GZipStream(compressed, CompressionMode.Decompress));
    }
}

/// <summary>A fault stands for the whole of one provider, so these tests run one at a time on the host that they use.</summary>
[NotInParallel("two-providers")]
public class ProviderAnswerLimitTests
{
    private static AuthFlowFactory Host => AuthHosts.TwoProviders;

    [Test]
    [Arguments("/token", false)]
    [Arguments("/token", true)]
    [Arguments("/jwks", false)]
    [Arguments("/jwks", true)]
    public async Task a_token_or_key_answer_over_the_limit_is_refused(string path, bool gzip)
    {
        // Arrange
        await WaitForCacheOfTheLastProbeAsync();
        using var browser = new AuthBrowser(Host);
        var authorize = await browser.StartWebAsync();
        Host.Provider.AnswerFault = AnswerFaults.Padded(path, AnswerFaults.SizeOf(over: true), gzip);
        try
        {
            // Act
            var response = await browser.FinishAtProviderAsync(authorize, new StubLogin($"subject-{Guid.NewGuid():N}"));

            // Assert
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await response.Content.ReadAsStringAsync()).ShouldContain("<h1>Sign-in failed</h1>");
            browser.SessionCookie.ShouldBeNull();
        }
        finally
        {
            Host.Provider.AnswerFault = null;
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task a_userinfo_answer_over_the_limit_leaves_the_login_on_the_id_token(bool gzip)
    {
        // Arrange
        await WaitForCacheOfTheLastProbeAsync();
        using var browser = new AuthBrowser(Host);
        var subject = $"subject-{Guid.NewGuid():N}";
        var authorize = await browser.StartWebAsync();
        Host.Provider.AnswerFault = AnswerFaults.Padded("/userinfo", AnswerFaults.SizeOf(over: true), gzip);
        try
        {
            // Act
            await browser.FinishAtProviderAsync(authorize, new StubLogin(subject) { Name = "Token Person", UserinfoName = "Userinfo Person" });

            // Assert
            browser.SessionCookie.ShouldNotBeNull();
            var name = await Host.WithDatabaseAsync(db => db.Users.Where(u => db.Set<ExternalLogin>().Any(link => link.Subject == subject && link.UserId == u.Id)).Select(u => u.FirstName).SingleAsync());
            name.ShouldBe("Token");
        }
        finally
        {
            Host.Provider.AnswerFault = null;
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task a_discovery_answer_over_the_limit_shows_that_provider_as_down_and_the_healthy_provider_still_signs_in(bool gzip)
    {
        // Arrange
        Host.Provider.AnswerFault = AnswerFaults.Padded(AnswerFaults.Discovery, AnswerFaults.SizeOf(over: true), gzip);
        try
        {
            await WaitForCacheOfTheLastProbeAsync();
            using var browser = new AuthBrowser(Host);

            // Act
            var login = await browser.GetAsync("/auth/login");
            var startOfBad = await browser.GetAsync($"/auth/oidc/{AuthFlowFactory.ModeId}/start?client=web");
            var startOfHealthy = await browser.GetAsync($"/auth/oidc/{AuthFlowFactory.OtherModeId}/start?client=web");

            // Assert
            login.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await login.Content.ReadAsStringAsync()).ShouldContain("Stub is not available.");
            startOfBad.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            startOfHealthy.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        }
        finally
        {
            Host.Provider.AnswerFault = null;
            await WaitForCacheOfTheLastProbeAsync();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task a_large_answer_under_the_limit_still_signs_in(bool gzip)
    {
        // Arrange
        await WaitForCacheOfTheLastProbeAsync();
        using var browser = new AuthBrowser(Host);
        var authorize = await browser.StartWebAsync();
        Host.Provider.AnswerFault = AnswerFaults.Padded("/token", AnswerFaults.SizeOf(over: false), gzip);
        try
        {
            // Act
            await browser.FinishAtProviderAsync(authorize, new StubLogin($"subject-{Guid.NewGuid():N}"));

            // Assert
            browser.SessionCookie.ShouldNotBeNull();
        }
        finally
        {
            Host.Provider.AnswerFault = null;
        }
    }

    // One probe lasts 2 seconds in the Server.
    private static Task WaitForCacheOfTheLastProbeAsync() => Task.Delay(TimeSpan.FromSeconds(2.1));
}

/// <summary>Only this host captures the logs of the Server. A fault stands for the whole of its provider, so these tests run one at a time on it.</summary>
[NotInParallel("owner-claim-file")]
public class ProviderAnswerLogTests
{
    private static AuthFlowFactory Host => AuthHosts.Sweep;

    [Test]
    [Arguments("/token", HttpStatusCode.BadRequest, "token")]
    [Arguments("/token", HttpStatusCode.InternalServerError, "token")]
    [Arguments("/jwks", HttpStatusCode.InternalServerError, "key")]
    [Arguments("/userinfo", HttpStatusCode.InternalServerError, "userinfo")]
    public async Task a_status_on_an_answer_logs_the_provider_the_answer_and_the_status(string path, HttpStatusCode status, string answer)
    {
        // Act
        var lines = await LinesOfALoginAsync(new StubLogin(NewSubject()), AnswerFaults.Status(path, status));

        // Assert
        ShouldNameIt(lines, answer, $"status {(int)status}");
    }

    [Test]
    public async Task a_cut_connection_logs_the_cause_and_not_a_refused_code()
    {
        // Act
        var lines = await LinesOfALoginAsync(new StubLogin(NewSubject()), AnswerFaults.Cut("/token"));

        // Assert
        ShouldNameIt(lines, "token", nameof(HttpRequestError.ResponseEnded));
    }

    [Test]
    public async Task a_token_answer_with_no_id_token_logs_that_and_not_a_refused_code()
    {
        // Act
        var lines = await LinesOfALoginAsync(new StubLogin(NewSubject()) { TokenBody = """{"access_token":"secret-of-the-answer"}""" }, null);

        // Assert
        ShouldNameIt(lines, "token", "no id_token");
    }

    [Test]
    [Arguments("""not json""", "JsonReaderException")]
    [Arguments("""{"name":"no subject"}""", "no sub")]
    public async Task a_bad_userinfo_answer_logs_the_provider_the_answer_and_the_cause(string body, string cause)
    {
        // Act
        var lines = await LinesOfALoginAsync(new StubLogin(NewSubject()) { UserinfoBody = body }, null);

        // Assert
        ShouldNameIt(lines, "userinfo", cause);
    }

    [Test]
    public async Task a_discovery_answer_that_does_not_come_logs_that_it_did_not_come_in_time()
    {
        // Arrange
        await Task.Delay(TimeSpan.FromSeconds(2.1));
        var seen = Host.Logs.All.Count;
        Host.Provider.AnswerFault = AnswerFaults.NoAnswer(AnswerFaults.Discovery);
        try
        {
            using var browser = new AuthBrowser(Host);

            // Act
            await browser.GetAsync("/auth/status");

            // Assert
            ShouldNameIt(WarningsSince(seen), "discovery", "no answer in time");
        }
        finally
        {
            Host.Provider.AnswerFault = null;
            await Task.Delay(TimeSpan.FromSeconds(2.1));
        }
    }

    private static async Task<List<string>> LinesOfALoginAsync(StubLogin login, Func<string, HttpResponseMessage, CancellationToken, Task<HttpResponseMessage>>? fault)
    {
        using var browser = new AuthBrowser(Host);
        var authorize = await browser.StartWebAsync();
        var seen = Host.Logs.All.Count;
        Host.Provider.AnswerFault = fault;
        try
        {
            await browser.FinishAtProviderAsync(authorize, login);
            return WarningsSince(seen);
        }
        finally
        {
            Host.Provider.AnswerFault = null;
        }
    }

    private static List<string> WarningsSince(int seen)
        => Host.Logs.All.Skip(seen).Where(line => line.Level == LogEventLevel.Warning).Select(line => line.RenderMessage(CultureInfo.InvariantCulture)).ToList();

    private static void ShouldNameIt(List<string> lines, string answer, string cause)
    {
        lines.ShouldContain(line => line.Contains(AuthFlowFactory.ModeId, StringComparison.Ordinal)
            && line.Contains(answer, StringComparison.Ordinal)
            && line.Contains(cause, StringComparison.Ordinal));
        lines.ShouldNotContain(line => line.Contains("not for the log", StringComparison.Ordinal) || line.Contains("secret-of-the-answer", StringComparison.Ordinal));
        foreach (var handedOut in Host.Provider.HandedOut)
            lines.ShouldNotContain(line => line.Contains(handedOut, StringComparison.Ordinal));
    }

    private static string NewSubject() => $"subject-{Guid.NewGuid():N}";
}
