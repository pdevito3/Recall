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
    /// A rename to a unique name is atomic, so one caller wins and the file is gone for each other caller. A rename keeps the
    /// write time. A file that is too old goes too, because a restore that does not keep times would make a file that stays new again.
    /// A file that cannot go refuses the claim and does not stop the login, because the claim must be good for one login only.
    /// A link or a folder with this name is no claim, because the write time of a link says nothing about when the operator made it.
    /// </summary>
    public bool TryConsume()
    {
        var taken = $"{claimFilePath}.{Guid.CreateVersion7():N}";
        try
        {
            if ((File.GetAttributes(claimFilePath) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            {
                _logger.Here().Warning(
                    "The owner claim file {ClaimFile} is a link or a folder and not a regular file, so the claim did nothing. Remove it, and create a regular file to claim the Owner",
                    claimFilePath);
                return false;
            }

            // Only the overwrite form is a rename. The other form on Unix links or copies, then removes the old name, so two callers can
            // each win, and a file that cannot go leaves a new copy at each login.
            File.Move(claimFilePath, taken, overwrite: true);

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
