namespace Recall.AppHost.AuthProviders;

/// <summary>
/// A stack with no provider of its own. With no other setting the Server boots with no login method and says so on its
/// login page. To bring a provider, set <c>Auth:Providers:0:*</c> in the AppHost configuration, and the Server gets it,
/// with <c>Auth:OpenSignup</c>. See docs/authentication.md.
/// </summary>
public sealed class NoneAuthProvider : IAuthProvider
{
    public bool RunsE2eTests => false;

    public AuthProviderConfig? AddTo(IDistributedApplicationBuilder builder, AuthClientUrls clientUrls) => null;
}
