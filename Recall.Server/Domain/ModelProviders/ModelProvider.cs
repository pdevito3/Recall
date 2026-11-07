namespace Recall.Server.Domain.ModelProviders;

using Exceptions;

/// <summary>
/// The AI service that an Admin connects for the app. A connect does not change which one a Cleanup uses: a provider takes
/// words only when a person chooses it and it has a model. The newest choice of a tenant is the active one, so a change of
/// the active one writes one row, and a delete of it falls back to the one before.
/// </summary>
public class ModelProvider : BaseEntity, ITenantable
{
    public const string IdPrefix = "mpr";
    public const int NameMaxLength = 100;
    public const int BaseUrlMaxLength = 500;
    public const int ModelMaxLength = 200;
    public const int ApiKeyMaxLength = 1_000;

    // The protected form of the longest key, in base64 with the header of the key ring.
    public const int ProtectedApiKeyMaxLength = 4_000;

    private const string NameField = "name";
    private const string KindField = "kind";
    private const string BaseUrlField = "baseUrl";
    private const string ApiKeyField = "apiKey";
    private const string ModelField = "model";

    [NotSensitive] public string TenantId { get; private set; } = default!;

    public ModelProviderKind Kind { get; private set; }

    [NotSensitive] public string Name { get; private set; } = default!;

    [NotSensitive] public string? BaseUrl { get; private set; }

    /// <summary>The API key under the key ring. Only <see cref="ApiKeyProtector"/> reads it.</summary>
    [Sensitive]
    [NotRecorded("A key that a person deletes or replaces must not stay in an audit entry beside the key ring in a backup.")]
    public string? ProtectedApiKey { get; private set; }

    /// <summary>A model from the list of the provider. It is null until the Admin picks one.</summary>
    [NotSensitive] public string? Model { get; private set; }

    /// <summary>Null until a person chooses this provider, and again when its model goes away.</summary>
    [NotSensitive] public DateTimeOffset? ActivatedOn { get; private set; }

    public static ModelProvider Connect(string tenantId, ModelProviderKind kind, string name, string? baseUrl, string? protectedApiKey)
    {
        var provider = new ModelProvider { TenantId = tenantId, Kind = kind };
        provider.SetName(name);
        provider.SetBaseUrl(baseUrl);
        provider.ProtectedApiKey = protectedApiKey;
        provider.RequireKey();
        return provider;
    }

    /// <summary>A null key keeps the stored key, so an edit of the name needs no key. A new address needs the key again, so a stored key never goes to an address that the person who entered it did not see.</summary>
    public void Update(string name, string? baseUrl, string? protectedApiKey, string? model, uint version)
    {
        GuardVersion(version);
        SetName(name);
        var addressWas = BaseUrl;
        SetBaseUrl(baseUrl);
        ValidationException.Must(BaseUrl == addressWas || ProtectedApiKey is null || protectedApiKey is not null, ApiKeyField, "Please enter the API key again for the new base URL.");
        ProtectedApiKey = protectedApiKey ?? ProtectedApiKey;
        RequireKey();
        SetModel(model);
    }

    public void Activate(DateTimeOffset now)
    {
        ValidationException.Must(Model is not null, ModelField, "Please pick a model before you make this model provider active.");
        ActivatedOn = now;
    }

    /// <summary>A deleted row stays for the audit log. A key must not stay with it.</summary>
    public void ForgetKey() => ProtectedApiKey = null;

    private void SetName(string name)
    {
        ValidationException.ThrowWhenNullOrWhitespace(name, NameField, "Please provide a name.");
        var trimmed = name.Trim();
        ValidationException.ThrowWhenLongerThan(trimmed, NameMaxLength, NameField, $"The name must not have more than {NameMaxLength} characters.");
        ValidationException.ThrowWhenHasCharacterNotStorable(trimmed, NameField, "The name has a character that is not allowed.");
        Name = trimmed;
    }

    private void SetBaseUrl(string? baseUrl)
    {
        var address = ModelProviderAddress.Normalized(baseUrl);
        ValidationException.Must(Kind != ModelProviderKind.OpenAiCompatible || address is not null, BaseUrlField, "Please provide the base URL of the model provider.");
        BaseUrl = address;
    }

    private void SetModel(string? model)
    {
        var trimmed = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        if (trimmed is not null)
        {
            ValidationException.ThrowWhenLongerThan(trimmed, ModelMaxLength, ModelField, $"The model must not have more than {ModelMaxLength} characters.");
            ValidationException.ThrowWhenHasCharacterNotStorable(trimmed, ModelField, "The model has a character that is not allowed.");
        }

        Model = trimmed;
        if (Model is null)
            ActivatedOn = null;
    }

    private void RequireKey()
        => ValidationException.Must(Kind != ModelProviderKind.Anthropic || ProtectedApiKey is not null, ApiKeyField, "Please provide the API key of the model provider.");

    /// <summary>The checks of a key that a person sends, before it goes under the key ring.</summary>
    public static string? ApiKeyOf(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return null;

        var trimmed = apiKey.Trim();
        ValidationException.ThrowWhenLongerThan(trimmed, ApiKeyMaxLength, ApiKeyField, $"The API key must not have more than {ApiKeyMaxLength} characters.");
        ValidationException.Must(trimmed.All(c => c is > ' ' and < '\u007f'), ApiKeyField, "The API key has a character that is not allowed.");
        return trimmed;
    }

    public static ModelProviderKind KindOf(string kind)
        => Enum.TryParse<ModelProviderKind>(kind, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new ValidationException(KindField, "The kind of model provider is not known.");

    protected ModelProvider() : base(IdPrefix) { }
}
