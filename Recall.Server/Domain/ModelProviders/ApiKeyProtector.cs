namespace Recall.Server.Domain.ModelProviders;

using System.Security.Cryptography;
using Exceptions;
using Microsoft.AspNetCore.DataProtection;

/// <summary>The only code that reads or writes a key of a model provider. The database holds only the protected form.</summary>
public sealed class ApiKeyProtector(IDataProtectionProvider dataProtection)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("Recall.ModelProviders.ApiKey.v1");

    public string? Protect(string? apiKey)
    {
        if (apiKey is null)
            return null;

        try
        {
            return _protector.Protect(apiKey);
        }
        catch (CryptographicException)
        {
            throw new ValidationException("apiKey", "The server cannot save an API key, because its key folder takes no write.");
        }
    }

    public ProtectedKeyRead Read(string? protectedKey)
    {
        if (protectedKey is null)
            return new ProtectedKeyRead.NoKey();

        try
        {
            return new ProtectedKeyRead.Readable(_protector.Unprotect(protectedKey));
        }
        catch (CryptographicException)
        {
            return new ProtectedKeyRead.Unreadable();
        }
    }

    public static ModelProviderKeyState StateOf(ProtectedKeyRead read) => read switch
    {
        ProtectedKeyRead.Readable => ModelProviderKeyState.Readable,
        ProtectedKeyRead.Unreadable => ModelProviderKeyState.Unreadable,
        _ => ModelProviderKeyState.None,
    };
}

public abstract record ProtectedKeyRead
{
    private ProtectedKeyRead() { }

    public sealed record NoKey : ProtectedKeyRead;

    /// <summary>The key ring of this server cannot read the key, so the Admin must enter the key again.</summary>
    public sealed record Unreadable : ProtectedKeyRead;

    public sealed record Readable(string ApiKey) : ProtectedKeyRead
    {
        public override string ToString() => nameof(Readable);
    }
}

public enum ModelProviderKeyState
{
    None = 0,
    Readable = 1,
    Unreadable = 2,
}
