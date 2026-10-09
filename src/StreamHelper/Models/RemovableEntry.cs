namespace StreamHelper.Models;

public sealed class RemovableEntry
{
    public RemovableEntry(string text) => Text = text;

    public string Text { get; }

    public string RemoveName => "Убрать " + Text;
}