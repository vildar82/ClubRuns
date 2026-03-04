# Lisi Sunrise MVP

Telegram bot + Strava Friday attendance auto-check.

## Implemented

- Single runtime mode: `bot` (long-running process).
- Telegram polling, OAuth callback endpoint (`/strava/callback`), and in-process scheduler.
- SQLite storage: `users`, `strava_auth`, `runs`, `attendance`, `oauth_states`, `legacy_stats`.
- Automatic Strava token refresh.
- Plain-text token storage in SQLite (portable DB between users/machines).
- Console + file logging.

## Bot Commands

- `/start`  
  Registers or updates the Telegram user in local DB and returns a `Connect Strava` button.
- `/connect`  
  Generates a fresh Strava OAuth link and sends it to the user.
- `/status` (admin only)  
  Shows total registered users and how many have Strava connected.
- `/users` (admin only)  
  Prints a short list of users with connection status (`connected` / `not connected`).
- `/run` (admin only)  
  Triggers attendance job immediately and returns summary in chat.
- `/job` (admin only)  
  Alias for `/run`.
- `/leaderboard`  
  Shows combined leaderboard: imported legacy baseline + auto-tracked attendance.
- `/importlegacy` (admin only)  
  Starts legacy import session. Send raw leaderboard text messages after this command.
- `/importlegacydone` (admin only)  
  Finishes import session and writes parsed data into `legacy_stats`.
- `/importlegacycancel` (admin only)  
  Cancels active import session.
- `/importlegacyexample`  
  Sends a short example of import text format.

Admin access is controlled by `Telegram.AdminTelegramUserIds`.

## Quick Start

1. Configure `appsettings.json` or environment variables:

- `Telegram__BotToken`
- `Telegram__GroupChatId`
- `Telegram__AdminTelegramUserIds__0`, `__1`, ...
- `Strava__ClientId`
- `Strava__ClientSecret`
- `Strava__RedirectUri` (for example `http://localhost:5099/strava/callback`)
- `Strava__UseReadAllScope` (`true|false`)

2. Make sure your Strava app has the same `redirect_uri`.

3. Run bot mode:

```powershell
dotnet run
```

## Legacy Import Format

You can use the sample file in repo as a format reference:
- `legacy_stats.example.txt`

In Telegram:
1. `/importlegacy`
2. Send leaderboard text (one or multiple messages)
3. `/importlegacydone`

## appsettings.json Reference

Below is what each property means and where to get it.

- `Telegram.BotToken`  
  Telegram bot API token. Create a bot via `@BotFather` in Telegram, run `/newbot`, copy the token.
- `Telegram.GroupChatId`  
  Optional default Telegram chat id for automatic report publishing (for example, a group id). If empty/null, automatic job publishing is skipped.
- `Telegram.AdminTelegramUserIds`  
  Telegram user ids allowed to run admin commands.

- `Strava.ClientId`  
  Strava application client id. Get it from Strava developer settings: https://www.strava.com/settings/api
- `Strava.ClientSecret`  
  Strava application client secret from the same Strava API app page.
- `Strava.RedirectUri`  
  OAuth callback URL handled by this app, for example `http://localhost:5099/strava/callback`. Must exactly match the callback configured in Strava app.
- `Strava.UseReadAllScope`  
  `true` to request `activity:read_all` (can read private activities), `false` for `activity:read`.

- `Matching.LisiStartLat` / `Matching.LisiStartLng`  
  Reference coordinates near Lisi start point used for distance matching.
- `Matching.RadiusKm`  
  Maximum distance in kilometers from reference point to activity start location.
- `Matching.WindowStartLocal` / `Matching.WindowEndLocal`  
  Local time window (`HH:mm`, Tbilisi timezone) in which start time must fall.
- `Matching.TargetStartLocal`  
  Preferred start time (`HH:mm`) used to choose the best activity when multiple matches exist.
- `Matching.AllowedActivityTypes`  
  Allowed Strava activity types, usually `Run` and `TrailRun`.
- `Matching.MinDistanceKm` / `Matching.MaxDistanceKm`  
  Optional distance filter in kilometers. Set `null` to disable.

- `Database.Path`  
  SQLite file path. Relative path is resolved from app runtime directory.
- `Schedule.DayOfWeek`  
  Auto-run day in bot scheduler (`Friday`, `Monday`, etc.).
- `Schedule.Hour`  
  Auto-run hour in 24h format (Tbilisi local time).
- `Schedule.MinuteFrom` / `Schedule.MinuteTo`  
  Inclusive minute window for catch-up at startup/restart time.
- `Logging.LogPath`  
  File log path. Relative path is resolved from app runtime directory.

Environment variable mapping uses double underscore, for example:
- `Strava__ClientId`
- `Matching__RadiusKm`
- `Telegram__AdminTelegramUserIds__0`
- `Schedule__DayOfWeek`

## MVP Notes

- Private activities require `activity:read_all`.
- If callback server is not reachable at `RedirectUri`, Strava connection cannot complete.
- Tokens are currently stored as plain text in SQLite for portability.
- For production: add migrations, retry policies, and stricter startup config validation.