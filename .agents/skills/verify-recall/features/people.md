# People

A person keeps the People that entries tag at `/people`. Two People can share one name, such as two Janes. Each can hold an optional full name and notes that tell them apart, and a mention in an entry still shows only the name. When a Cleanup finds a name that two or more People have, it tags none of them, and the People proposal on the entry shows the details of each and lets the person choose.

## Sub-features

- `list` shows `Your people`: each name, with the full name and the notes under it when the Person has them. Long notes show three lines.
- `add` adds a Person from the field `Add a person` and the button `Add`.
- `details` opens the dialog `Details of <name>` from the button `Edit the details of <name>`, with `Full name` and `Notes`. `Save details` closes it and shows the toast `Details saved`. A blank field clears the detail. A save after another save shows `This person changed after you opened it.` and `Load the latest`.
- `two of one name` on an entry: the proposal of a name that two or more People have shows `<n> people have this name. Choose the person to tag.`, the list `People named <name>` with the details of each and a `Tag <full name>` button, `Add a new person`, and `Do not tag`. There is no plain `Tag <name>` button.
- `one of one name` on an entry: the proposal shows the details of the one match, `Tag`, `Add a new person`, and `Do not tag`.

## How to get to it (user POV)

- Choose `People` in the sidebar, or open `/people`.
- Open an entry with a People proposal at `/sessions/<id>`, the card `Cleanup`, section `People to tag`.

## Driving it with browse.mjs

Preconditions:

- `doctor.sh` exits with 0.
- The database has no tenant, so `login()` makes an Admin with no People.

- **Add.** Fill `Add a person` with `Jane` and choose `Add`, two times. The list shows `Jane` two times.
- **Details.** Choose the first `Edit the details of Jane`, fill `Full name` with a long name with no spaces and `Notes`, and choose `Save details`. The `PUT /api/v1/people/<id>/details` answer keeps the label `Jane`, and the list shows its details. Do the same for the second Jane with `Jane Doe`.
- **Proposal.** Through the API from the page, create an entry, save its words, and `POST /api/v1/sessions/<id>/cleanup/result` with `peopleProposal: ['Jane']`. The answer has both IDs in `matchedPersonIds` and no `personIds`. Open the entry: `People named Jane` lists both, with their details.
- **Choose.** Choose `Tag Jane Doe`. The answer has `personIds` of that Jane only, and the proposal leaves.

Recipe: `recipes/people.mjs`.

## Gotchas

- Two People of one name have the same `Edit the details of Jane` button. Pick one with `.nth()` in the order of the list.
- A device result needs no model provider, so the recipe does not connect one.
