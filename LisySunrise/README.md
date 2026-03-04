# Lisi Sunrise MVP

Telegram-бот + Strava авто-поиск посещаемости по пятницам.

## Что реализовано

- Режим `bot`: polling Telegram + OAuth callback endpoint (`/strava/callback`) + планировщик пятницы 12:00 Asia/Tbilisi.
- Режим `job`: одноразовый запуск проверки и публикация отчета в группу.
- SQLite-хранилище: `users`, `strava_auth`, `runs`, `attendance`, `oauth_states`.
- Команды:
  - `/start`
  - `/connect`
  - `/status` (admin)
  - `/users` (admin)
  - `/run` (admin)
- Авто-refresh Strava токенов.
- Шифрование Strava токенов в SQLite через Windows DPAPI (`CurrentUser` scope).
- Отчет в группу: found/not found/errors.
- Логирование в консоль и файл.

## Быстрый старт

1. Заполните `appsettings.json` или env vars:

- `Telegram__BotToken`
- `Telegram__GroupChatId`
- `Telegram__AdminTelegramUserIds__0`, `__1`, ...
- `Strava__ClientId`
- `Strava__ClientSecret`
- `Strava__RedirectUri` (например `http://localhost:5099/strava/callback`)
- `Strava__UseReadAllScope` (`true|false`)

2. Убедитесь, что в Strava app настроен тот же `redirect_uri`.

3. Запуск бота:

```powershell
dotnet run -- bot
```

4. Одноразовый job запуск:

```powershell
dotnet run -- job
```

## Windows Task Scheduler (вариант job)

Создайте задачу на пятницу 12:00 (Tbilisi timezone на уровне ОС/Task):

- Program/script: `dotnet`
- Arguments: `run --project C:\dev\LisySunrise\LisySunrise\LisySunrise.csproj -- job`
- Start in: `C:\dev\LisySunrise\LisySunrise`

## Замечания MVP

- Доступ к приватным активностям требует `activity:read_all`.
- Если callback сервер недоступен по `RedirectUri`, подключение Strava не завершится.
- DPAPI `CurrentUser` значит токены можно расшифровать только под тем же Windows пользователем.
- Для продакшена стоит добавить миграции, retry policies и более строгую валидацию конфигурации.
