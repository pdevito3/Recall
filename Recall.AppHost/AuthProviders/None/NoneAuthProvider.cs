namespace Recall.AppHost.AuthProviders;

public sealed class NoneAuthProvider : IAuthProvider
{
    public bool RunsE2eTests => false;

    public AuthProviderConfig? AddTo(IDistributedApplicationBuilder builder, AuthClientUrls clientUrls) => null;
}
