# ClubRuns Plan

## Product name

Working product name: `ClubRuns`.

Why:
- descriptive
- easy to understand without explanation
- suitable for Telegram bot, web app, and future SaaS positioning

Alternative names worth keeping in reserve:
- `RunClub`
- `ClubPace`
- `RunRoster`

Current recommendation:
- use `ClubRuns` as the main product name
- keep naming clear and functional

## Product direction

ClubRuns should be a single multi-tenant platform for many clubs, not a separate bot/app instance per club.

Target shape:
- one backend
- one database
- one Telegram bot
- one Strava application
- many clubs inside the same platform

Inside the platform:
- clubs
- club members
- recurring run templates
- generated event instances
- event registrations
- event attendance/results
- notifications

## Hosting and callback URL

Strava OAuth requires a callback URL.

What it is:
- user clicks connect in Telegram
- browser opens Strava OAuth page
- after approval Strava redirects back to ClubRuns
- ClubRuns receives the OAuth `code` on `/strava/callback`

Examples:
- local: `http://localhost:5099/strava/callback`
- production: `https://your-domain/strava/callback`

Important rule:
- Strava application settings and ClubRuns config must use the same callback URL

Where this value exists:
- in Strava app settings
- in ClubRuns config as `Strava.RedirectUri` or environment variable `Strava__RedirectUri`

About `your-domain`:
- it does not exist yet
- it will appear later when hosting is chosen
- after choosing hosting, we will get a public domain or service URL and use it here

## Architecture decisions to keep

These decisions should stay as the baseline:

1. One Strava application for the whole platform
- one `client_id`
- one `client_secret`
- one callback URL
- each user has their own `access_token`, `refresh_token`, `expires_at`

2. One Telegram bot for the whole platform
- club context should be resolved in bot flows
- later we may add multiple bot tokens for white-label mode

3. Club settings should live in the database
- not in `appsettings.json`
- schedules, texts, report chats, branding, feature flags should be runtime-configurable

4. Scheduler should be event-driven, not hardcoded
- process upcoming events based on stored schedule
- avoid club-specific hardcoded logic

5. Keep service boundaries clean
- Telegram handling
- Strava integration
- attendance matching
- rate-limited Strava request scheduling
- data access

## Time model

Current code assumes `Asia/Tbilisi`.
That is temporary and must be generalized.

Decision:
- each club must have its own `TimeZoneId`
- recurring event templates store local schedule in club time
- event instances are generated in local club time
- actual timestamps from Strava and stored attendance must be kept as absolute time (`DateTimeOffset` / UTC-capable values)

Storage rules:
- template schedule:
  - `DayOfWeek`
  - `WindowStartLocal`
  - `WindowEndLocal`
  - `TargetStartLocal`
  - `TimeZoneId`
- real event instance:
  - `StartsAtUtc`
  - `WindowStartUtc`
  - `WindowEndUtc`
- real activity match:
  - store absolute timestamp from activity as `DateTimeOffset`

Display rule:
- show times to users in club-local time
- calculate internally using absolute timestamps plus timezone conversion

## Location model

Decision:
- store run/event place as coordinates + radius
- admin UX should support Telegram location pin in addition to manual `lat,lng`

Storage:
- `StartLat`
- `StartLng`
- `RadiusKm`

UX target:
- accept either Telegram map location or typed coordinates

## MVP scope

Core MVP:
- Telegram `/start`
- Strava OAuth connect
- token refresh before API calls
- create/edit clubs and recurring runs from the bot
- register users for a specific event
- attendance matching by time + location + activity type
- attendance report in Telegram chat
- attendance history in DB

Recommended additions to MVP:
- user self-service commands:
  - `/myprofile`
  - `/join_run`
  - `/leave_run`
  - `/results`
- manual confirmation fallback for misses:
  - `/confirm`

## Important product correction

Club membership and event participation must be separate things.

Do not use:
- "all club members are checked for every run"

Use:
- club has many members
- recurring run template belongs to a club
- real event instance is created for a concrete date
- each event has registered participants
- attendance job checks only registered participants

Reason:
- this is required for Strava API limits
- it also matches real club behavior better

## Strava limits and what they mean

Current Strava app limits:
- overall: `200 requests / 15 min`, `2000 / day`
- read: `100 requests / 15 min`, `1000 / day`

Implications:
- checking 10-30 participants for one event is fine
- checking 100+ users in a single pass becomes risky
- checking all club members is not scalable

Required policy:
- never poll all club members by default
- check only registered participants
- cache already matched activities
- do not re-check a user after a confirmed match for the same event unless needed

## Strava request scheduling

Webhooks are postponed.
For now we need controlled polling with strict rate limiting.

Decision:
- introduce `StravaRequestScheduler`
- all Strava read requests should go through it
- it must guarantee we do not exceed `100 read requests / 15 minutes`

Expected behavior:
- enqueue user activity checks
- execute requests in FIFO or small-batch order
- track read requests used in the current 15-minute window
- when window limit is reached, stop sending and wait until the next window
- resume automatically
- persist enough event/user progress so the job can continue safely

What Polly can do:
- retries for transient HTTP failures
- backoff for `429` or network issues

What Polly cannot solve:
- product-level scheduling of hundreds of user checks within Strava quota

So:
- Polly may still be useful inside the HTTP client
- but rate-limit control must be owned by our application logic

## Webhooks strategy

Webhooks are not part of the current step.

Important clarification:
- webhooks reduce unnecessary polling
- webhooks do not eliminate the need to fetch activity details when a match is needed

So webhooks may reduce total wasted requests later, but they are not the current solution for quota management.
Current solution is registration + rate-limited scheduler.

## Data model direction

The target model should become:

- `clubs`
- `club_members`
- `users`
- `telegram_accounts` or keep fields on `users` for MVP
- `strava_connections`
- `run_templates`
- `event_instances`
- `event_registrations`
- `activities_cache`
- `event_results`
- `notifications`
- `app_settings`

Suggested meaning:
- `run_templates` = recurring definition, for example every Friday 06:30 at Lisi
- `event_instances` = real occurrence for a concrete date
- `event_registrations` = who plans to attend this occurrence
- `event_results` = final matched outcome for that occurrence

Important design rule:
- most club-owned records should carry `ClubId`
- if white-label is expected later, also leave room for `TenantId`

## Roles and access

Need three levels of access:
- platform admin
- club organizer/admin
- member

Do not keep all admin logic as global forever.
Current global admin mode is acceptable temporarily, but should evolve into club-scoped roles.

## Database strategy before v1

Decision:
- no migrations yet
- schema can be rebuilt from scratch while pre-release work is in progress
- optimize for speed of iteration, not compatibility

That means:
- it is acceptable to change table shapes directly
- it is acceptable to reinitialize SQLite during active design changes
- once the first real version is close, add migrations and stabilization

## White-label readiness

Not in MVP, but architecture should allow it later:
- multiple bot tokens
- multiple branded domains
- club-specific text templates
- separate branding assets
- optional isolated deployment for enterprise customers

Do not implement now.
Just avoid hardcoding choices that would block it.

## Practical next implementation steps

1. Add explicit `Club` entity
2. Move current runs under `Club`
3. Replace current member-per-run model with `ClubMembers`
4. Add `RunTemplate`
5. Add `EventInstance`
6. Add `EventRegistration`
7. Change attendance check to inspect only registered participants
8. Add `TimeZoneId` to club and stop hardcoding Tbilisi in matching/scheduling
9. Add Telegram location input for event place
10. Add `StravaRequestScheduler` with 15-minute quota window handling
11. Add user self-service commands for join/leave and profile
12. Consider PostgreSQL only after the first version proves the model

## What not to do now

- separate bot per club
- separate deployment per club
- separate Strava app per club
- checking all members on every run
- overbuilding white-label features before MVP is validated
- migrations before the data model stabilizes

## Current repository note

The repository currently already supports:
- one Strava connection per user
- multiple configurable runs
- inline Telegram management flow
- scheduled/manual attendance checks

That is a useful base, but it is still an intermediate multi-run model.
The next step is to move to real multi-club + event-registration architecture.
