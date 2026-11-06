using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Recall.Bff.Sessions;

/// <summary>
/// The context for the EF Core tools, so that <c>dotnet ef migrations add</c> does not start the BFF.
/// </summary>
public sealed class BffDbContextFactory : IDesignTimeDbContextFactory<BffDbContext>
{
    public BffDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<BffDbContext>();
        BffSessionExtensions.ConfigureBffDb(options, "Data Source=bff.db");
        return new BffDbContext(options.Options);
    }
}
