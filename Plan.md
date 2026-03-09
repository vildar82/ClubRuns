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
- keep internal project file names as-is for now
- do a full code/project rename only when the data model stabilizes

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
- recurring runs/events
- event registrations
- event attendance/results
- notifications

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
- process upcoming runs/events based on stored schedule
- avoid club-specific hardcoded logic

5. Keep service boundaries clean
- Telegram handling
- Strava integration
- attendance matching
- scheduler/webhook processing
- data access

## MVP scope

Core MVP:
- Telegram `/start`
- Strava OAuth connect
- token refresh before API calls
- create/edit recurring runs from the bot
- add/remove members to runs
- attendance matching by time + location + activity type
- attendance report in Telegram chat
- attendance history in DB

Recommended additions to MVP:
- registration for a specific upcoming event/run
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
- each event/run has registered participants
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
- do 1-2 checks per event at most
- cache already matched activities
- do not re-check a user after a confirmed match for the same event unless needed

## Webhooks strategy

Polling is acceptable for MVP only as a controlled fallback.

Target direction:
- add Strava webhooks
- use webhook events as the main trigger for new activities
- keep one scheduled reconciliation job as backup

Webhook design notes:
- one webhook subscription per Strava application
- public HTTPS endpoint
- verify endpoint with `hub.challenge`
- receive POST events and acknowledge fast
- save raw events before processing
- process events asynchronously

Webhook should be used to:
- detect new activities
- map `owner_id` to platform user
- check whether this user is registered for a nearby event
- fetch activity details only when needed

## Data model direction

The current multi-run model is a useful intermediate step, but the target model should become:

- `tenants` or optional future `platform_accounts`
- `clubs`
- `club_members`
- `users`
- `telegram_accounts`
- `strava_connections`
- `events`
- `event_registrations`
- `activities`
- `event_results`
- `notifications`
- `app_settings`

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

1. Rename visible product text from `TRC Bot` to `ClubRuns`
2. Add explicit `Club` entity
3. Move run ownership under `Club`
4. Add event registration separate from run membership
5. Change attendance check to only inspect registered participants
6. Add `my profile` and join/leave commands for users
7. Add webhook endpoints and raw webhook event storage
8. Keep scheduler as backup reconciliation
9. Replace global admin-only model with club roles
10. Consider PostgreSQL when moving beyond local/single-instance MVP

## What not to do now

- separate bot per club
- separate deployment per club
- separate Strava app per club
- checking all members on every run
- overbuilding white-label features before MVP is validated

## Current repository note

The repository currently already supports:
- one Strava connection per user
- multiple configurable runs
- inline Telegram management flow
- scheduled/manual attendance checks

That is a good base, but it is still "multi-run inside one product", not full multi-club SaaS yet.
