# Lisi Sunrise MVP

Telegram bot + Strava Friday attendance auto-check.

## Implemented

- Single runtime mode: `bot` (long-running process).
- Telegram polling, OAuth callback endpoint (`/strava/callback`), and in-process scheduler.
- SQLite storage: `users`, `strava_auth`, `runs`, `attendance`, `oauth_states`, `legacy_stats`, `admins`, `app_settings`.
- Automatic Strava token refresh.
- Plain-text token storage in SQLite (portable DB between users/machines).
- Console + file logging.

## Bot Commands

- `/start`  
  Registers or updates the Telegram user in local DB and returns a `Connect Strava` button.
- `/connect`  
  Generates a fresh Strava OAuth link and sends it to the user.
- `/leaderboard`  
  Shows combined leaderboard: imported legacy baseline + auto-tracked attendance.
- `/myid`  
  Shows your Telegram user id.
- `/help`  
  Shows command list.

Admin commands:
- `/status`  
  Shows total registered users and how many have Strava connected.
- `/users`  
  Prints a short list of users with connection status (`connected` / `not connected`).
- `/run` or `/job`  
  Triggers attendance job immediately.
- `/admins`  
  Lists current admins.
- `/addadmin <id|@username>`  
  Adds a new admin dynamically.
- `/setstrava <client_id> <client_secret>`  
  Stores Strava credentials in DB settings.
- `/stravastatus`  
  Shows whether Strava credentials are configured.
- `/importlegacy`  
  Starts legacy import session. Send raw leaderboard text messages after this command.
- `/importlegacydone`  
  Finishes import session and writes parsed data into `legacy_stats`.
- `/importlegacycancel`  
  Cancels active import session.
- `/importlegacyexample`  
  Sends example import text loaded from `legacy_stats.example.txt`.

Admin bootstrap is controlled by `Telegram.AdminTelegramUserIds` in config. After startup, admins can add more admins with `/addadmin`.

## Secrets Strategy

### Simple for local debugging (now)

1. Copy `LisiSunrise/local.settings.example.json` to `LisiSunrise/local.settings.json`.
2. Fill values:
- `Telegram.BotToken`
- `Telegram.AdminTelegramUserIds` (your id)
3. Run app:

```powershell
dotnet run --project .\LisiSunrise\LisiSunrise.csproj
```

4. In Telegram as admin, set Strava secrets once:

```text
/setstrava <client_id> <client_secret>
```

`local.settings.json` is ignored by git.

### For server/production (later)

- Set `Telegram__BotToken` and bootstrap admin ids via environment variables or secret manager.
- Keep Strava credentials either:
  - in env vars (`Strava__ClientId`, `Strava__ClientSecret`), or
  - via `/setstrava` stored in DB (`app_settings`).
- Prefer secret manager (Docker/K8s secrets, cloud secrets, CI/CD secret vars) over files.

## Quick Start

1. Configure defaults in `LisiSunrise/appsettings.json` (non-secret values).
2. Configure secrets as described above.
3. Run bot mode:

```powershell
dotnet run --project .\LisiSunrise\LisiSunrise.csproj
```

## Legacy Import Format

You can use the sample file in repo as a format reference:
- `LisiSunrise/legacy_stats.example.txt`

In Telegram:
1. `/importlegacy`
2. Send leaderboard text (one or multiple messages)
3. `/importlegacydone`

## appsettings.json Reference

- `Telegram.BotToken`  
  Telegram bot API token from `@BotFather` (optional if using env/local.settings).
- `Telegram.GroupChatId`  
  Optional default Telegram chat id for automatic report publishing.
- `Telegram.AdminTelegramUserIds`  
  Bootstrap admin user ids at startup.

- `Strava.RedirectUri`  
  OAuth callback URL handled by this app (must match Strava app callback).
- `Strava.UseReadAllScope`  
  `true` to request `activity:read_all`, `false` for `activity:read`.
- `Strava.ClientId` / `Strava.ClientSecret`  
  Optional fallback from env/appsettings. Runtime DB values set by `/setstrava` take priority.

- `Matching.*`  
  Matching filters for attendance (coords, radius, time window, allowed activity types, optional distance bounds).
- `Database.Path`  
  SQLite file path. Relative path is resolved from app runtime directory.
- `Schedule.DayOfWeek`, `Schedule.Hour`, `Schedule.MinuteFrom`, `Schedule.MinuteTo`  
  Scheduler config in Tbilisi local time.
- `Logging.LogPath`  
  File log path.

## MVP Notes

- Private activities require `activity:read_all`.
- If callback server is not reachable at `RedirectUri`, Strava connection cannot complete.
- Tokens are currently stored as plain text in SQLite for portability.
- For production: add migrations, retry policies, and stricter startup config validation.
