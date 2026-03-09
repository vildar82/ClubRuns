# ClubRuns

Telegram bot for running clubs attendance tracking with one-time Strava connection and multiple configurable club runs.

## What the bot does

- Registers a Telegram user and connects Strava once via `/start`.
- Stores multiple club runs in SQLite.
- Keeps a separate member list for each club run.
- Runs attendance checks for every active club run scheduled for the selected day.
- Lets admins create and edit club runs from Telegram through a guided `/manage` dialog with inline buttons.
- Keeps legacy import and leaderboard support for old manually tracked statistics.

## Current bot commands

User commands:
- `/start` - register user and show Strava connect button.
- `/leaderboard` - show combined legacy + auto-tracked attendance leaderboard.
- `/myid` - show Telegram user id.
- `/help` - show help.

Admin commands:
- `/manage` - create and edit club runs with prompts.
- `/users` - list registered users and Strava connection status.
- `/run [YYYY-MM-DD] [club_run_id]` - run attendance check manually.
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
- `Schedule__DayOfWeek`
- `Schedule__Hour`
- `Schedule__MinuteFrom`
- `Schedule__MinuteTo`
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
4. Create a club run.
5. Add members to the run.
6. Ask users to run `/start` and connect Strava.
7. Run `/run` to test attendance manually.

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
- `Create run`
- `Edit run`
- `List runs`
- `Cancel`

Create flow asks for:
- run name
- day of week
- schedule hour and minute window
- start latitude and longitude
- radius in km
- attendance search window start/end
- target start time
- optional report chat id

Edit flow supports:
- edit name
- edit day
- edit hour
- edit minute range
- edit start point and radius
- edit attendance window
- edit report chat id
- toggle active/inactive
- manage members
- run selected club run immediately

Member management supports:
- list members
- add member by Telegram user id or `@username`
- remove member by Telegram user id or `@username`

Important: user must have already used `/start` before they can be added to a club run.

## Data model

Core tables:
- `users`
- `strava_auth`
- `club_runs`
- `club_run_members`
- `club_run_reports`
- `club_run_attendance`
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
- `Schedule.DayOfWeek` - global scheduler day in Tbilisi time.
- `Schedule.Hour` - global scheduler hour.
- `Schedule.MinuteFrom` - beginning of scheduler catch-up window.
- `Schedule.MinuteTo` - end of scheduler catch-up window.
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
- Scheduler checks only active club runs that match the selected day.
- Manual `/run` can target all runs for a day or a specific `club_run_id`.
- Private Strava activities require `activity:read_all`.
