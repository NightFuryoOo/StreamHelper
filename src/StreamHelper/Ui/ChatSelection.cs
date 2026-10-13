using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace StreamHelper.Ui;

public static class ChatSelection
{
    public static string TextBetween(TextPointer from, TextPointer to)
    {
        var text = new StringBuilder();
        var at = from;
        while (at.CompareTo(to) < 0)
        {
            var context = at.GetPointerContext(LogicalDirection.Forward);
            if (context == TextPointerContext.Text)
            {
                var run = at.GetTextInRun(LogicalDirection.Forward);
                var take = Math.Min(run.Length, at.GetOffsetToPosition(to));
                text.Append(run, 0, take);
                at = at.GetPositionAtOffset(take) ?? to;
                continue;
            }
            if (context == TextPointerContext.ElementStart && at.GetAdjacentElement(LogicalDirection.Forward) is InlineUIContainer picture)
            {
                if (picture.Child is Image { ToolTip: string name } image && ChatEmotes.IsEmote(image.Tag)) text.Append(name);
                at = picture.ElementEnd.CompareTo(to) < 0 ? picture.ElementEnd : to;
                continue;
            }
            if (context == TextPointerContext.ElementStart && at.GetAdjacentElement(LogicalDirection.Forward) is LineBreak) text.Append('\n');
            at = at.GetNextContextPosition(LogicalDirection.Forward) ?? to;
        }
        return text.ToString();
    }

    public static List<Rect> Rects(TextPointer from, TextPointer to)
    {
        var rects = new List<Rect>();
        var at = from.GetInsertionPosition(LogicalDirection.Forward);
        while (at.CompareTo(to) < 0)
        {
            var next = at.GetNextInsertionPosition(LogicalDirection.Forward);
            if (next == null || next.CompareTo(to) > 0) next = to;
            if (next.CompareTo(at) <= 0) break;
            var left = at.GetCharacterRect(LogicalDirection.Forward);
            var right = next.GetCharacterRect(LogicalDirection.Backward);
            if (!left.IsEmpty && !right.IsEmpty && Overlap(left, right) && right.Left > left.Left)
            {
                var piece = new Rect(new Point(left.Left, Math.Min(left.Top, right.Top)), new Point(right.Left, Math.Max(left.Bottom, right.Bottom)));
                if (rects.Count > 0 && Overlap(rects[^1], piece) && piece.Left <= rects[^1].Right + 1) rects[^1] = Rect.Union(rects[^1], piece);
                else rects.Add(piece);
            }
            at = next;
        }
        return rects;
    }

    private static bool Overlap(Rect a, Rect b) => a.Top < b.Bottom - 1 && b.Top < a.Bottom - 1;
}
