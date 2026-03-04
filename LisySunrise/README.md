# Lisi Sunrise MVP

Telegram bot + Strava Friday attendance auto-check.

## Implemented

- `bot` mode: Telegram polling, OAuth callback endpoint (`/strava/callback`), and Friday 12:00 Asia/Tbilisi scheduler.
- `job` mode: one-shot attendance collection and report publishing.
- SQLite storage: `users`, `strava_auth`, `runs`, `attendance`, `oauth_states`.
- Commands:
  - `/start`
  - `/connect`
  - `/status` (admin)
  - `/users` (admin)
  - `/run` (admin)
- Automatic Strava token refresh.
- Plain-text token storage in SQLite (portable DB between users/machines).
- Optional Telegram report target (group if configured, or manual user target in `job` mode).
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
dotnet run -- bot
```

4. Run one-shot job mode:

```powershell
dotnet run -- job
```

## appsettings.json Reference

Below is what each property means and where to get it.

- `Telegram.BotToken`  
  Telegram bot API token. Create a bot via `@BotFather` in Telegram, run `/newbot`, copy the token.
- `Telegram.GroupChatId`  
  Optional default Telegram chat id for automatic report publishing (for example, a group id). If empty/null, automatic job publishing is skipped.
- `Telegram.AdminTelegramUserIds`  
  Telegram user ids allowed to run admin commands (`/status`, `/users`, `/run`). Use `getUpdates` to read your user id after sending a private message to the bot.

- `Strava.ClientId`  
  Strava application client id. Get it from Strava developer settings: https://www.strava.com/settings/api
- `Strava.ClientSecret`  
  Strava application client secret from the same Strava API app page.
- `Strava.RedirectUri`  
  OAuth callback URL handled by this app, for example `http://localhost:5099/strava/callback`. Must exactly match the Authorization Callback Domain/App settings in Strava.
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

## Windows Task Scheduler (job mode)

Create a Friday 12:00 task (Tbilisi timezone should be set at OS/task level):

- Program/script: `dotnet`
- Arguments: `run --project C:\dev\LisySunrise\LisySunrise\LisySunrise.csproj -- job`
- Start in: `C:\dev\LisySunrise\LisySunrise`

`job` mode behavior:
- Always prints full report to console.
- Optionally asks for a Telegram destination (`@username` or numeric `chat id`) and sends the same report there.
- `@username` works only for users already present in local DB (they must have used `/start` before).

## MVP Notes

- Private activities require `activity:read_all`.
- If callback server is not reachable at `RedirectUri`, Strava connection cannot complete.
- Tokens are currently stored as plain text in SQLite for portability.
- For production: add migrations, retry policies, and stricter startup config validation.
