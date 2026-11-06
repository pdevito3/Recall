namespace Recall.Server.Auth;

using Databases;
using Microsoft.Data.Sqlite;
using Resources;
using Serilog;

public sealed class OwnerClaim(string claimFilePath, TimeProvider timeProvider)
{
    public const string FileName = "claim-owner";

    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

    private readonly ILogger _logger = Log.ForContext<OwnerClaim>();

    public string FilePath => claimFilePath;

    public static string PathFor(IConfiguration configuration)
    {
        var dataFile = new SqliteConnectionStringBuilder(configuration.GetConnectionString(DatabaseConsts.DatabaseName)).DataSource;
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dataFile))!, FileName);
    }

    /// <summary>
    /// A move to a unique name is atomic, so one caller wins and the file is gone for each other caller. A move keeps the
    /// write time. A file that is too old goes too, because a restore that does not keep times would make a file that stays new again.
    /// A file that cannot go refuses the claim and does not stop the login, because the claim must be good for one login only.
    /// </summary>
    public bool TryConsume()
    {
        var taken = $"{claimFilePath}.{Guid.CreateVersion7():N}";
        try
        {
            File.Move(claimFilePath, taken);

            // Duration, so that a write time in the future does not read as a new file.
            var age = (timeProvider.GetUtcNow().UtcDateTime - File.GetLastWriteTimeUtc(taken)).Duration();
            File.Delete(taken);
            if (age <= MaxAge)
                return true;

            _logger.Here().Warning(
                "Removed the owner claim file {ClaimFile} and did not use it. It is {AgeMinutes} minutes from the current time, and the limit is {MaxAgeMinutes}. To claim the Owner, create the file again and sign in within the limit",
                claimFilePath, (long)age.TotalMinutes, (long)MaxAge.TotalMinutes);
            return false;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.Here().Warning(
                "Could not remove the owner claim file {ClaimFile} ({Failure}), so the claim did nothing. Make the file and its folder writable for the Server, remove the file, and create it again to claim the Owner",
                claimFilePath, exception.GetType().Name);
            return false;
        }
    }
}
