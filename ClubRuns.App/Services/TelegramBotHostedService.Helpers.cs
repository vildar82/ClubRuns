using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ClubRuns.App.Data;
using Telegram.Bot;

namespace ClubRuns.App.Services;

public sealed partial class TelegramBotHostedService
{
    private async Task<bool> HandleLegacyImportChunkAsync(long chatId, long telegramUserId, string text, CancellationToken ct)
    {
        if (!await repository.IsAdminAsync(telegramUserId, ct))
        {
            return false;
        }

        if (!_importSessions.TryGetValue(telegramUserId, out var session) || session.ChatId != chatId)
        {
            return false;
        }

        session.Buffer.AppendLine(text);
        session.ChunksCount++;
        if (session.ChunksCount % 3 == 0)
        {
            await botClient.SendMessage(chatId, $"Received {session.ChunksCount} message chunks so far.", cancellationToken: ct);
        }

        return true;
    }

    private async Task<bool> EnsureAdminOrReplyAsync(long chatId, long telegramUserId, CancellationToken ct)
    {
        if (await repository.IsAdminAsync(telegramUserId, ct))
        {
            return true;
        }

        await botClient.SendMessage(chatId, presentation.BuildNotAdminText(telegramUserId), cancellationToken: ct);
        return false;
    }

    private async Task<long?> ResolveTelegramUserIdAsync(string target, CancellationToken ct)
    {
        var user = await ResolveUserAsync(target, ct);
        return user?.TelegramUserId;
    }

    private async Task<UserRecord?> ResolveUserAsync(string target, CancellationToken ct)
    {
        if (long.TryParse(target, out var numericTelegramId))
        {
            return await repository.GetUserByTelegramIdAsync(numericTelegramId, ct);
        }

        var username = target.TrimStart('@');
        return string.IsNullOrWhiteSpace(username) ? null : await repository.GetUserByTelegramUsernameAsync(username, ct);
    }

    private async Task EnsureTelegramUserExistsAsync(long telegramUserId, CancellationToken ct)
    {
        var existing = await repository.GetUserByTelegramIdAsync(telegramUserId, ct);
        if (existing is null)
        {
            await repository.UpsertUserAsync(telegramUserId, null, null, null, ct);
        }
    }

    private async Task<string> BuildConnectUrlAsync(long telegramUserId, CancellationToken ct)
    {
        var state = CreateStateToken(telegramUserId);
        await repository.SaveOAuthStateAsync(state, telegramUserId, ct);
        return await stravaApi.BuildAuthorizeUrlAsync(state, ct);
    }

    private static string CreateStateToken(long telegramUserId)
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        var random = Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").Replace("=", string.Empty);
        var payload = $"{telegramUserId}:{random}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).Replace("+", "-").Replace("/", "_").Replace("=", string.Empty);
    }


    private static string BuildLegacyExampleText()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "legacy_stats.example.txt");
        return File.Exists(path) ? File.ReadAllText(path) : "Example file is missing: legacy_stats.example.txt";
    }

    private sealed class LegacyImportSession(long chatId, StringBuilder buffer, int chunksCount)
    {
        public long ChatId { get; } = chatId;
        public StringBuilder Buffer { get; } = buffer;
        public int ChunksCount { get; set; } = chunksCount;
    }
}


