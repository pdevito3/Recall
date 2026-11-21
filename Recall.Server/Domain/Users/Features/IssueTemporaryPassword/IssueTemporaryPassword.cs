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
using Serilog;

public static class IssueTemporaryPassword
{
    public const string LoginNameNeededMessage = "Give the person a login name. They type it to sign in.";

    public sealed record Command(string Id, string? LoginName) : IRequest<TemporaryPasswordDto>;

    public sealed class Handler(AppDbContext dbContext, IPermissionGuard permissionGuard, SessionStore store, PasswordHashing hashing, TimeProvider timeProvider)
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
                ?? throw new ServerBusyException(PasswordLogin.BusyMessage);

            // The exclusive lock keeps a delete of the user from saving between the load and the save, which would
            // leave a credential of a deleted user, and it keeps two first issues from adding two rows.
            var issued = await LastAdmin.RunWithTenantLockAsync(dbContext, request.Id, async ct =>
            {
                var user = await dbContext.Users
                    .TagWithOperationCallSite("users.issue_temporary_password.load")
                    .Include(u => u.UserPermissions)
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

                var expiresAt = credential.TemporaryUntil!.Value;
                dbContext.RecordAction(new TemporaryPasswordIssued(AuditSubject.Of(user), credential.LoginName.Value, expiresAt));
                await dbContext.SaveChangesAsync(ct);
                await store.RevokeAllAsync(user.Id, RevokeReason.TemporaryPassword);
                return new TemporaryPasswordDto { LoginName = credential.LoginName.Value, Password = password, ExpiresAt = expiresAt };
            }, cancellationToken);

            _logger.Here().Information("Issued a temporary password to user {UserId} and ended each of its sessions", request.Id);
            return issued;
        }

        private static LoginName LoginNameOf(string? typed)
        {
            ValidationException.MustNot(string.IsNullOrWhiteSpace(typed), "loginName", LoginNameNeededMessage);
            return LoginName.TryOf(typed, out var name) ? name : throw new ValidationException("loginName", LoginName.NotValidMessage);
        }

    }
}
