namespace Recall.FunctionalTests.TestUtilities;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Recall.Server.Domain;
using Recall.Server.Domain.Users;
using Recall.Server.Resources.Entrypoints.Auth;

public static class HttpClientExtensions
{
    public static void SignInAsAppUser(this HttpClient client, string subject)
        => client.SetFakeBearerToken(new Dictionary<string, object>
        {
            ["sub"] = subject,
            ["azp"] = TestingWebApplicationFactory.FirstPartyClientId,
        });

    public static void SignInAsTrustedMachineClient(this HttpClient client)
        => client.SetFakeBearerToken(new Dictionary<string, object>
        {
            ["client_id"] = TestingWebApplicationFactory.TrustedMachineClientId,
        });

    public static void SignInAsMachineClient(this HttpClient client, string clientId)
        => client.SetFakeBearerToken(new Dictionary<string, object> { ["client_id"] = clientId });

    public static void SignInAsDelegatedClient(this HttpClient client, string subject, string clientId)
        => client.SetFakeBearerToken(new Dictionary<string, object> { ["sub"] = subject, ["azp"] = clientId });

    /// <summary>Signs in as a native session of the app for the user with this identifier, as a person who signed in to Recall.</summary>
    public static void SignInAsAppSession(this HttpClient client, string subject) => client.SignInAsAppSession(subject, SessionKind.Native);

    /// <summary>
    /// Signs in as a web session of the app for the user with this identifier, as a page of Recall in a browser. It sends
    /// the user of the page, as the SPA does.
    /// </summary>
    public static void SignInAsWebSession(this HttpClient client, string subject)
    {
        client.SignInAsAppSession(subject, SessionKind.Web);
        client.DefaultRequestHeaders.Remove(PageUserCheck.Header);
        client.DefaultRequestHeaders.Add(PageUserCheck.Header, subject);
    }

    private static void SignInAsAppSession(this HttpClient client, string subject, SessionKind kind)
    {
        var session = new SessionPrincipal(EntityId.New(AuthSession.IdPrefix), subject, kind, DateTimeOffset.MaxValue);
        var claims = AppSessionHandler.PrincipalOf(session).Claims.ToDictionary(c => c.Type, c => (object)c.Value);
        claims[AppSessionTestPrincipal.Marker] = "1";
        client.SetFakeBearerToken(claims);
    }

    /// <summary>Signs in as a new personal access token of the user with this identifier. Returns the token ID.</summary>
    public static string SignInAsPersonalAccessToken(this HttpClient client, string subject, TokenGrant grant)
    {
        var token = new PatClaims(EntityId.New(PatClaims.IdPrefix), grant);
        var claims = token.PrincipalOf(subject).Claims.ToDictionary(c => c.Type, c => (object)c.Value);
        claims[AppSessionTestPrincipal.Marker] = "1";
        client.SetFakeBearerToken(claims);
        return token.TokenId;
    }

    public static async Task<HttpResponseMessage> GetRequestAsync(this HttpClient client, string url)
        => await client.GetAsync(url);

    public static async Task<HttpResponseMessage> PostJsonRequestAsync(this HttpClient client, string url, object value)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        return await client.PostAsJsonAsync(url, value, options);
    }

    public static async Task<HttpResponseMessage> PutJsonRequestAsync(this HttpClient client, string url, object value)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        return await client.PutAsJsonAsync(url, value, options);
    }

    public static async Task<HttpResponseMessage> DeleteRequestAsync(this HttpClient client, string url)
        => await client.DeleteAsync(url);
}
