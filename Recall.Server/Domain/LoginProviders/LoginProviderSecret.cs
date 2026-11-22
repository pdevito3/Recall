namespace Recall.Server.Domain.LoginProviders;

using System.Security.Cryptography;
using Exceptions;
using Microsoft.AspNetCore.DataProtection;

/// <summary>The only code that reads or writes the client secret of a login provider. The database holds only the protected form (IDP-5).</summary>
public sealed class LoginProviderSecret(IDataProtectionProvider dataProtection)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("Recall.LoginProviders.ClientSecret.v1");

    public string Protect(string secret)
    {
        try
        {
            return _protector.Protect(secret);
        }
        catch (CryptographicException)
        {
            throw new ValidationException(LoginProvider.ClientSecretField, "The server cannot save a client secret, because it cannot use its key ring. Check the key folder and the log of the server.");
        }
    }

    /// <summary>The secret, or null when the key ring of this server cannot read it, so the Admin must enter it again.</summary>
    public string? Read(string protectedSecret)
    {
        try
        {
            return _protector.Unprotect(protectedSecret);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
