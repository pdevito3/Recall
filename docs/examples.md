# Example code

The template has example code that shows the patterns of the app. An app that you make from the template does not need it. `scripts/remove-examples.cs` deletes all of it in one run:

```bash
dotnet run scripts/remove-examples.cs
dotnet build Recall.slnx
```

Then commit the result. The script changes only a clean git tree. A second run changes nothing. If the script stops before the end, it prints the command that undoes its changes. Correct the cause, undo the changes, and run it again.

The script does these steps:

1. It deletes each tracked folder named `Examples`, `examples`, or `(examples)`.
2. It deletes each section between an `example:start` comment and an `example:end` comment, in all other tracked files.
3. It writes the EF Core model snapshot again, so that the snapshot has no example tables.
4. It writes the generated files again: the OpenAPI document of the Server, the generated API client of the SPA, and `routeTree.gen.ts`.
5. It writes `scripts/examples-removed.txt`. Keep this file. When the app has it, `scripts/template-merge.cs` deletes the examples from each template update, so that a template update adds no example code again. See [Take updates from the template](template-updates.md#an-app-that-deleted-the-examples).

A marker with no partner stops the script before it changes a file. A change to the model of the Server with no migration also stops it, because the script deletes the migration that it adds to write the snapshot. Do not put a marker at the start of a line in a code block of a doc, because the script reads it as a marker.

## Add example code

Put example code where the script finds it:

- Put a file that is only an example in a folder named `Examples` (.NET), `examples` (the SPA and the tests), or `(examples)` (a TanStack Router route group, which adds no segment to the URL).
- Put example lines in a shared file between two comments. Use the comment form of the file: `// example:start` and `// example:end` in C# and TypeScript, `# example:start` in YAML and shell, `<!-- example:start -->` in Markdown, and `{/* example:start */}` in JSX.
- Do not let code outside the examples use example code. After the script runs, that code does not compile.
- Keep each `// new-feature:<slot>` marker of `scripts/new-feature.cs` out of an example section. The new-feature script adds its lines above the marker, also in an app with no examples.

## Run the CI jobs locally

The `new-feature` job of CI is in [scripts/ci/new-feature.cs](../scripts/ci/new-feature.cs). CI runs this script, so a local run does the same steps:

```bash
dotnet run scripts/ci/new-feature.cs
```

The script works on a throwaway copy of the checkout, with your uncommitted changes in the copy. It does not change the checkout. A pass deletes the copy. A failure names the step and keeps the copy, for its logs and test reports. The script needs the .NET SDK band of CI (10.0.4xx), Docker, and buf.
