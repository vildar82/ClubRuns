using Telegram.Bot;

namespace LisiSunrise;

public static class StravaCallbackEndpoints
{
    public static void MapStravaEndpoints(this WebApplication app)
    {
        // OAuth redirect endpoint called by Strava after user approves app access.
        app.MapGet("/strava/callback", async (
            string? code,
            string? state,
            string? error,
            SqliteRepository repository,
            StravaApiClient stravaApi,
            ITelegramBotClient botClient,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("StravaCallback");

            if (!string.IsNullOrWhiteSpace(error))
            {
                // User denied or Strava returned an OAuth error.
                return Results.Text($"Strava authorization failed: {error}", "text/plain");
            }

            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
            {
                return Results.BadRequest("Missing code or state.");
            }

            // state is one-time: read + delete to block replay.
            var stateRecord = await repository.ConsumeOAuthStateAsync(state, ct);
            if (stateRecord is null)
            {
                return Results.BadRequest("Invalid or expired state.");
            }

            var user = await repository.GetUserByTelegramIdAsync(stateRecord.TelegramUserId, ct);
            if (user is null)
            {
                // If user row is missing, callback cannot be linked to Telegram identity.
                return Results.BadRequest("User not found.");
            }

            try
            {
                // Final OAuth step: exchange code and persist tokens for this Telegram user.
                var token = await stravaApi.ExchangeCodeAsync(code, ct);
                await repository.SaveStravaAuthAsync(
                    user.Id,
                    token.Athlete.Id,
                    token.AccessToken,
                    token.RefreshToken,
                    token.ExpiresAt,
                    token.Scope,
                    ct);

                await botClient.SendMessage(user.TelegramUserId, "Strava connected ✅", cancellationToken: ct);
                return Results.Text("Strava connected. You can return to Telegram.", "text/plain");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to complete Strava callback for user {TelegramUserId}", user.TelegramUserId);
                await botClient.SendMessage(user.TelegramUserId, "Failed to connect Strava. Try /start again.", cancellationToken: ct);
                return Results.Problem("Failed to exchange token with Strava.");
            }
        });
    }
}

