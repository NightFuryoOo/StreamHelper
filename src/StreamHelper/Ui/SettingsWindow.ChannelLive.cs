using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using StreamHelper.Api;
using StreamHelper.Storage;
using static StreamHelper.Ui.Freezables;

namespace StreamHelper.Ui;

public partial class SettingsWindow
{
    private const double FaceSize = 18;
    private static readonly Brush DotRing = Solid(Color.FromRgb(0x1B, 0x1D, 0x23));
    private static readonly Brush FaceBack = Solid(Color.FromRgb(0x4A, 0x50, 0x60));

    private readonly Dictionary<string, string> _faceUrls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _faceWanted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageSource> _faces = new(StringComparer.Ordinal);
    private readonly List<(string Id, ChannelAvatar Avatar)> _faceSlots = new();
    private bool _facesLoading;
    private readonly System.Windows.Threading.DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private IReadOnlyDictionary<string, LiveStream> _live = new Dictionary<string, LiveStream>();
    private bool _liveLoading;

    private FrameworkElement ChannelContent(string text, string id, bool onAir)
    {
        var avatar = new ChannelAvatar(text, FaceSize, 10, FaceBack);
        avatar.Root.Margin = new Thickness(0, 0, 6, 0);
        if (onAir)
        {
            avatar.Root.Children.Add(new Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = LiveDot,
                Stroke = DotRing,
                StrokeThickness = 1.5,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -3, -3, 0),
            });
        }
        ShowFace(id, avatar);
        _faceSlots.Add((id, avatar));
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(avatar.Root);
        panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private void ShowFace(string id, ChannelAvatar avatar)
    {
        if (!_faceUrls.TryGetValue(id, out var url) || url.Length == 0) return;
        if (FaceOf(url) is { } picture) avatar.Show(picture);
        else _ = LoadFaceAsync(url);
    }

    private ImageSource? FaceOf(string url)
    {
        if (_faces.TryGetValue(url, out var kept)) return kept;
        if (_services.Images.Get(url) is not { } frames) return null;
        _faces[url] = frames.Frames[0];
        return frames.Frames[0];
    }

    private async Task LoadFacesAsync(IReadOnlyList<string> ids)
    {
        foreach (var id in ids) _faceWanted.Add(id);
        if (_facesLoading) return;
        _facesLoading = true;
        try
        {
            while (true)
            {
                var missing = _faceWanted.Where(id => !_faceUrls.ContainsKey(id)).ToList();
                if (missing.Count > 0)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var users = await _services.Twitch.GetUsersAsync(missing, cts.Token);
                    foreach (var id in missing) _faceUrls[id] = "";
                    foreach (var user in users) _faceUrls[user.Id] = user.ProfileImageUrl;
                }
                var urls = _faceWanted.Select(id => _faceUrls[id])
                    .Where(url => url.Length > 0 && FaceOf(url) == null)
                    .Distinct()
                    .ToList();
                if (urls.Count > 0)
                {
                    await Task.WhenAll(urls.Select(LoadFaceAsync));
                    Log.Write($"Channel pictures: {urls.Count(url => FaceOf(url) != null)} of {urls.Count} loaded.");
                }
                if (_faceWanted.All(_faceUrls.ContainsKey)) break;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Channel pictures failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _facesLoading = false;
        }
    }

    private async Task LoadFaceAsync(string url)
    {
        if (await _services.Images.LoadAsync(url) is not { } frames) return;
        _faces[url] = frames.Frames[0];
        foreach (var (id, avatar) in _faceSlots)
        {
            if (_faceUrls.TryGetValue(id, out var wanted) && wanted == url) avatar.Show(frames.Frames[0]);
        }
    }

    private void StartLiveWatch()
    {
        _liveTimer.Tick += (_, _) => _ = RefreshLiveAsync();
        _liveTimer.Start();
    }

    private void StopLiveWatch() => _liveTimer.Stop();

    private async Task RefreshLiveAsync()
    {
        var settings = _services.Settings.Current;
        if (_liveLoading || !settings.HasTwitchTokens) return;
        var ids = (_moderated ?? Array.Empty<ModeratedChannel>()).Select(c => c.Id)
            .Concat(settings.ViewerChannels.Select(c => c.Id))
            .Append(settings.IsOwnChannel ? "" : settings.TwitchChannelId)
            .Where(id => id.Length > 0)
            .Distinct()
            .ToList();
        _ = LoadFacesAsync(ids);
        var live = new Dictionary<string, LiveStream>();
        if (ids.Count > 0)
        {
            _liveLoading = true;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                foreach (var stream in await _services.Twitch.GetLiveStreamsAsync(ids, cts.Token)) live[stream.UserId] = stream;
            }
            catch (Exception ex) when (ex is HttpRequestException or AuthRequiredException or OperationCanceledException or System.Text.Json.JsonException)
            {
                Log.Write("Live channels failed: " + ex.Message);
                return;
            }
            finally
            {
                _liveLoading = false;
            }
        }
        var changed = !live.Keys.ToHashSet().SetEquals(_live.Keys);
        _live = live;
        if (changed && IsLoaded) RefreshChannelBlock();
    }
}
