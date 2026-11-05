namespace Recall.Server.Auth;

using Databases;
using Microsoft.Data.Sqlite;

public sealed class OwnerClaim(string claimFilePath)
{
    public const string FileName = "claim-owner";

    public static string PathFor(IConfiguration configuration)
    {
        var dataFile = new SqliteConnectionStringBuilder(configuration.GetConnectionString(DatabaseConsts.DatabaseName)).DataSource;
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dataFile))!, FileName);
    }

    /// <summary>A move to a unique name is atomic, so one caller wins and the file is gone for each other caller.</summary>
    public bool TryConsume()
    {
        var taken = $"{claimFilePath}.{Guid.CreateVersion7():N}";
        try
        {
            File.Move(claimFilePath, taken);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }

        File.Delete(taken);
        return true;
    }
}
