namespace Recall.Server.Auth;

using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using Databases;
using Domain;
using Domain.AuditEntries;
using Domain.Tenants;
using Domain.Tenants.Models;
using Domain.Users;
using Entities;
using Exceptions;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;

/// <summary>What a password form post ends in. The endpoints turn each case into a page or the session.</summary>
public abstract record PasswordOutcome
{
    /// <summary>A right password. <see cref="Guard"/> is the credential as this sign-in saved it, which the save of the session checks.</summary>
    public sealed record SignedIn(ProvisionOutcome.Provisioned User, CredentialGuard Guard) : PasswordOutcome;

    /// <summary>
    /// A right temporary password. The person picks a password next (PWD-16), while the temporary password stays the one
    /// issued at <see cref="ChangedAt"/>. A wrong try does not move it, so a stranger cannot void the choice.
    /// </summary>
    public sealed record MustChoose(string UserId, DateTimeOffset ChangedAt) : PasswordOutcome;

    public sealed record Refused(int Status, string Message) : PasswordOutcome;

    /// <summary>A 429 with <c>Retry-After</c>, for the wait of PWD-12 or a full line of hashes (PWD-11).</summary>
    public sealed record Wait(TimeSpan Left, string Message) : PasswordOutcome;

    /// <summary>The setup is done, by this request's rival or before it (PWD-3).</summary>
    public sealed record AlreadySetUp : PasswordOutcome;

    /// <summary>The credential of a choose attempt changed since the attempt, so the attempt is no longer valid.</summary>
    public sealed record Gone : PasswordOutcome;

    /// <summary>The person changed the password (PWD-17). The current session stays.</summary>
    public sealed record Changed : PasswordOutcome;

    /// <summary>The <c>claim-owner</c> file was not there, not fresh, or not a regular file at the post, so nothing changed (PWD-19).</summary>
    public sealed record NoClaim : PasswordOutcome;
}

/// <summary>
/// The login, the setup, and the choice of a password. The decisions are on <see cref="PasswordCredential"/>,
/// <see cref="PasswordPolicy"/>, <see cref="PasswordBackoff"/>, and <see cref="SetupCode"/>, and this class reads and saves.
/// </summary>
public sealed class PasswordLogin(
    IServiceScopeFactory scopeFactory,
    PasswordHashing hashing,
    PasswordBackoff backoff,
    SetupCode setupCode,
    OwnerClaim ownerClaim,
    TimeProvider timeProvider)
{
    public const string WrongMessage = "The login name or the password is wrong.";
    public const string StoppedMessage = "Password sign-in is stopped for this account after too many wrong tries. Ask an Admin for a temporary password.";
    public const string ExpiredMessage = "This temporary password expired. Ask an Admin for a new one.";
    public const string BusyMessage = "The Server is busy. Try again in a few seconds.";
    public const string SameAsTemporaryMessage = "Pick a password that is not your temporary password.";
    public const string WrongCodeMessage = "The setup code is wrong. Find the current code in the Server log, then try again.";
    public const string AlreadySetUpMessage = "This Server is already set up. Sign in instead.";
    public const string NameMissingMessage = "Please give your name.";
    public const string WrongCurrentMessage = "Your current password is wrong.";
    public const string NoPasswordToChangeMessage = "This account has no password to change. Sign in with a temporary password from an Admin to pick one.";

    public static readonly string ClaimGoneMessage =
        $"The claim-owner file is gone or too old, so nothing changed. Create the file again, then set the password within {(int)OwnerClaim.MaxAge.TotalMinutes} minutes.";

    private const string FirstTenantName = "Recall";
    private const int MaxSignInRuns = 3;

    private static readonly string[] SetupRaceConstraints = ["ix_tenants_name", "ix_auth_password_credentials_login_name"];

    private readonly ILogger _logger = Log.ForContext<PasswordLogin>();

    /// <summary>A test seam: runs after a right password saved its credential and before its session or app code is written.</summary>
    internal Func<string, Task>? BeforeSessionWrite { get; set; }

    public static string TooManyTriesMessage(TimeSpan left)
    {
        var minutes = (int)Math.Ceiling(left.TotalMinutes);
        return minutes == 1 ? "Too many wrong tries. Wait 1 minute, then try again." : $"Too many wrong tries. Wait {minutes} minutes, then try again.";
    }

    /// <summary>
    /// A sign-in with a login name and a password. With <c>temporaryOnly</c>, password login is Off, so only a Temporary credential takes a
    /// password (IDP-20). A Chosen or Stopped one answers as a login name that no credential holds (PWD-6), so the form tells nothing about the account.
    /// </summary>
    public async Task<PasswordOutcome> SignInAsync(
        string? typedName, string? typedPassword, IPAddress? address, bool temporaryOnly, CancellationToken cancellationToken)
    {
        // A wait runs no hash, so a stranger who guesses pays nothing and gets nothing.
        if (backoff.WaitLeft(typedName, address) is { } left)
            return new PasswordOutcome.Wait(left, TooManyTriesMessage(left));

        var password = PasswordPolicy.Normalize(typedPassword) ?? "";
        for (var run = 1; ; run++)
        {
            try
            {
                var (outcome, wrong) = await SignInOnceAsync(typedName, password, temporaryOnly, cancellationToken);
                if (wrong)
                    backoff.NoteWrong(typedName, address);
                else if (outcome is not PasswordOutcome.Wait)
                    backoff.NoteRight(typedName, address);
                return outcome;
            }
            // An issue, a choice, or another try changed the credential after the load, and the save wrote nothing. The
            // next run checks the password against the row as it is now, so a password that an issue replaced is wrong.
            catch (StaleVersionException) when (run < MaxSignInRuns)
            {
                Telemetry.AddEvent([("app.race.run", run)], "race.lost");
            }
            catch (StaleVersionException)
            {
                Telemetry.AddEvent([("app.race.run", run)], "race.exhausted");
                return Busy();
            }
        }
    }

    /// <summary>One load, check, and save of a sign-in. The flag tells whether the try counts as wrong for PWD-12.</summary>
    /// <exception cref="StaleVersionException">Another write changed the credential after the load.</exception>
    private async Task<(PasswordOutcome Outcome, bool Wrong)> SignInOnceAsync(
        string? typedName, string password, bool temporaryOnly, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateSystemScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var credential = LoginName.TryOf(typedName, out var name)
            ? await dbContext.Set<PasswordCredential>()
                .TagWithOperationCallSite("auth.password.find_credential")
                .FirstOrDefaultAsync(p => p.LoginName == name, cancellationToken)
            : null;
        if (temporaryOnly && credential is not { State: PasswordState.Temporary })
            credential = null;

        // A name that no credential holds and a Stopped credential each run one hash of a fixed value (PWD-6).
        var storedHash = credential is { State: not PasswordState.Stopped } ? credential.PasswordHash : null;
        if (await hashing.CheckAsync(storedHash, password, cancellationToken) is not { } check)
            return (Busy(), false);

        if (credential is null)
            return (new PasswordOutcome.Refused(StatusCodes.Status401Unauthorized, WrongMessage), true);

        Activity.Current?.SetTag("app.user_id", credential.UserId);
        var wasStopped = credential.State == PasswordState.Stopped;
        var verdict = credential.Answer(check, timeProvider.GetUtcNow());
        var outcome = verdict switch
        {
            PasswordVerdict.Wrong => new PasswordOutcome.Refused(StatusCodes.Status401Unauthorized, WrongMessage),
            PasswordVerdict.Stopped => new PasswordOutcome.Refused(StatusCodes.Status401Unauthorized, StoppedMessage),
            PasswordVerdict.Expired => new PasswordOutcome.Refused(StatusCodes.Status401Unauthorized, ExpiredMessage),
            PasswordVerdict.MustChoose => new PasswordOutcome.MustChoose(credential.UserId, credential.ChangedAt),
            _ => await SignedInAsync(dbContext, credential, check, password, cancellationToken),
        };
        // The save checks the version of the row, so no outcome leaves here for a credential that changed after the load.
        await dbContext.SaveChangesAsync(cancellationToken);

        if (!wasStopped && credential.State == PasswordState.Stopped)
            _logger.Here().Warning("The password of user {UserId} stopped after {WrongTries} wrong tries in a row", credential.UserId, PasswordCredential.StopAt);
        else if (outcome is PasswordOutcome.SignedIn)
            _logger.Here().Information("User {UserId} signed in with a password", credential.UserId);
        else
            _logger.Here().Information("A password sign-in of user {UserId} ended as {Verdict}", credential.UserId, verdict);
        return (outcome, verdict is PasswordVerdict.Wrong or PasswordVerdict.Stopped);
    }

    private async Task<PasswordOutcome> SignedInAsync(
        AppDbContext dbContext, PasswordCredential credential, PasswordCheck check, string password, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users
            .TagWithOperationCallSite("auth.password.user")
            .FirstOrDefaultAsync(u => u.Id == credential.UserId, cancellationToken);
        if (user is null)
            return new PasswordOutcome.Refused(StatusCodes.Status401Unauthorized, WrongMessage);

        // A Server whose cost went up stores the new hash in the same save (PWD-10). With no slot, the next login tries again.
        if (check == PasswordCheck.RightAndRehash && await hashing.HashAsync(password, cancellationToken) is { } rehashed)
            credential.Rehash(rehashed);

        return new PasswordOutcome.SignedIn(new ProvisionOutcome.Provisioned(user.Id, user.FullName), credential.Guard);
    }

    /// <summary>
    /// The choice of a password after a right temporary password (PWD-16). A credential whose password changed after the
    /// choose attempt, through a new temporary password or another choice, gives <see cref="PasswordOutcome.Gone"/>. A save
    /// that lost to another write, such as a wrong try, runs once more on the new row.
    /// </summary>
    public async Task<PasswordOutcome> ChooseAsync(string userId, DateTimeOffset changedAt, string? typedPassword, CancellationToken cancellationToken)
    {
        try
        {
            return await ChooseOnceAsync(userId, changedAt, typedPassword, cancellationToken);
        }
        catch (StaleVersionException)
        {
            Telemetry.AddEvent([("app.race.run", 1)], "race.lost");
        }

        try
        {
            return await ChooseOnceAsync(userId, changedAt, typedPassword, cancellationToken);
        }
        catch (StaleVersionException)
        {
            _logger.Here().Information("A choice of a password of user {UserId} lost to a change of its credential twice", userId);
            return new PasswordOutcome.Gone();
        }
    }

    /// <exception cref="StaleVersionException">Another write changed the credential after the load.</exception>
    private async Task<PasswordOutcome> ChooseOnceAsync(string userId, DateTimeOffset changedAt, string? typedPassword, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateSystemScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var credential = await dbContext.Set<PasswordCredential>()
            .TagWithOperationCallSite("auth.password.choose_credential")
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        var user = await dbContext.Users
            .TagWithOperationCallSite("auth.password.choose_user")
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (credential is not { State: PasswordState.Temporary } || credential.ChangedAt != changedAt || user is null)
            return new PasswordOutcome.Gone();
        if (credential.IsExpired(now))
            return new PasswordOutcome.Refused(StatusCodes.Status401Unauthorized, ExpiredMessage);

        var choice = PasswordPolicy.Check(typedPassword, credential.LoginName, [user.FirstName, user.FullName]);
        if (choice is not PasswordChoice.Accepted accepted)
            return Refuse(((PasswordChoice.Refused)choice).Message);

        if (await hashing.CheckAsync(credential.PasswordHash, accepted.Password, cancellationToken) is not { } same)
            return Busy();
        if (same != PasswordCheck.Wrong)
            return Refuse(SameAsTemporaryMessage);
        if (await hashing.HashAsync(accepted.Password, cancellationToken) is not { } hash)
            return Busy();

        credential.Choose(hash, now);
        dbContext.RecordAction(new PasswordChosen(AuditSubject.Of(user)));
        await dbContext.SaveChangesAsync(cancellationToken);
        _logger.Here().Information("User {UserId} chose a password", user.Id);
        return new PasswordOutcome.SignedIn(new ProvisionOutcome.Provisioned(user.Id, user.FullName), credential.Guard);
    }

    /// <summary>
    /// The login name that the change form shows (PWD-17): a Chosen or a Stopped credential. A Stopped one gets the form so that the
    /// person reads why each try is refused. Null for a user with no password or with a temporary one.
    /// </summary>
    public async Task<string?> ChangeableLoginNameOfAsync(string userId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateSystemScope();
        var name = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<PasswordCredential>()
            .TagWithOperationCallSite("auth.password.changeable_login_name")
            .AsNoTracking()
            .Where(c => c.UserId == userId && (c.FailedCount >= PasswordCredential.StopAt || c.TemporaryUntil == null))
            .Select(c => (LoginName?)c.LoginName)
            .FirstOrDefaultAsync(cancellationToken);
        return name?.Value;
    }

    /// <summary>
    /// A person in a web or native session changes the password with the current one (PWD-17). A wrong current password counts as a
    /// wrong login for the login name and the address (PWD-12) and for the credential (PWD-13). One save writes the new password,
    /// the end of each other session and each personal access token, and the delete of each login in flight, and it keeps
    /// <paramref name="sessionId"/>. A save that lost to another write of the credential runs again on the row as it is now, as a
    /// sign-in does, so a temporary password that an Admin issued in between stays and the change gets 409.
    /// </summary>
    public async Task<PasswordOutcome> ChangeAsync(
        string userId, string sessionId, string? typedCurrent, string? typedNew, IPAddress? address, CancellationToken cancellationToken)
    {
        var current = PasswordPolicy.Normalize(typedCurrent) ?? "";
        // A run again after a lost save keeps the hash of the new password, so it costs one check and no second hash.
        var newHash = new StrongBox<string?>();
        for (var run = 1; ; run++)
        {
            try
            {
                var (outcome, loginName, wrong) = await ChangeOnceAsync(userId, sessionId, current, typedNew, newHash, address, cancellationToken);
                if (wrong)
                    backoff.NoteWrong(loginName, address);
                else if (outcome is PasswordOutcome.Changed)
                    backoff.NoteRight(loginName, address);
                return outcome;
            }
            catch (StaleVersionException) when (run < MaxSignInRuns)
            {
                Telemetry.AddEvent([("app.race.run", run)], "race.lost");
            }
            catch (StaleVersionException)
            {
                Telemetry.AddEvent([("app.race.run", run)], "race.exhausted");
                return Busy();
            }
        }
    }

    /// <summary>One load, check, and save of a change. The flag tells whether the try counts as wrong for PWD-12.</summary>
    /// <exception cref="StaleVersionException">Another write changed the credential, a session, or a login in flight after the load.</exception>
    private async Task<(PasswordOutcome Outcome, string? LoginName, bool Wrong)> ChangeOnceAsync(
        string userId, string sessionId, string current, string? typedNew, StrongBox<string?> newHash, IPAddress? address, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateSystemScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var credential = await dbContext.Set<PasswordCredential>()
            .TagWithOperationCallSite("auth.password.change_credential")
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        var user = await dbContext.Users
            .TagWithOperationCallSite("auth.password.change_user")
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (credential is null or { State: PasswordState.Temporary } || user is null)
            return (new PasswordOutcome.Refused(StatusCodes.Status409Conflict, NoPasswordToChangeMessage), null, false);

        var loginName = credential.LoginName.Value;
        if (backoff.WaitLeft(loginName, address) is { } left)
            return (new PasswordOutcome.Wait(left, TooManyTriesMessage(left)), loginName, false);

        var choice = PasswordPolicy.Check(typedNew, credential.LoginName, [user.FirstName, user.FullName]);
        if (choice is not PasswordChoice.Accepted accepted)
            return (Refuse(((PasswordChoice.Refused)choice).Message), loginName, false);

        var storedHash = credential.State == PasswordState.Stopped ? null : credential.PasswordHash;
        if (await hashing.CheckAsync(storedHash, current, cancellationToken) is not { } check)
            return (Busy(), loginName, false);

        var wasStopped = credential.State == PasswordState.Stopped;
        var now = timeProvider.GetUtcNow();
        var verdict = credential.Answer(check, now);
        if (verdict != PasswordVerdict.SignedIn)
        {
            // The save checks the version, so a wrong try counts only on the row that it was checked against.
            await dbContext.SaveChangesAsync(cancellationToken);
            if (!wasStopped && credential.State == PasswordState.Stopped)
                _logger.Here().Warning("The password of user {UserId} stopped after {WrongTries} wrong tries in a row", userId, PasswordCredential.StopAt);
            else
                _logger.Here().Information("A password change of user {UserId} ended as {Verdict}", userId, verdict);
            var message = verdict == PasswordVerdict.Stopped ? StoppedMessage : WrongCurrentMessage;
            return (new PasswordOutcome.Refused(StatusCodes.Status403Forbidden, message), loginName, true);
        }

        newHash.Value ??= await hashing.HashAsync(accepted.Password, cancellationToken);
        if (newHash.Value is null)
            return (Busy(), loginName, false);

        credential.Change(newHash.Value, now);
        await SessionStore.EndSignInsInSaveAsync(dbContext, userId, sessionId, RevokeReason.RevokeOthers, now, cancellationToken);
        dbContext.RecordAction(new PasswordChanged(AuditSubject.Of(user)));
        await dbContext.SaveChangesAsync(cancellationToken);
        _logger.Here().Information("User {UserId} changed the password and ended each other session", userId);
        return (new PasswordOutcome.Changed(), loginName, false);
    }

    /// <summary>
    /// The operator sets the login name and the password of the Owner through a fresh <c>claim-owner</c> file (PWD-19). The Admin is
    /// the one that the claim of an OIDC login picks, and a tenant with no such Admin gets a new one. With no fresh file the post
    /// answers as for a gone file before it reads or hashes anything, so it tells nothing about login names. The checks of the form,
    /// also the check of a login name in use under the tenant lock, run before the claim takes the file, so a refused value keeps it.
    /// Once the file is gone the claim must not be lost, so a save that lost to another write of the credential runs again under the
    /// tenant lock on the row as it is now. An Admin issue holds the same lock, so it lands before or after the claim and never inside it.
    /// </summary>
    public async Task<PasswordOutcome> ClaimOwnerAsync(string? typedLoginName, string? typedPassword, CancellationToken cancellationToken)
    {
        if (!ownerClaim.IsFresh())
            return new PasswordOutcome.NoClaim();
        if (!LoginName.TryOf(typedLoginName, out var loginName))
            return Refuse(LoginName.NotValidMessage);

        string tenantId;
        string hash;
        await using (var scope = scopeFactory.CreateSystemScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (await Provisioner.ClaimedTenantIdAsync(dbContext, cancellationToken) is not { } oldestTenantId)
            {
                ownerClaim.TryConsume();
                return new PasswordOutcome.NoClaim();
            }

            tenantId = oldestTenantId;
            var picked = await Provisioner.ClaimedAdminAsync(dbContext, tenantId, cancellationToken);
            var choice = PasswordPolicy.Check(typedPassword, loginName, [picked?.FirstName, picked?.FullName]);
            if (choice is not PasswordChoice.Accepted accepted)
                return Refuse(((PasswordChoice.Refused)choice).Message);
            if (await hashing.HashAsync(accepted.Password, cancellationToken) is not { } made)
                return Busy();
            hash = made;
        }

        var consumed = new StrongBox<bool>();
        for (var run = 1; ; run++)
        {
            try
            {
                var outcome = await ClaimOwnerOnceAsync(tenantId, loginName, hash, consumed, cancellationToken);
                if (outcome is PasswordOutcome.SignedIn owner)
                {
                    Activity.Current?.SetTag("app.user_id", owner.User.UserId);
                    _logger.Here().Warning(
                        "The owner claim file {ClaimFile} set the password of user {UserId}, gave back each Admin permission, and ended each of its sessions",
                        ownerClaim.FilePath, owner.User.UserId);
                }

                return outcome;
            }
            // Login names are unique in the install and the lock is of one tenant, so another tenant can take the name after the check.
            catch (ConstraintConflictException conflict) when (conflict.Constraint == "ix_auth_password_credentials_login_name")
            {
                _logger.Here().Warning("The owner claim file {ClaimFile} was used, and another user took the login name before the save, so the claim changed nothing", ownerClaim.FilePath);
                return new PasswordOutcome.Refused(StatusCodes.Status409Conflict, LoginName.InUseMessage);
            }
            catch (StaleVersionException) when (run < MaxSignInRuns)
            {
                Telemetry.AddEvent([("app.race.run", run)], "race.lost");
            }
            catch (StaleVersionException)
            {
                Telemetry.AddEvent([("app.race.run", run)], "race.exhausted");
                _logger.Here().Warning("The owner claim file {ClaimFile} was used, and the claim lost {Runs} races in a row and changed nothing", ownerClaim.FilePath, run);
                return Busy();
            }
        }
    }

    /// <summary>
    /// One locked load and save of the owner claim. The first run takes the file after the check of the login name, and
    /// <paramref name="consumed"/> keeps that for a run again. <see cref="PasswordOutcome.NoClaim"/> when the file or the tenant is gone.
    /// </summary>
    /// <exception cref="StaleVersionException">Another write changed the credential, a session, or a login in flight after the load.</exception>
    private async Task<PasswordOutcome> ClaimOwnerOnceAsync(
        string tenantId, LoginName loginName, string hash, StrongBox<bool> consumed, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateSystemScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await RootLock.Tenant.RunExclusiveAsync<PasswordOutcome>(dbContext, tenantId, async ct =>
        {
            var admin = await Provisioner.ClaimedAdminAsync(dbContext, tenantId, ct);
            var adminId = admin?.Id;
            var nameInUse = await dbContext.Set<PasswordCredential>()
                .TagWithOperationCallSite("auth.password.claim_name_in_use")
                .AnyAsync(c => c.LoginName == loginName && c.UserId != adminId, ct);
            if (nameInUse && !consumed.Value)
                return new PasswordOutcome.Refused(StatusCodes.Status409Conflict, LoginName.InUseMessage);

            if (!consumed.Value)
            {
                if (!ownerClaim.TryConsume())
                    return new PasswordOutcome.NoClaim();
                consumed.Value = true;
            }

            if (admin is null)
            {
                if (!await dbContext.Tenants.TagWithOperationCallSite("auth.password.claim_tenant_is_live").AnyAsync(t => t.Id == tenantId, ct))
                    return new PasswordOutcome.NoClaim();

                admin = User.CreateFromLogin(NewPerson.FromClaims(new Profile(null, null, loginName.Value, null, loginName.Value)), tenantId, UserRole.Admin());
                dbContext.Users.Add(admin);
            }

            Provisioner.GiveBackEachPermission(dbContext, admin);
            var now = timeProvider.GetUtcNow();
            var credential = await dbContext.Set<PasswordCredential>()
                .TagWithOperationCallSite("auth.password.claim_credential")
                .FirstOrDefaultAsync(c => c.UserId == admin.Id, ct);
            if (credential is null)
            {
                credential = PasswordCredential.Choose(admin.Id, loginName, hash, now);
                dbContext.Set<PasswordCredential>().Add(credential);
            }
            else
            {
                credential.ClaimForOwner(loginName, hash, now);
            }

            await SessionStore.EndSignInsInSaveAsync(dbContext, admin.Id, null, RevokeReason.OwnerClaim, now, ct);
            dbContext.RecordAction(new OwnerPasswordSet(AuditSubject.Of(admin), loginName.Value));
            await dbContext.SaveChangesAsync(ct);
            return new PasswordOutcome.SignedIn(new ProvisionOutcome.Provisioned(admin.Id, admin.FullName), credential.Guard);
        }, cancellationToken);
    }

    public async Task<PasswordOutcome> SetupAsync(string? code, string? typedName, string? typedLoginName, string? typedPassword, CancellationToken cancellationToken)
    {
        if (await SetupCodeAtBoot.AnyLiveTenantAsync(scopeFactory, cancellationToken))
            return new PasswordOutcome.AlreadySetUp();

        switch (setupCode.Check(code))
        {
            case SetupCodeCheck.Void:
                return new PasswordOutcome.AlreadySetUp();
            case SetupCodeCheck.Wrong:
                _logger.Here().Warning("A setup gave a wrong setup code");
                return new PasswordOutcome.Refused(StatusCodes.Status403Forbidden, WrongCodeMessage);
        }

        var name = typedName?.Trim();
        if (string.IsNullOrEmpty(name) || !StorableText.CanStore(name))
            return Refuse(NameMissingMessage);
        if (!LoginName.TryOf(typedLoginName, out var loginName))
            return Refuse(LoginName.NotValidMessage);

        var person = NewPerson.FromClaims(new Profile(null, null, name, null, loginName.Value));
        var choice = PasswordPolicy.Check(typedPassword, loginName, [name, person.FirstName]);
        if (choice is not PasswordChoice.Accepted accepted)
            return Refuse(((PasswordChoice.Refused)choice).Message);
        if (await hashing.HashAsync(accepted.Password, cancellationToken) is not { } hash)
            return Busy();

        await using var scope = scopeFactory.CreateSystemScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenant = Tenant.Create(new TenantForCreation { Name = FirstTenantName });
        var user = User.CreateFromLogin(person, tenant.Id, UserRole.Admin());
        dbContext.Tenants.Add(tenant);
        dbContext.Users.Add(user);
        var credential = PasswordCredential.Choose(user.Id, loginName, hash, timeProvider.GetUtcNow());
        dbContext.Set<PasswordCredential>().Add(credential);
        dbContext.RecordAction(new PasswordChosen(AuditSubject.Of(user)));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (ConstraintConflictException conflict) when (SetupRaceConstraints.Contains(conflict.Constraint))
        {
            return new PasswordOutcome.AlreadySetUp();
        }

        setupCode.Void();
        Activity.Current?.SetTag("app.user_id", user.Id);
        _logger.Here().Information("The setup made tenant {TenantId} and its Owner {UserId}, and voided the setup code", tenant.Id, user.Id);
        return new PasswordOutcome.SignedIn(new ProvisionOutcome.Provisioned(user.Id, user.FullName), credential.Guard);
    }

    private static PasswordOutcome.Refused Refuse(string message) => new(StatusCodes.Status422UnprocessableEntity, message);

    private static PasswordOutcome.Wait Busy() => new(PasswordHashing.SlotWait, BusyMessage);
}

/// <summary>A person chose a password: at the setup, or after a temporary password (PWD-16). It holds no password and no hash.</summary>
[AuditActionName("password_chosen")]
public sealed record PasswordChosen(AuditSubject Subject) : AuditAction(Subject);

/// <summary>A person changed the password with the current one (PWD-17). It holds no password and no hash.</summary>
[AuditActionName("password_changed")]
public sealed record PasswordChanged(AuditSubject Subject) : AuditAction(Subject);

/// <summary>The operator set the login name and the password of this Admin through the <c>claim-owner</c> file (PWD-19). It holds no password and no hash.</summary>
[AuditActionName("owner_password_set")]
public sealed record OwnerPasswordSet(AuditSubject Subject, [property: Sensitive] string LoginName) : AuditAction(Subject);
