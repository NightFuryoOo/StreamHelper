using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace StreamHelper.Ui;

internal static class TreeWalk
{
    public static IEnumerable<DependencyObject> UpFrom(DependencyObject? node)
    {
        for (; node != null; node = node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            yield return node;
        }
    }
}
