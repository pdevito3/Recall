namespace Recall.FunctionalTests.TestUtilities;

public static class ApiRoutes
{
    public const string Base = "api/v1";
    public const string Health = "health";
    public const string Alive = "alive";
    public const string SupportBundle = $"{Base}/support-bundle";

    public static class Users
    {
        public static string GetList => $"{Base}/users";
        public static string GetCurrent => $"{Base}/users/me";
        public static string GetRecord(string id) => $"{Base}/users/{id}";
        public static string GetByIdentifier(string identifier) => $"{Base}/users/by-identifier/{identifier}";
        public static string Create => $"{Base}/users";
        public static string Initiate => $"{Base}/users/initiate";
        public static string Put(string id) => $"{Base}/users/{id}";
        public static string Delete(string id) => $"{Base}/users/{id}";
        public static string UpdateRole(string id) => $"{Base}/users/{id}/role";
        public static string AddPermission(string id) => $"{Base}/users/{id}/permissions";
        public static string RemovePermission(string id, string permission) => $"{Base}/users/{id}/permissions/{permission}";
    }

    public static class Tenants
    {
        public static string GetList => $"{Base}/tenants";
        public static string Create => $"{Base}/tenants";
        public static string GetRecord(string id) => $"{Base}/tenants/{id}";
        public static string Put(string id) => $"{Base}/tenants/{id}";
        public static string Delete(string id) => $"{Base}/tenants/{id}";
    }

    public static class Sessions
    {
        public static string Root => $"{Base}/sessions";
        public static string GetRecord(string id) => $"{Base}/sessions/{id}";
        public static string Draft(string id) => $"{Base}/sessions/{id}/draft";
        public static string Cleaned(string id) => $"{Base}/sessions/{id}/cleaned";
        public static string Metadata(string id) => $"{Base}/sessions/{id}/metadata";
        public static string RawRevisions(string id) => $"{Base}/sessions/{id}/raw-revisions";
        public static string RawRevision(string id, int number) => $"{Base}/sessions/{id}/raw-revisions/{number}";
        public static string CleanedRevisions(string id) => $"{Base}/sessions/{id}/cleaned-revisions";
        public static string CleanedRevision(string id, int number) => $"{Base}/sessions/{id}/cleaned-revisions/{number}";
    }

    public static class Sync
    {
        public static string Changes => $"{Base}/sync/changes";
    }

    public static class People
    {
        public static string Root => $"{Base}/people";
        public static string GetRecord(string id) => $"{Base}/people/{id}";
    }

    public static class Corrections
    {
        public static string Root => $"{Base}/corrections";
        public static string GetRecord(string id) => $"{Base}/corrections/{id}";
    }

    public static class UserSettings
    {
        public static string Root => $"{Base}/me/settings";
    }

    public static class FileStore
    {
        public static string Status => $"{Base}/file-store/status";
    }

    public static class Agents
    {
        public static string GetList => $"{Base}/agents";
        public static string Create => $"{Base}/agents";
        public static string GetRecord(string id) => $"{Base}/agents/{id}";
        public static string AddPermission(string id) => $"{Base}/agents/{id}/permissions";
        public static string RemovePermission(string id, string permission) => $"{Base}/agents/{id}/permissions/{permission}";
    }

    public static class AuditEntries
    {
        public static string GetPage(string entityId) => $"{Base}/audit-entries?entityId={Uri.EscapeDataString(entityId)}";
    }
}
