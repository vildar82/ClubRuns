# ClubRuns

Telegram bot for running clubs attendance tracking with one-time Strava connection and multiple configurable club runs.

## What the bot does

- Registers a Telegram user and connects Strava once via `/start`.
- Stores multiple clubs and multiple recurring runs in SQLite.
- Lets users register themselves for upcoming run events.
- Runs attendance checks only for users registered for a specific event instance.
- Lets admins create and edit clubs and runs from Telegram through a guided `/manage` dialog with inline buttons.
- Keeps legacy import support for old manually tracked statistics.

## Current bot commands

User commands:
- `/start` - register user and show Strava connect button.
- `/clubs` - list clubs.
- `/runs` - browse active runs and register with inline buttons.
- `/myregistrations` - list upcoming registrations.
- `/leaderboard` - show run statistics based on auto-tracked event results.
- `/myid` - show Telegram user id.
- `/help` - show help.

Admin commands:
- `/manage` - create and edit clubs and runs.
- `/users` - list registered users and Strava connection status.`r`n- `/clubs` - list clubs.
- `/run` - choose a club and run, then start attendance check manually.
- `/admins` - list admins.
- `/addadmin <id|@username>` - add admin.
- `/setstrava <client_id> <client_secret>` - store Strava credentials in DB settings.
- `/stravastatus` - show whether Strava credentials are configured.
- `/importlegacy` - start legacy stats import session.
- `/importlegacydone` - finish import session.
- `/importlegacycancel` - cancel import session.
- `/importlegacyexample` - send example legacy import text.

## Get Started

### 1. Create a Telegram bot

1. Open `@BotFather` in Telegram.
2. Run `/newbot`.
3. Save the bot token.

This token is required before the application can start.

### 2. Create a Strava application

1. Open your Strava API settings page.
2. Create an application.
3. Save:
- `ClientId`
- `ClientSecret`
4. Set the Strava callback URL to your ClubRuns callback endpoint.

What callback URL means:
- after the user approves Strava access, Strava redirects the browser back to ClubRuns
- that redirect must point to a real HTTP endpoint in this application
- the endpoint path in ClubRuns is `/strava/callback`

Local example:
- `http://localhost:5099/strava/callback`

Production example:
- `https://clubruns.example.com/strava/callback`

Important:
- the value in Strava app settings and the value in ClubRuns config must be exactly the same
- in production it should be a public HTTPS URL

### 3. Configure the application

For deployment, configure these environment variables:

Required:
- `Telegram__BotToken`
- `Telegram__AdminTelegramUserIds__0`
- `Strava__RedirectUri`

Optional at startup:
- `Strava__ClientId`
- `Strava__ClientSecret`
- `Strava__UseReadAllScope`
- `Database__Path`
- `Logging__LogPath`

Notes:
- `Telegram__AdminTelegramUserIds__0` is the first admin Telegram user id
- add `__1`, `__2`, and so on for more bootstrap admins
- `Strava__ClientId` and `Strava__ClientSecret` may also be set later from Telegram with `/setstrava`

Windows PowerShell example:

```powershell
$env:Telegram__BotToken = "YOUR_BOT_TOKEN"
$env:Telegram__AdminTelegramUserIds__0 = "123456789"
$env:Strava__RedirectUri = "http://localhost:5099/strava/callback"
```

### 4. Publish and run

To build a local release:

```powershell
dotnet publish .\ClubRuns.App\ClubRuns.App.csproj -c Release -r win-x64 --self-contained false
```

Then run the published app, for example:

```powershell
.\ClubRuns.App.exe
```

For local development from source you can also run:

```powershell
dotnet run --project .\ClubRuns.App\ClubRuns.App.csproj
```

What starts together in one process:
- Telegram bot polling
- Strava OAuth callback endpoint
- scheduler
- SQLite initialization

### 5. First-time setup in Telegram

1. Send `/start` to the bot.
2. If Strava secrets were not configured via environment variables, run:

```text
/setstrava <client_id> <client_secret>
```

3. Run `/manage`.
4. Create a club.
5. Create a run inside that club.
6. Ask users to run `/start` and connect Strava.
7. Ask users to open `/runs` and register for the next event.
8. Run `/run` to test attendance manually.

### 6. How to find admin Telegram user ids

Preferred way:
- start the bot
- send `/myid`
- use that number in admin configuration

Why ids are better than usernames:
- Telegram usernames are optional
- usernames can be changed by users
- Telegram user ids are stable

Current recommendation:
- keep admin bootstrap by Telegram user id
- allow adding admins later from inside the bot with `/addadmin <id|@username>`

## Manage dialog

`/manage` opens an inline-button admin flow.

Available actions:
- `Create club`
- `List clubs`
- `Delete club`
- `Create run`
- `Edit run`
- `List runs`
- `Cancel`

Create club asks for:
- club name
- time zone id, for example `Asia/Tbilisi`
- or `skip` to use the default app time zone

Create run asks for:
- club
- run name
- day of week
- schedule hour and minute window
- start latitude and longitude
- radius in km
- attendance search window start/end
- target start time
- attendance check time
- optional report chat id

Edit run supports:
- edit name
- edit day
- edit hour
- edit minute range
- edit start point and radius
- edit attendance window
- edit attendance check time
- edit report chat id
- toggle active/inactive
- run selected club run immediately

## Data model

Core tables:
- `users`
- `strava_auth`
- `clubs`
- `club_runs`
- `event_instances`
- `event_registrations`
- `event_results`
- `event_reports`
- `strava_request_queue`
- `oauth_states`
- `admins`
- `app_settings`

Legacy compatibility tables still exist:
- `runs`
- `attendance`
- `legacy_stats`

## Configuration

Main settings:
- `Telegram.BotToken` - Telegram bot token from `@BotFather`.
- `Telegram.AdminTelegramUserIds` - bootstrap admin ids on startup.
- `Strava.RedirectUri` - OAuth callback URL, must match the Strava app settings.
- `Strava.UseReadAllScope` - `true` requests `activity:read_all`, `false` requests `activity:read`.
- `Strava.ClientId` - optional fallback Strava client id.
- `Strava.ClientSecret` - optional fallback Strava client secret.
- `Database.Path` - SQLite file path, relative paths are resolved from app runtime directory.
- `Logging.LogPath` - log file path.

Runtime Strava credentials set by `/setstrava` are stored in the database and override config values.

## Legacy import

Example file:
- `ClubRuns.App/legacy_stats.example.txt`

Telegram flow:
1. `/importlegacy`
2. send leaderboard text in one or more messages
3. `/importlegacydone`

## Notes

- Tokens are stored in plain text in SQLite for portability.
- Scheduler checks active club runs based on each run's own local check time and the default app time zone.
- Manual `/run` opens an admin inline flow to choose a club and run.
- Private Strava activities require `activity:read_all`.






