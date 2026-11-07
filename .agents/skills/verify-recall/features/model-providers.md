# Model providers and Cleanup

An Admin connects a model provider with an API key of their own at `/model-providers`, tests the connection, and picks a model from the list of the provider. A member then runs Cleanup on a Session. The job runs on the server, so the person can close the page and later find Cleaned. Raw does not change.

## Sub-features

- `connect` adds a provider with `Kind`, `Name`, `Base URL`, and `API key`. The form clears, and no page shows the key again.
- `test` shows `Connected.` with the count of the models of the provider, or an alert with the words of the provider.
- `model` shows the select `Model of <name>` after a test. A pick saves the model and makes the provider ready.
- `key-ring` shows an alert when the key ring of the server changed or takes no write, and `The server cannot read this key. Enter it again.` on the provider.
- `no-admin` shows `An Admin connects the model provider of this install.` to a member with no Admin role.
- `cleanup` shows the section `Cleanup` on the page of an entry, between History and Tags, with its status, the line `A Cleanup sends your words to <name>.`, the button `Run Cleanup`, the suggestions, and the people to tag. The result shows in the `Cleaned` tab of the editor with no reload.
- `background` keeps the job running after the page closes. A later visit shows `Cleaned is current.`.

## How to get to it (user POV)

- Choose `Model providers` in the sidebar.
- For a Cleanup, choose an entry in the Journal, or open `/sessions/<id>` directly.

## Driving it with browse.mjs

Preconditions:

- `doctor.sh` exits with 0.
- The database has no tenant, so `login()` makes an Admin.
- The recipe starts `tests/Recall.FakeModelProvider` with a key that it makes at run time, and stops it at the end. Build the solution first. No real provider and no real key are necessary.
- The SPA has no page to write a Session in this map yet. The recipe adds a Session and its draft with `fetch` in the page.

- **Connect.** Choose `Model providers`. Fill `Name`, `Base URL`, and `API key`. Choose `Connect`. The level 3 heading of the provider shows, and `API key` is empty.
- **Test.** Choose `Test connection` on the provider. The status starts with `Connected.`.
- **Model.** Choose `Model of <name>`, then an option. The hint `Test the connection, then pick a model.` goes away.
- **Bad key.** Connect a second provider with another key, and choose `Test connection`. The alert holds the words of the fake. The newest provider is active, so choose `Make active` on the first provider before a Cleanup. If you do not, `Run Cleanup` gets a 422 because the active provider has no model.
- **Cleanup.** Open `/sessions/<id>`, and choose `Run Cleanup` in the `Cleanup` section. The status shows `You can close this page`, then `Cleaned is current.` with no reload. The `Cleaned` tab shows the result. A reload shows the same status, and `Your words` still shows the words of the draft.
- **Proof.** Run `node $S/scripts/browse.mjs $S/recipes/model-providers.mjs`. The captures `connected`, `tested`, `bad-key`, `running`, and `cleaned` show each state. The recipe did not run after the Cleanup moved to the entry page, so check its steps on the first run.

## Gotchas

- `POST /api/v1/sessions/{id}/cleanup` only queues the job. Wait for the status on the page, not for the response.
- The fake has one behavior for all callers. A recipe that sets a behavior sets `Normal` again before its next step.
- Do not put an API key in a file or in a capture. The recipe makes each key at run time. The form clears the key after a connect, so a capture holds no key.
