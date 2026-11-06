# Recall

A self-hosted journal. A person opens the phone, dictates, and the entry is captured with no friction. AI can make a polished copy of the words, and it never changes the words of the person.

## Language

A rule that ends in _(agent default)_ is a choice of an agent, not a ruling of the operator. The operator can change it.

### Journal

**Session**:
The root of the journal: one act of journaling by a User. A Session has no parent, and a Journaling day holds any number of Sessions. A bare "Session" always means this. The record of a login is always an Auth session.
_Avoid_: Entry (the Session is the entry)

**Journaling day**:
The calendar day of a Session in the time zone of its User. A Session stores absolute UTC. The Journaling day is a grouping that the app derives from that time, and it is not a record.
_Avoid_: Date, parent day

**Raw**:
The words that the User wrote or dictated. It is the source of truth that the User owns. The User can edit it at any time, and AI never changes it.
_Avoid_: Original, transcript

**Cleaned**:
The polished copy of Raw that Cleanup makes. The User can also edit it by hand. A new Cleanup replaces it, and the prior copy stays as a Revision. If the copy has edits by hand, Cleanup warns the User first.
_Avoid_: Edited, final, processed

**Draft**:
The current, autosaved working text of Raw or of Cleaned. A Draft changes, and a Revision does not.

**Revision**:
An append-only snapshot of Raw or of Cleaned, made at a save point. Each change to the text makes one, and that includes the change that a Person rename makes _(agent default)_. Revisions are history inside one Session, and search reads only the current state.
_Avoid_: Version (the concurrency token of a row), history entry

**Last-write-wins**:
The rule for two saves of one Draft. The later save time holds the Draft, and the earlier save stays as a Revision, so no words go away.
_Avoid_: Conflict, merge

**Stale**:
The state of a Session whose current Raw Draft is at a newer Revision than the Revision that the last successful Cleanup read. It has this one definition. An edit of Cleaned by hand and a save that lost Last-write-wins do not make a Session stale. A Person rename also does not. If the rename makes a Raw Revision on a Session that is not stale, the Revision that the last Cleanup read moves to the new Revision. A stale Session stays stale. These three rules on a rename are one choice _(agent default)_.
_Avoid_: Dirty, outdated

**Client session ID**:
The ID that a client makes for a Session before it has a connection, so a replay of the create finds the same Session. It is unique for one User. It is not an OAuth client ID.

**Sync feed**:
The ordered list of each change in the journal of one User. A client pulls it from a cursor and gets each changed row once, in its newest state.
_Avoid_: Change log (the Audit log records who changed what)

**Tombstone**:
The line that the Sync feed keeps for a deleted row. It holds the IDs of the row and no words.
_Avoid_: Soft delete (the deleted words are gone)

### AI

**Cleanup**:
The work that reads Raw and makes Cleaned, the Synopsis, the Suggestions, and the People proposals. A person starts it at any time. On the `Server` engine, it continues after the client closes. A Session has no lifecycle, so Cleanup never completes a Session.
_Avoid_: Processing, evaluation

**Cleanup status**:
Where a Session stands with Cleanup: `NotRun`, `Running`, `Clean`, `Stale`, or `Failed`. `Stale` comes from the definition of Stale and is never stored.

**Cleanup engine**:
Where the model of a Cleanup runs: `Server`, or `OnDevice` on the iPhone of the User. The User picks a default engine and can override it for one run. The engine does not change what a Cleanup is or what it makes.
_Avoid_: Runner, provider

**Model provider**:
The AI service that an Admin connects for the app. The `Server` engine uses it.
_Avoid_: Bare "provider", which can also mean an Identity provider

**Transcription engine**:
What turns audio into Raw: the server, a cloud model, or a speech model on the device. A cloud model is an engine only for a provider that accepts audio, and which providers accept audio is unconfirmed until R11. It is not a Cleanup engine.
_Avoid_: Recognizer, bare "engine"

**Synopsis**:
The short AI recap of one Session. A Summary covers a period, and a Synopsis does not. A Person rename does not change a Synopsis, so it can keep the old name _(agent default)_.
_Avoid_: Recap, abstract

**Correction**:
A term of one User, with its common mishearings, that fixes dictation mistakes in Cleaned. By default it is a hint to the model, and a User can flag it to replace the words each time. It never changes Raw.
_Avoid_: Lexicon, vocabulary, dictionary

### Metadata

**Topic**:
A life-area tag on a Session, such as work or trips. Each User has their own Topics, and a Session can have many. A Topic is the only metadata with Provenance.

**Provenance**:
Who set a Topic: `UserSet` or `AiSuggested`. AI never replaces a `UserSet` Topic.
_Avoid_: Source, origin

**Mood**:
How the User felt: a known mood or the free text of the User. A Session can have many Moods, and a Mood has no Provenance.

**Activity**:
What the User physically did while journaling: a known activity or the free text of the User. A Session has exactly one, and only the User sets it. The value `None` means that the User did not say, and there is no other unset state. Life areas are Topics, and feelings are Moods.
_Avoid_: Unset, null activity

**Person**:
A record in the directory of one User. A mention in the text refers to the Person, and the People of a Session are the Persons that its Raw and its Cleaned mention. A rename changes the label of each mention and makes a Revision of each text that changes _(agent default)_.
_Avoid_: Contact, person tag

**Suggestion**:
A Topic or a Mood that AI proposes, which the User accepts or rejects. It is not metadata until the User accepts it. A Person is never a Suggestion.

**People proposal**:
A name that Cleanup proposes as a mention of a Person, which the User approves or rejects one Person at a time. An approval adds the mention to Cleaned. A User can turn off this review, and then Cleanup adds the mentions itself.
_Avoid_: People suggestion

**Location**:
An optional single point (latitude and longitude) on a Session from its creation. The User opts in, and the default is off. The User can decline it for one Session.
_Avoid_: Place, address

### Summaries

**Summary**:
An AI narrative of one User over one period: `Day`, `Week`, `Month`, `Quarter`, or `Year`. The User, the period, and the date are the key of a Summary. Summaries roll up as needed. A period with no sources has no Summary.
_Avoid_: Report, digest

**Roll-up**:
The sources of a Summary. Day and Week read Sessions, and each reads Cleaned when it is present and Raw when it is not. Month reads Day Summaries, Quarter reads Months, and Year reads Quarters. A Week can cross two Months, so it is outside that chain. _(agent default)_

**Stale summary**:
A Summary whose sources changed after it was made. A Person rename counts as a change, so that a Summary does not keep the old name _(agent default)_. The change goes up the chain to each Summary that reads it. A missing or stale Summary generates when a person opens its period, and a nightly job handles the stale periods _(agent default)_.

### Identity

**User**:
A person with a login, and the owner of their journal data. Recall keeps one seeded tenant, so a tenant does not separate one person from another. All journal data belongs to exactly one User.
_Avoid_: Account

**Admin**:
A User who manages users, app-wide settings, and system health. An Admin has no access to the journal of another User.
_Avoid_: Superuser

**Member**:
A User with full use of their own journal and no admin pages. The code names this role `User`.

**Owner**:
The person whose first login on a new install created its tenant. An Owner is an Admin. Control of the data directory can move this place to another identity.
_Avoid_: Root, superadmin, first user

**Privacy invariant**:
No User, an Admin included, can read the journal data of another User.

### Authentication

**Auth session**:
The record at the Server of one signed-in device, a browser or a native app. A person can have several. It keeps working when the identity provider is down.
_Avoid_: Cookie session, user session, login, BFF session

**IdP session**:
The user's signed-in state at the identity provider. It proves who a person is at login. Then it has no link to an auth session, so one can end and the other stays.
_Avoid_: SSO session, provider session

**Identity provider**:
The external OpenID Connect server that proves who a person is at login, for example Keycloak.
An install has any number of them, or none yet.
_Avoid_: OP, IdP server

### Concurrency

**Version**:
An integer column of the row of an aggregate root. The save sets it to 1 on an add and adds 1 on each update. It is opaque, so compare it only for equality. A change to an owned child also gives the root a new version.
_Avoid_: Row version, ETag, revision

**Stale version**:
A change that the caller based on a version that is no longer current. The caller loads the record again and repeats the change.
_Avoid_: Concurrency conflict, already exists

**Run again**:
What the server does with a command that sends no version when another save changed its aggregate first. The command runs again on the new rows. See `IRunsAgainWhenRaced`.
_Avoid_: Retry, replay

**Concurrent change**:
A command with no version that lost the race on each run again. The caller can send the same request again.
_Avoid_: Stale version, timeout

### Agents

**First-party client**:
A client of this app: an auth session, or Swagger UI. A user who signs in through it acts with all of their own permissions.
_Avoid_: App client, main client

**Agent**:
An OAuth client other than the first-party client that a tenant lets act in the tenant, with a JWT from the provider at `Auth:Authority`. It is either a delegated agent or a machine agent. The API and `/mcp` take its token as a bearer token.
_Avoid_: Bot, integration, app

**Delegated agent**:
An agent that acts for a signed-in user of its tenant. It holds only the permissions that both the user and its grant have.
_Avoid_: Assistant, on-behalf-of client

**Machine agent**:
An agent that acts for itself in one tenant, with no user. It holds its grant. Only the operator binds a client to a tenant as a machine agent.
_Avoid_: Service account, machine client

**Grant**:
The permissions that a tenant gives an agent. It is the most that the agent can hold. Nobody can grant a permission that they do not hold.
_Avoid_: Scope, consent

**All-tenants client**:
An OAuth client that the operator trusts to read, or to manage, the data of all tenants, with a JWT from the provider at `Auth:Authority`. It acts for itself, with no user. It is the one caller of `POST /api/v1/users/initiate`.
_Avoid_: Admin client, super client

**Unknown client**:
An OAuth client that is not the first-party client, not an all-tenants client, and not an agent for the token. It sees no data and holds no permission.
_Avoid_: Unregistered agent, guest

### Decisions

**Decision shape**:
One load into a case record, one pure decision by an ordered list of rules, and one save. A rule set with two callers, or a rule that reads facts from more than one aggregate, uses it.
_Avoid_: Decision pattern, rule engine

**Case record**:
A record that holds each fact that one rule set reads. One loader fills it, and each caller of the rules uses that loader.
_Avoid_: Context, snapshot, request model

**Refusal**:
The answer of a rule that blocks a change. It is one kind of a closed set, with the request field and the message, and the handler maps each kind to an exception.
_Avoid_: Error code, failure reason

**Proof type**:
A type that only the decision makes, which the write method takes, so the write cannot run without the check.
_Avoid_: Token, ticket, witness

### Files

**File store**:
The folder on the local disk that keeps the bytes of files, such as audio. A write ends with an atomic rename, so a reader sees a whole file or no file while the root is one file system.
_Avoid_: Object storage, bucket, blob store

**File key**:
The relative path of one file or one folder in the file store. A value with a segment that climbs out of the root of the store cannot be a file key. All the files of a tenant are under the folder `tenants/{tenantId}`, and a tenant delete deletes that folder.
_Avoid_: Object key, storage key
