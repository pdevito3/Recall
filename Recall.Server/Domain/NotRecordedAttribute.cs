namespace Recall.Server.Domain;

/// <summary>
/// The audit log does not store this value. <see cref="SensitiveAttribute"/> hides a value from a reader, but the stored
/// entry still holds it, and an entry stays after the row. Use this mark for a secret that a delete must really remove.
/// </summary>
/// <param name="reason">Why no entry holds the value, for the reader of the type.</param>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class NotRecordedAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}
