namespace Recall.Server.Auth;

using System.Diagnostics;
using System.Net;
using Databases;
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

    public async Task<PasswordOutcome> SignInAsync(string? typedName, string? typedPassword, IPAddress? address, CancellationToken cancellationToken)
    {
        // A wait runs no hash, so a stranger who guesses pays nothing and gets nothing.
        if (backoff.WaitLeft(typedName, address) is { } left)
            return new PasswordOutcome.Wait(left, TooManyTriesMessage(left));

        var password = PasswordPolicy.Normalize(typedPassword) ?? "";
        for (var run = 1; ; run++)
        {
            try
            {
                var (outcome, wrong) = await SignInOnceAsync(typedName, password, cancellationToken);
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
    private async Task<(PasswordOutcome Outcome, bool Wrong)> SignInOnceAsync(string? typedName, string password, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateSystemScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var credential = LoginName.TryOf(typedName, out var name)
            ? await dbContext.Set<PasswordCredential>()
                .TagWithOperationCallSite("auth.password.find_credential")
                .FirstOrDefaultAsync(p => p.LoginName == name, cancellationToken)
            : null;

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
