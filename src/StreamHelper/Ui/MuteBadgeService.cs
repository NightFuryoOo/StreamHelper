using System;
using StreamHelper.Storage;

namespace StreamHelper.Ui;

public sealed class MuteBadgeService : IPositionable
{
    private readonly SettingsStore _settings;
    private MuteBadgeWindow? _window;
    private bool _muted;
    private bool _positioning;

    public MuteBadgeService(SettingsStore settings) => _settings = settings;

    public bool IsPositioning => _positioning;

    public bool IsShown => _window != null;

    public event Action<double>? ScaleChanged;

    public void Apply(bool muted)
    {
        _muted = muted;
        Update();
    }

    public void BeginPositioning()
    {
        _positioning = true;
        Update();
    }

    public void EndPositioning()
    {
        _positioning = false;
        Update();
    }

    public void ApplyAppearance() => _window?.ApplyAppearance();

    public static bool ShouldShow(bool muted, bool iconHidden, bool positioning) => positioning || (muted && !iconHidden);

    public void Refresh() => Apply(_muted);

    public void ResetPosition()
    {
        var settings = _settings.Current;
        settings.MuteBadgeLeft = null;
        settings.MuteBadgeTop = null;
        _settings.Save();
        _window?.MoveToConfiguredPlace();
    }

    public void Close()
    {
        _positioning = false;
        _muted = false;
        Update();
    }

    private void Update()
    {
        if (!ShouldShow(_muted, _settings.Current.MuteBadgeHidden, _positioning))
        {
            _window?.Close();
            _window = null;
            return;
        }
        if (_window == null)
        {
            var window = new MuteBadgeWindow(_settings.Current);
            window.Moved += (left, top) =>
            {
                _settings.Current.MuteBadgeLeft = left;
                _settings.Current.MuteBadgeTop = top;
                _settings.Save();
            };
            window.ScaleChanging += scale => ScaleChanged?.Invoke(scale);
            window.ResizeFinished += (scale, left, top) =>
            {
                var settings = _settings.Current;
                settings.MuteBadgeScale = MuteBadgePlacement.ClampScale(scale);
                settings.MuteBadgeLeft = left;
                settings.MuteBadgeTop = top;
                _settings.Save();
            };
            _window = window;
            window.Present();
        }
        _window.SetPositioning(_positioning);
    }
}