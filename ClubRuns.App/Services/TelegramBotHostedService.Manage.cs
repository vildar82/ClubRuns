using Telegram.Bot.Types;

namespace ClubRuns.App.Services;

public sealed partial class TelegramBotHostedService
{
    private Task<bool> HandleManageMessageAsync(long chatId, long telegramUserId, string text, CancellationToken ct)
    {
        return manageFlow.HandleMessageAsync(chatId, telegramUserId, text, ct);
    }

    private Task HandleManageCallbackAsync(long chatId, long telegramUserId, CallbackQuery callbackQuery, CancellationToken ct)
    {
        return manageFlow.HandleCallbackAsync(chatId, telegramUserId, callbackQuery, ct);
    }
}
