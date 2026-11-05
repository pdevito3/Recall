namespace Recall.Server.Databases.Sqlite;

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>
/// Writes each <see cref="DateTimeOffset"/> parameter of a command in the format of the columns. FromSql and SqlQuery
/// find the mapping of a parameter with no model, so the default type mapping of the context does not reach them.
/// </summary>
public sealed class UtcTextParameters : DbCommandInterceptor
{
    public static UtcTextParameters Instance { get; } = new();

    private UtcTextParameters()
    {
    }

    public override DbCommand CommandInitialized(CommandEndEventData eventData, DbCommand result)
    {
        foreach (DbParameter parameter in result.Parameters)
        {
            if (parameter.Value is DateTimeOffset value)
                parameter.Value = UtcTextConverter.ToText(value);
        }

        return result;
    }
}
