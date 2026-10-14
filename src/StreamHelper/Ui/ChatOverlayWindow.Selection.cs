using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using StreamHelper.Storage;
using static StreamHelper.Ui.Freezables;

namespace StreamHelper.Ui;

public partial class ChatOverlayWindow
{
    private const int CopyKeyId = 0x5C01;
    private const uint CopyKey = 0x43;
    private static readonly Brush SelectionBrush = Solid(Color.FromArgb(0x8C, 0x2F, 0x6F, 0xE0));

    private sealed record Spot(ChatLine Line, System.Windows.Documents.TextPointer Position);

    private readonly DispatcherTimer _copiedTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private Spot? _anchor;
    private Spot? _focus;
    private Point _pressedAt;
    private bool _selecting;
    private bool _copyKeyHeld;

    private bool HasSelection => _anchor != null && _focus != null && (_anchor.Line != _focus.Line || _anchor.Position.CompareTo(_focus.Position) != 0);

    private void InitSelection()
    {
        Viewport.PreviewMouseLeftButtonDown += OnSelectDown;
        Viewport.PreviewMouseMove += OnSelectMove;
        Viewport.PreviewMouseLeftButtonUp += OnSelectUp;
        Viewport.LostMouseCapture += (_, _) => _selecting = false;
        _copiedTimer.Tick += (_, _) =>
        {
            _copiedTimer.Stop();
            CopiedStrip.Visibility = Visibility.Collapsed;
            UpdatePausedStrip();
        };
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(OnCopyKey);
        Closed += (_, _) => ReleaseCopyKey();
    }

    private void OnSelectDown(object sender, MouseButtonEventArgs e)
    {
        if (!_interactive || _positioning || TreeWalk.UpFrom(e.OriginalSource as DependencyObject).Any(node => node is ButtonBase or Thumb)) return;
        ClearSelection();
        _pressedAt = e.GetPosition(Viewport);
        _anchor = SpotAt(_pressedAt);
    }

    private void OnSelectMove(object sender, MouseEventArgs e)
    {
        if (_anchor == null || e.LeftButton != MouseButtonState.Pressed) return;
        var at = e.GetPosition(Viewport);
        if (!_selecting)
        {
            if (Math.Abs(at.X - _pressedAt.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(at.Y - _pressedAt.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _selecting = true;
            Viewport.CaptureMouse();
            UpdatePausedStrip();
        }
        RestartIdle();
        if (SpotAt(at) is { } spot) _focus = spot;
        DrawSelection();
        e.Handled = true;
    }

    private void OnSelectUp(object sender, MouseButtonEventArgs e)
    {
        if (_anchor == null) return;
        if (!_selecting)
        {
            _anchor = null;
            return;
        }
        _selecting = false;
        Viewport.ReleaseMouseCapture();
        e.Handled = true;
        if (HasSelection) HoldCopyKey();
        else ClearSelection();
    }

    private Spot? SpotAt(Point inViewport)
    {
        ChatLine? found = null;
        foreach (var line in _lines)
        {
            if (!line.Box.IsLoaded) continue;
            found = line;
            var bottom = line.Box.TranslatePoint(new Point(0, line.Box.ActualHeight), Viewport).Y;
            if (inViewport.Y <= bottom) break;
        }
        if (found == null) return null;
        var text = found.Text;
        var point = Viewport.TranslatePoint(inViewport, text);
        if (point.Y < 0) return new Spot(found, text.ContentStart);
        if (point.Y > text.ActualHeight) return new Spot(found, text.ContentEnd);
        point.X = Math.Clamp(point.X, 0, Math.Max(0, text.ActualWidth - 1));
        return new Spot(found, text.GetPositionFromPoint(point, true) ?? text.ContentEnd);
    }

    private (Spot Start, Spot End) Ordered()
    {
        var a = _lines.IndexOf(_anchor!.Line);
        var b = _lines.IndexOf(_focus!.Line);
        return a < b || a == b && _anchor.Position.CompareTo(_focus.Position) <= 0 ? (_anchor, _focus) : (_focus, _anchor);
    }

    private void DrawSelection()
    {
        if (!HasSelection)
        {
            foreach (var line in _lines) Paint(line, null);
            return;
        }
        var (start, end) = Ordered();
        var first = _lines.IndexOf(start.Line);
        var last = _lines.IndexOf(end.Line);
        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            if (i < first || i > last) Paint(line, null);
            else Paint(line, (i == first ? start.Position : line.Text.ContentStart, i == last ? end.Position : line.Text.ContentEnd));
        }
    }

    private static void Paint(ChatLine line, (System.Windows.Documents.TextPointer From, System.Windows.Documents.TextPointer To)? range)
    {
        line.Selected = range;
        line.Highlight.Children.Clear();
        if (range is not { } r) return;
        foreach (var rect in ChatSelection.Rects(r.From, r.To))
        {
            var mark = new Rectangle { Width = rect.Width, Height = rect.Height, Fill = SelectionBrush, RadiusX = 2, RadiusY = 2 };
            Canvas.SetLeft(mark, rect.Left);
            Canvas.SetTop(mark, rect.Top);
            line.Highlight.Children.Add(mark);
        }
    }

    private void RepaintSelection(ChatLine line)
    {
        if (line.Selected is { } range) Paint(line, range);
    }

    private string SelectedText()
    {
        if (!HasSelection) return "";
        var (start, end) = Ordered();
        var parts = new List<string>();
        for (var i = _lines.IndexOf(start.Line); i <= _lines.IndexOf(end.Line); i++)
        {
            var line = _lines[i];
            var from = line == start.Line ? start.Position : line.Text.ContentStart;
            var to = line == end.Line ? end.Position : line.Text.ContentEnd;
            parts.Add(ChatSelection.TextBetween(from, to));
        }
        return string.Join(Environment.NewLine, parts);
    }

    private void ClearSelection()
    {
        ReleaseCopyKey();
        if (_selecting) Viewport.ReleaseMouseCapture();
        _selecting = false;
        _anchor = null;
        _focus = null;
        foreach (var line in _lines)
        {
            if (line.Selected != null) Paint(line, null);
        }
        UpdatePausedStrip();
    }

    private void ForgetSelectionOf(ChatLine line)
    {
        if (_anchor?.Line == line || _focus?.Line == line) ClearSelection();
    }

    private void CopySelection()
    {
        var text = SelectedText();
        if (text.Length > 0) CopyText(text);
        ReleaseCopyKey();
    }

    private void CopyText(string text)
    {
        if (!ClipboardText.TrySet(text)) return;
        CopiedStrip.Visibility = Visibility.Visible;
        UpdatePausedStrip();
        _copiedTimer.Stop();
        _copiedTimer.Start();
    }

    private void HoldCopyKey()
    {
        if (_copyKeyHeld || _typing || _handle == IntPtr.Zero) return;
        _copyKeyHeld = NativeMethods.RegisterHotKey(_handle, CopyKeyId, NativeMethods.ModControl | NativeMethods.ModNoRepeat, CopyKey);
        if (!_copyKeyHeld) Log.Write("Ctrl+C for the chat selection is taken by another program.");
    }

    private void ReleaseCopyKey()
    {
        if (!_copyKeyHeld) return;
        _copyKeyHeld = false;
        if (_handle != IntPtr.Zero) NativeMethods.UnregisterHotKey(_handle, CopyKeyId);
    }

    private IntPtr OnCopyKey(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WmHotkey || wParam.ToInt32() != CopyKeyId) return IntPtr.Zero;
        handled = true;
        CopySelection();
        return IntPtr.Zero;
    }
}
