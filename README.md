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

Base config file: `TRC_Bot/appsettings.json`

Properties:
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

Runtime Strava credentials set by `/setstrava` are stored in the database and override appsettings values.

## Local development

1. Fill `TRC_Bot/local.settings.json` or use environment variables.
2. Set at least:
- `Telegram.BotToken`
- `Telegram.AdminTelegramUserIds`
3. Run:

```powershell
dotnet run --project .\TRC_Bot\TRC_Bot.csproj
```

4. In Telegram:
- run `/start`
- run `/setstrava <client_id> <client_secret>` as admin if Strava secrets are not already configured
- run `/manage` to create club runs

## Legacy import

Example file:
- `TRC_Bot/legacy_stats.example.txt`

Telegram flow:
1. `/importlegacy`
2. send leaderboard text in one or more messages
3. `/importlegacydone`

## Notes

- Tokens are stored in plain text in SQLite for portability.
- Scheduler checks only active club runs that match the selected day.
- Manual `/run` can target all runs for a day or a specific `club_run_id`.
- Private Strava activities require `activity:read_all`.
