# Recall takes template fixes by cherry-pick and does not merge the template

Recall started from [FullstackTemplate](https://github.com/pdevito3/FullstackTemplate) at commit `e1cffd29052419331957407033c38ec71f62800d`. From that commit, Recall follows the template only by a cherry-pick of a fix that it wants.

```bash
git remote add template https://github.com/pdevito3/FullstackTemplate.git
git remote set-url --push template no-push
git fetch template
git log --oneline e1cffd29052419331957407033c38ec71f62800d..template/main
git cherry-pick -x <commit>
git ls-files | grep -iE 'fullstack-?template'
git grep -ilE 'fullstack[- ]?template' -- ':!*.md'
```

A clone does not bring the remote, so add it once in each new clone. It has push disabled. `-x` records the template commit in the message.

A clean cherry-pick is not proof. A new file in a new template folder lands at its old path, and a new line in an existing file keeps the old name, with no conflict. The solution does not list the old folder, so that code does not build, and no build error tells you. A URN with the old name compiles, and it no longer matches the renamed values. The last two commands find each form of the name that `init.cs` renames, and they must print nothing. Move each file that they name to its `Recall` path, and change the template name to `Recall`. They skip Markdown, because some docs name the template on purpose, so read the Markdown lines of the pick by hand. Then run the checks in `AGENTS.md`.

## Why not the merge script

[template-updates.md](../template-updates.md) describes `scripts/template-merge.cs`, which renames both sides and then merges. That script fits an app that stays on the template stack. Recall does not stay on it.

- [ADR 0015](0015-sqlite-storage.md) forks the data layer, and that fork alone modifies 31 template app files and 12 test files. ADRs [0013](0013-one-container-hosting.md) and [0014](0014-app-owned-identity.md) delete the BFF and replace auth. A merge would bring each template change to that code back as a conflict on a file that Recall rewrote or deleted.
- The template moves fast. It has 211 commits from 2026-10-19 to 2026-11-05, so a merge is a large job each time.
- The script needs its pull request to land as a merge commit, because a squash removes the template commit from the history of the app. A cherry-pick can land in any way.

The script stays available from `template/main`, and `scripts/examples-removed.txt` stays so that it still works. Use it only if a later owner measures that a merge costs less than the cherry-picks. `init.cs` deleted the script from this repository. R0 then deleted `init.cs`, because the rename is done and the script runs the `init.cs` of the template commit, not a copy in the app.

## Evidence

Three probes ran on the renamed branch in a throwaway worktree, and nothing from them stays.

- `git revert -n e1cffd2` uses the same three-way merge as a cherry-pick. Git followed each renamed path. Seven of the eight files merged with no conflict. The eighth conflicted, because the revert removes `PlatformReferences.cs`, a file in which `init.cs` changed the namespace.
- A commit on the template base added a new file in a new folder and a line with the template name in `UserRole.cs`. `git cherry-pick -x` reported no conflict, and the two name checks above named both files.
- A commit on the template base added a file at the old verify skill path, a `urn:fullstacktemplate:problem:*` line, and a file in an existing folder. The first two picked with no conflict, and only the name checks above named them. The third gave `CONFLICT (file location)`.

These probes do not show the cost after R1, R3, and R4, when the code differs more.

## Consequences

- Nothing tells Recall about a security fix in the template. A person must read the template log to find one.
- A new file in an existing template folder gives `CONFLICT (file location)`, and Git stages it at the `Recall` path with the old namespace. Keep that path, and change the namespace.
- A template file that Recall deleted gives a conflict that says `deleted by us`. If the fix applies to code that moved, make the same change at the new place.
