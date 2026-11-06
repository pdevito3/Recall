namespace Recall.FunctionalTests.TestUtilities;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

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
