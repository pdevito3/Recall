// Each type in this file is a fixture for JournalRulesTests.
namespace Recall.ArchitectureFixtures.Core;

using Microsoft.EntityFrameworkCore;
using Recall.Server.Databases;

public static class JournalFilter
{
    public static IQueryable<Recall.Server.Domain.Sessions.Session> ReadsEachJournal(AppDbContext dbContext)
        => dbContext.Sessions.IgnoreQueryFilters();

    public static IQueryable<Recall.Server.Domain.Tenants.Tenant> ReadsATableOutsideTheJournal(AppDbContext dbContext)
        => dbContext.Tenants.IgnoreQueryFilters();
}
