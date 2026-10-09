using System.Globalization;

namespace StreamHelper.Ui;

public enum DurationUnit
{
    Seconds,
    Minutes,
    Hours,
    Days,
}

public static class MuteDuration
{
    public const int MaxSeconds = 14 * 24 * 3600;

    public const string Limits = "От 1 секунды до 14 суток.";

    public static int UnitSeconds(DurationUnit unit) => unit switch
    {
        DurationUnit.Seconds => 1,
        DurationUnit.Minutes => 60,
        DurationUnit.Hours => 3600,
        _ => 86400,
    };

    public static bool TryCompute(string? text, DurationUnit unit, out int seconds, out string error)
    {
        seconds = 0;
        error = "";
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            error = "Впиши время.";
            return false;
        }
        if (!long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
        {
            error = "Только цифры.";
            return false;
        }

        var total = amount * UnitSeconds(unit);
        if (amount < 1 || total > MaxSeconds || amount > MaxSeconds)
        {
            error = Limits;
            return false;
        }
        seconds = (int)total;
        return true;
    }
}
