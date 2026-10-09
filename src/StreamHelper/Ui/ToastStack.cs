using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamHelper.Ui;

public static class ToastStack
{
    public const int MaxToasts = 4;
    public const double Gap = 8;

    public static List<double> Tops(double anchorTop, IReadOnlyList<double> heights, ScreenBounds workArea)
    {
        var tops = new List<double>(heights.Count);
        if (heights.Count == 0) return tops;
        var total = heights.Sum() + Gap * (heights.Count - 1);
        if (anchorTop + total <= workArea.Bottom)
        {
            var y = anchorTop;
            foreach (var height in heights)
            {
                tops.Add(y);
                y += height + Gap;
            }
            return tops;
        }

        var bottom = anchorTop + heights[0];
        foreach (var height in heights)
        {
            tops.Add(Math.Max(workArea.Top, bottom - height));
            bottom -= height + Gap;
        }
        return tops;
    }
}
