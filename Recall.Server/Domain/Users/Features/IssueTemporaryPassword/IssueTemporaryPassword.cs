namespace Recall.Server.Domain.Users.Features;

using System.Diagnostics;
using AuditEntries;
using Auth;
using Auth.Entities;
using Databases;
using Dtos;
using Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Resources.Entrypoints.Auth;
using Serilog;

public static class IssueTemporaryPassword
{
    public const string LoginNameNeededMessage = "Give the person a login name. They type it to sign in.";

    /// <summary>
    /// A sign-in or a choice can save the credential between the load and the save of an issue, so the issue runs again
    /// on the new row. It sends no version, because the Admin decided nothing on the state of the credential.
    /// </summary>
    public sealed record Command(string Id, string? LoginName) : IRequest<TemporaryPasswordDto>, IRunsAgainWhenRaced;

    public sealed class Handler(
        AppDbContext dbContext, IPermissionGuard permissionGuard, ICurrentUserService currentUser, PasswordHashing hashing, TimeProvider timeProvider)
        : IRequestHandler<Command, TemporaryPasswordDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<TemporaryPasswordDto> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.user_id", request.Id);

            var caller = await permissionGuard.RequireSignedInPersonAsync(cancellationToken);
            UserAction.IssueTemporaryPassword().GuardBeforeLoad(caller, request.Id);

            // The hash runs before the lock, so the exclusive tenant lock is not held for the cost of a hash.
            var password = TemporaryPassword.New();
            var hash = await hashing.HashAsync(password, cancellationToken)
                ?? throw new ServerBusyException(PasswordLogin.BusyMessage, PasswordHashing.SlotWait);

            // The exclusive lock keeps a delete of the user from saving between the load and the save, which would
            // leave a credential of a deleted user, and it keeps two first issues from adding two rows.
            var issued = await LastAdmin.RunWithTenantLockAsync(dbContext, request.Id, async ct =>
            {
                var user = await dbContext.Users
                    .TagWithOperationCallSite("users.issue_temporary_password.load")
                    .Include(u => u.UserPermissions)
                    .Include(u => u.Roles)
                    .GetById(request.Id, ct);
                user.GuardAllows(UserAction.IssueTemporaryPassword(), caller);

                var credential = await dbContext.Set<PasswordCredential>()
                    .TagWithOperationCallSite("users.issue_temporary_password.credential")
                    .FirstOrDefaultAsync(c => c.UserId == user.Id, ct);
                var now = timeProvider.GetUtcNow();
                if (credential is null)
                {
                    credential = PasswordCredential.IssueTemporary(user.Id, LoginNameOf(request.LoginName), hash, now);
                    dbContext.Set<PasswordCredential>().Add(credential);
                }
                else
                {
                    credential.IssueTemporary(hash, now);
                }

                // A caller who issues to themselves keeps the session of this request, as a change of a password does (PWD-17).
                var keep = caller.UserId == user.Id ? currentUser.GetClaimValue(AppSessionHandler.SessionIdClaim) : null;
                await SessionStore.EndSignInsInSaveAsync(dbContext, user.Id, keep, RevokeReason.TemporaryPassword, now, ct);

                var expiresAt = credential.TemporaryUntil!.Value;
                dbContext.RecordAction(new TemporaryPasswordIssued(AuditSubject.Of(user), credential.LoginName.Value, expiresAt));
                // One save writes the credential, the end of each session, and the delete of each login in flight, so no
                // session or choice can start from the old password after the issue commits.
                await dbContext.SaveChangesAsync(ct);
                return new TemporaryPasswordDto { LoginName = credential.LoginName.Value, Password = password, ExpiresAt = expiresAt };
            }, cancellationToken);

            _logger.Here().Information("Issued a temporary password to user {UserId} and ended each of its other sessions", request.Id);
            return issued;
        }

        private static LoginName LoginNameOf(string? typed)
        {
            ValidationException.MustNot(string.IsNullOrWhiteSpace(typed), "loginName", LoginNameNeededMessage);
            return LoginName.TryOf(typed, out var name) ? name : throw new ValidationException("loginName", LoginName.NotValidMessage);
        }

    }
}
