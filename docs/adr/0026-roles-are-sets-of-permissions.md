# Roles are sets of permissions

A user had one role, `Admin` or `User`. The role mattered only when it was set: `User.Create` and `UpdateRole` copied its default permissions into `user_permissions` rows, and from then on the rows decided. A role change reset each permission that an admin had added or removed by hand, and each new permission needed a migration that gave it to each Admin as a row. Now a user has any number of roles, each role is a named set of permissions in code, and a row of `user_permissions` is an override on top of the roles.

## Decisions

- **The permissions of a user are one rule.** `EffectivePermissions`: each permission of each role, with each `Grant`, less each `Deny`. `User.Permissions()` applies it, and `PermissionGuard` gives the result to `Caller`. `Caller`, the grants of agents, and the grants of personal access tokens do not change.
- **Roles live in code, not in a table.** `Admin` gives each permission, also one that a later release adds. `UserManager` gives `manage_users`, `manage_user_access`, and `read_users`. `Auditor` gives `read_audit_log`, `read_audit_log_sensitive`, `read_users`, and `read_agents`. A tenant admin cannot edit a role.
- **The role `User` is gone.** It gave no permission. A user with no role is a Member. The journal needs no permission (ADR 0020), so no role and no override can take it away, and an admin cannot lock a person out of their own data.
- **An override is kept only when it differs from the roles.** An add of a permission that a role gives ends its `Deny` and writes no `Grant`. A removal of a permission that only a `Grant` gives deletes the row and writes no `Deny`. A user has at most one live override for each permission.
- **An add of a role ends each override of a permission of that role.** The user then holds each permission of the role, which is what an admin who adds the role means. A `Deny` of a permission that the role does not give stays, because the caller only had to hold the permissions of the role.
- **The rule that a caller gives only what it holds carries over.** `AddUserRole` requires each permission of the role. `RemoveUserRole`, like the old role change, requires each permission of the user, so a caller cannot take access from a user who holds more.
- **The last-Admin rule reads the role.** A full Admin has the role `Admin` and no `Deny`. Since `Admin` gives each permission, this is the same as the old count of rows, and it stays true when a release adds a permission. The Owner is the oldest user with the role `Admin`.

## Considered options

- **Keep one role and add the overrides.** We did not choose this option. A person who audits and manages users needs a role for each pair, and the number of roles grows with each combination.
- **Copy the permissions of each role into rows, as before.** We did not choose this option. A change of the set of a role would need a migration for each user, and a removal of a role could not tell which rows came from it.
- **Roles in a table that admins edit.** We did not choose this option yet. It needs pages, a migration of each change, and a rule that a caller cannot make a role stronger than itself. Roles in code cover the need now.
- **An add of a role ends each `Deny` of the user.** We did not choose this option. It would give back permissions that the role does not give and the caller may not hold.

## Consequences

- The migration `ReplaceTheRoleWithRoleSets` keeps the permissions that each user held: each Admin gets the role `Admin` and a `Deny` for each permission that the Admin lacked, the rows of an Admin are deleted, and each former `User` keeps their rows as `Grant`s. It drops `users.role` in place, because the EF rebuild of `users` would drop the trigger `users_purge_journal`.
- A new permission needs no migration for the Admins. `GrantPermissionToRole` reads `users.role`, so only a migration before `ReplaceTheRoleWithRoleSets` can call it.
- `PUT /users/{id}/role` is replaced by `POST /users/{id}/roles` and `DELETE /users/{id}/roles/{role}`. `UserDto` has `roles`, `permissions` (the effective set), `addedPermissions`, `removedPermissions`, and `addableRoles`: the roles that the user lacks and the caller holds each permission of, so the Add role dialog offers only roles that the Server accepts. A create request takes `roles`.
- The user list computes `permissions` in code after the page loads, so a filter or a sort on `Permissions` or on a role no longer runs in SQL.
- Open signup adds a person with no role.
