# The operator ruled on pricing, the API, and the repository

The operator gave these rulings on 2026-11-05. ADRs 0013 to 0017 do not record them, so this ADR does. The quotes keep the words of the operator as typed. Each "agent reading" is an interpretation of an agent and not a ruling.

The first ruling, which also covers SQLite and the Session hierarchy:

> agree sqlite. layment is oke tkme purchase. after trial they can read only in the ios app. hierechy meant that session is the root that yiu can have multiple across a day and summaries rollup as needed. sjouldnt need a change yes agree with 4 for repo names

The second ruling, on the API:

> id say keep the api whwre it makes sense but at the end of the daybwe shoild be doing what we want for our own patterns and they should generally end up close enough to not be a big lift even if they dont match in most cases so focus on following our oatterns.

## The rulings

- **One-time purchase.** "layment is oke tkme purchase". The agent reading is a one-time purchase after a 30-day trial.
- **Read-only after the trial.** "after trial they can read only in the ios app". The agent reading is that the iOS app is read-only after the trial. Two parts are agent assumptions: export stays on because it writes no journal data, and the web app has no limit.
- **The API follows Recall patterns.** "focus on following our oatterns". A POC name or shape stays only where it fits those patterns.
- **Repository names.** "yes agree with 4 for repo names". The agent reading is a new private `pdevito3/Recall`.

## Consequences

- The plan's R16 builds the purchase and the read-only state in the iOS app.
- The iOS client adapts to the API in R14, and R8 lists the real differences from the POC.
