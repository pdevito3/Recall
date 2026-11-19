# Model providers and Cleanup

An Admin connects a model provider with an API key of their own at `/model-providers`, tests the connection, and picks a model from the list of the provider. A member then runs Cleanup on a Session. The job runs on the server, so the person can close the page and later find Cleaned. Raw does not change.

## Sub-features

- `connect` adds a provider with `Kind`, `Name`, `Base URL`, and `API key`. The form clears, and no page shows the key again.
- `test` shows `Connected.` with the count of the models of the provider, or an alert with the words of the provider.
- `model` shows the select `Model of <name>` after a test. A pick saves the model.
- `active` shows the badge `Active` only on the provider that an Admin chose with `Make active`. A connect never makes a provider active, also the first provider of a tenant. See [The active provider](../../../../docs/backend/model-providers.md#the-active-provider).
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

- **Connect.** Choose `Model providers`. Fill `Name`, `Base URL`, and `API key`. Choose `Connect`. The level 3 heading of the provider shows, and `API key` is empty. The provider is the first of the tenant, and it has no badge `Active`. The card says `A Cleanup does not use this model provider. Pick a model, then make it active.` A sweep gives each name in an answer a long value, so the recipe reads the name of the heading from the answer to `Connect`, and finds each card by the id of its provider.
- **Model.** Choose `Test connection` on the provider. The status starts with `Connected.`. Choose `Model of <name>`, then an option. The hint `Test the connection, then pick a model.` goes away. The provider still has no badge `Active`, and the card says `A Cleanup does not use this model provider until you make it active.` The list of `/api/v1/model-providers` has no active provider, and `/api/v1/model-providers/active` gives no name.
- **Make active.** Choose `Make active`. The badge `Active` shows, and the button goes away. The list of `/api/v1/model-providers` gives this provider as active, and `/api/v1/model-providers/active` gives a name.
- **Second provider.** Connect a second provider with the same key, test it, and pick a model. The second provider has no badge `Active`, and the first provider keeps it. The list still gives the first provider as active.
- **Bad key.** Connect a third provider with another key, and choose `Test connection`. The alert holds the words of the fake, and the card says that the provider needs a model.
- **Cleanup.** Open `/sessions/<id>`, and choose `Run Cleanup` in the `Cleanup` section. The status shows `You can close this page`, then `Cleaned is current.` with no reload. The `Cleaned` tab shows the result. A reload shows the same status, and `Your words` still shows the words of the draft.
- **Proof.** Run `node $S/scripts/browse.mjs $S/recipes/model-providers.mjs`. The captures `connected`, `tested`, `second-provider`, `bad-key`, `running`, and `cleaned` show each state.

## Gotchas

- `POST /api/v1/sessions/{id}/cleanup` only queues the job. Wait for the status on the page, not for the response.
- The fake has one behavior for all callers. A recipe that sets a behavior sets `Normal` again before its next step.
- Do not put an API key in a file or in a capture. The recipe makes each key at run time. The form clears the key after a connect, so a capture holds no key.
