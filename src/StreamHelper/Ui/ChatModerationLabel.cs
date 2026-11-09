using StreamHelper.Api;

namespace StreamHelper.Ui;

public static class ChatModerationLabel
{
    public static string Text(ChatModeration moderation)
    {
        var action = moderation.Kind switch
        {
            ChatModerationKind.Ban => "бан",
            ChatModerationKind.Timeout => moderation.Seconds > 0 ? "мут на " + ChatPlacement.DescribeDuration(moderation.Seconds) : "мут",
            _ => moderation.Lifted == ChatModerationKind.Ban ? "бан снят" : "мут снят",
        };
        return moderation.ModeratorName.Length > 0 ? action + " · " + moderation.ModeratorName : action;
    }

    public static string Unbroken(ChatModeration moderation) => Text(moderation).Replace(' ', '\u00A0');

    public static string? Tip(ChatModeration moderation) =>
        IsActive(moderation) && moderation.Reason.Length > 0 ? "Причина: " + moderation.Reason : null;

    public static bool IsActive(ChatModeration moderation) => moderation.Kind != ChatModerationKind.Lift;
}
