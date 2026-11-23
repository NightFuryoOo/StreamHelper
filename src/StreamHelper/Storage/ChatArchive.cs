using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StreamHelper.Api;
using StreamHelper.Models;

namespace StreamHelper.Storage;

public sealed class ArchiveStream
{
    public string Id { get; set; } = "";
    public DateTime StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public string Title { get; set; } = "";
    public string Game { get; set; } = "";
    public int Messages { get; set; }
    public int Donations { get; set; }
    public int Subscriptions { get; set; }
    public int Bits { get; set; }
    public int Rewards { get; set; }
    public int Raids { get; set; }
    public int Clips { get; set; }
    public DateTime? ClipsCheckedUtc { get; set; }
}

public enum ArchiveKind
{
    Chat,
    Donation,
    Subscriber,
    Redemption,
    Moment,
    Deleted,
    Moderated,
    Clip,
}

public sealed class ArchiveLine
{
    public ArchiveKind Kind { get; set; }
    public DateTime AtUtc { get; set; }
    public ChatMessage? Chat { get; set; }
    public Donation? Donation { get; set; }
    public Subscriber? Subscriber { get; set; }
    public Redemption? Redemption { get; set; }
    public ChannelMoment? Moment { get; set; }
    public List<string>? Ids { get; set; }
    public ChatModeration? Moderation { get; set; }
    public StreamClip? Clip { get; set; }
}

public sealed class ArchiveContent
{
    public List<ChatMessage> Messages { get; } = new();
    public List<Donation> Donations { get; } = new();
    public List<Subscriber> Subscribers { get; } = new();
    public List<Redemption> Redemptions { get; } = new();
    public List<ChannelMoment> Moments { get; } = new();
    public List<StreamClip> Clips { get; } = new();
    public HashSet<string> Deleted { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ChatModeration> Moderation { get; } = new(StringComparer.Ordinal);
}

public sealed record ArchivedMessage(ArchiveStream Stream, ChatMessage Message, bool Deleted, ChatModeration? Moderation);

public sealed class ChatArchive : IDisposable
{
    public const string FolderName = "archive";
    public const string IndexName = "streams.json";
    private const int SaveIndexEvery = 50;

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions IndexJson = new() { WriteIndented = true };

    private readonly string _folder;
    private readonly object _gate = new();
    private readonly List<ArchiveStream> _streams;
    private readonly HashSet<string> _openKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StreamClip> _openClips = new(StringComparer.Ordinal);
    private ArchiveStream? _open;
    private StreamWriter? _writer;
    private int _unsaved;

    public ChatArchive(string folder)
    {
        _folder = folder;
        _streams = LoadIndex();
    }

    public static ChatArchive InDataFolder(string dataDirectory) => new(Path.Combine(dataDirectory, FolderName));

    public string Folder => _folder;

    public event Action? Changed;

    public ArchiveStream? Open
    {
        get
        {
            lock (_gate) return _open;
        }
    }

    public IReadOnlyList<ArchiveStream> Streams()
    {
        lock (_gate) return _streams.OrderByDescending(s => s.StartUtc).ToList();
    }

    public static string IdOf(DateTime startUtc) => startUtc.ToUniversalTime().ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);

    private string FileOf(string id) => Path.Combine(_folder, id + ".jsonl");

    public ArchiveStream Begin(DateTime startUtc, string title, string game, TimeSpan mergeWithin)
    {
        lock (_gate)
        {
            if (_open != null) return _open;
            var last = _streams.OrderBy(s => s.StartUtc).LastOrDefault();
            ArchiveStream stream;
            var sameStream = last != null && (startUtc - last.StartUtc).Duration() <= TimeSpan.FromMinutes(1);
            var soonAfter = last != null && startUtc >= last.StartUtc && (last.EndUtc == null || startUtc - last.EndUtc.Value <= mergeWithin);
            if (last != null && (sameStream || soonAfter))
            {
                stream = last;
                stream.EndUtc = null;
            }
            else
            {
                stream = new ArchiveStream { Id = IdOf(startUtc), StartUtc = startUtc };
                _streams.Add(stream);
            }
            if (title.Length > 0) stream.Title = title;
            if (game.Length > 0) stream.Game = game;
            Directory.CreateDirectory(_folder);
            _openKeys.Clear();
            _openClips.Clear();
            foreach (var line in ReadLines(stream.Id))
            {
                if (KeyOf(line) is { } key) _openKeys.Add(key);
                if (line.Kind == ArchiveKind.Clip && line.Clip is { } clip) _openClips[clip.Id] = clip;
            }
            _writer = new StreamWriter(new FileStream(FileOf(stream.Id), FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
            _open = stream;
            SaveIndex();
        }
        Changed?.Invoke();
        return _open!;
    }

    public void Describe(string title, string game)
    {
        lock (_gate)
        {
            if (_open == null || (title.Length == 0 || title == _open.Title) && (game.Length == 0 || game == _open.Game)) return;
            if (title.Length > 0) _open.Title = title;
            if (game.Length > 0) _open.Game = game;
            SaveIndex();
        }
        Changed?.Invoke();
    }

    public void End(DateTime endUtc)
    {
        lock (_gate)
        {
            if (_open == null) return;
            _open.EndUtc = endUtc;
            _writer?.Dispose();
            _writer = null;
            _open = null;
            _openKeys.Clear();
            _openClips.Clear();
            SaveIndex();
        }
        Changed?.Invoke();
    }

    public event Action<string, ArchiveLine, bool>? Written;

    public bool Write(ArchiveLine line, bool update = false)
    {
        string id;
        lock (_gate)
        {
            if (_open == null || _writer == null) return false;
            id = _open.Id;
            var key = KeyOf(line);
            if (!update && key != null && !_openKeys.Add(key)) return false;
            try
            {
                _writer.WriteLine(JsonSerializer.Serialize(line, Json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                Log.Write("Writing the stream archive failed: " + ex.Message);
                return false;
            }
            if (line.Kind == ArchiveKind.Clip && line.Clip is { } clip) _openClips[clip.Id] = clip;
            if (!update) Count(_open, line);
            if (++_unsaved >= SaveIndexEvery) SaveIndex();
        }
        Written?.Invoke(id, line, update);
        return true;
    }

    public int SaveClips(string streamId, IReadOnlyCollection<StreamClip> clips, DateTime? checkedUtc)
    {
        Dictionary<string, StreamClip>? known = null;
        lock (_gate)
        {
            if (_open?.Id == streamId) known = new Dictionary<string, StreamClip>(_openClips, StringComparer.Ordinal);
        }
        known ??= ClipsIn(streamId);
        var changes = new List<(ArchiveLine Line, bool Update)>();
        foreach (var clip in clips.Where(c => c.Id.Length > 0).GroupBy(c => c.Id, StringComparer.Ordinal).Select(g => g.Last()))
        {
            var had = known.TryGetValue(clip.Id, out var old);
            if (had && old == clip) continue;
            changes.Add((new ArchiveLine { Kind = ArchiveKind.Clip, AtUtc = clip.CreatedUtc, Clip = clip }, had));
        }
        var written = new List<(ArchiveLine Line, bool Update)>();
        lock (_gate)
        {
            var stream = _streams.FirstOrDefault(s => s.Id == streamId);
            if (stream == null) return 0;
            try
            {
                if (ReferenceEquals(stream, _open) && _writer != null)
                {
                    foreach (var change in changes)
                    {
                        if (!change.Update && !_openKeys.Add(KeyOf(change.Line)!)) continue;
                        _writer.WriteLine(JsonSerializer.Serialize(change.Line, Json));
                        _openClips[change.Line.Clip!.Id] = change.Line.Clip;
                        written.Add(change);
                    }
                }
                else if (changes.Count > 0)
                {
                    Directory.CreateDirectory(_folder);
                    using var writer = new StreamWriter(new FileStream(FileOf(streamId), FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
                    foreach (var change in changes)
                    {
                        writer.WriteLine(JsonSerializer.Serialize(change.Line, Json));
                        written.Add(change);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                Log.Write("Writing clips to the stream archive failed: " + ex.Message);
            }
            foreach (var change in written.Where(w => !w.Update)) Count(stream, change.Line);
            if (checkedUtc is { } at) stream.ClipsCheckedUtc = at;
            if (written.Count > 0 || checkedUtc != null) SaveIndex();
        }
        foreach (var change in written) Written?.Invoke(streamId, change.Line, change.Update);
        return written.Count(w => !w.Update);
    }

    private Dictionary<string, StreamClip> ClipsIn(string id)
    {
        var clips = new Dictionary<string, StreamClip>(StringComparer.Ordinal);
        foreach (var line in ReadLines(id))
        {
            if (line.Kind == ArchiveKind.Clip && line.Clip is { } clip) clips[clip.Id] = clip;
        }
        return clips;
    }

    public bool Has(ArchiveLine line)
    {
        lock (_gate) return KeyOf(line) is { } key && _openKeys.Contains(key);
    }

    public void Flush()
    {
        lock (_gate) SaveIndex();
    }

    public void CloseDangling()
    {
        lock (_gate)
        {
            var changed = false;
            foreach (var stream in _streams.Where(s => s.EndUtc == null && !ReferenceEquals(s, _open)))
            {
                var last = ReadLines(stream.Id).Select(l => l.AtUtc).DefaultIfEmpty(stream.StartUtc).Max();
                stream.EndUtc = last < stream.StartUtc ? stream.StartUtc : last;
                changed = true;
            }
            if (changed) SaveIndex();
        }
    }

    public ArchiveContent Read(string id)
    {
        var content = new ArchiveContent();
        var donations = new Dictionary<string, Donation>(StringComparer.Ordinal);
        var subscribers = new Dictionary<string, Subscriber>(StringComparer.Ordinal);
        var redemptions = new Dictionary<string, Redemption>(StringComparer.Ordinal);
        var moments = new Dictionary<string, ChannelMoment>(StringComparer.Ordinal);
        var clips = new Dictionary<string, StreamClip>(StringComparer.Ordinal);
        var messages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in ReadLines(id))
        {
            switch (line.Kind)
            {
                case ArchiveKind.Chat when line.Chat is { } chat:
                    if (chat.MessageId.Length == 0 || messages.Add(chat.MessageId)) content.Messages.Add(chat);
                    break;
                case ArchiveKind.Donation when line.Donation is { } donation:
                    donations[donation.Key] = donation;
                    break;
                case ArchiveKind.Subscriber when line.Subscriber is { } subscriber:
                    subscribers[subscriber.Key] = subscriber;
                    break;
                case ArchiveKind.Redemption when line.Redemption is { } redemption:
                    redemptions[redemption.Key] = redemption;
                    break;
                case ArchiveKind.Moment when line.Moment is { } moment:
                    moments[moment.Key] = moment;
                    break;
                case ArchiveKind.Clip when line.Clip is { Id.Length: > 0 } clip:
                    clips[clip.Id] = clip;
                    break;
                case ArchiveKind.Deleted when line.Ids != null:
                    foreach (var deleted in line.Ids) content.Deleted.Add(deleted);
                    break;
                case ArchiveKind.Moderated when line.Ids != null && line.Moderation != null:
                    foreach (var moderated in line.Ids) content.Moderation[moderated] = line.Moderation;
                    break;
            }
        }
        content.Donations.AddRange(donations.Values);
        content.Subscribers.AddRange(subscribers.Values);
        content.Redemptions.AddRange(redemptions.Values);
        content.Moments.AddRange(moments.Values);
        content.Clips.AddRange(clips.Values);
        return content;
    }

    public IReadOnlyList<ArchivedMessage> MessagesOf(string chatterKey, string? streamId = null)
    {
        var found = new List<ArchivedMessage>();
        foreach (var stream in Streams().Where(s => streamId == null || s.Id == streamId).OrderBy(s => s.StartUtc))
        {
            var content = Read(stream.Id);
            foreach (var message in content.Messages.Where(m => Sync.ChatFeed.KeyOf(m) == chatterKey).OrderBy(m => m.AtUtc))
            {
                found.Add(new ArchivedMessage(stream, message, content.Deleted.Contains(message.MessageId), content.Moderation.GetValueOrDefault(message.MessageId)));
            }
        }
        return found;
    }

    private IEnumerable<ArchiveLine> ReadLines(string id)
    {
        var file = FileOf(id);
        if (!File.Exists(file)) yield break;
        List<string> lines;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            lines = new List<string>();
            while (reader.ReadLine() is { } text) lines.Add(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Reading the stream archive failed: " + ex.Message);
            yield break;
        }
        foreach (var text in lines)
        {
            if (text.Length == 0) continue;
            ArchiveLine? line;
            try
            {
                line = JsonSerializer.Deserialize<ArchiveLine>(text, Json);
            }
            catch (JsonException)
            {
                continue;
            }
            if (line != null) yield return line;
        }
    }

    private static string? KeyOf(ArchiveLine line) => line.Kind switch
    {
        ArchiveKind.Chat when line.Chat is { } chat => chat.MessageId.Length > 0 ? "m:" + chat.MessageId : null,
        ArchiveKind.Donation when line.Donation is { } donation => "d:" + donation.Key,
        ArchiveKind.Subscriber when line.Subscriber is { } subscriber => "s:" + subscriber.Key,
        ArchiveKind.Redemption when line.Redemption is { } redemption => "r:" + redemption.Key,
        ArchiveKind.Moment when line.Moment is { } moment => "e:" + moment.Key,
        ArchiveKind.Clip when line.Clip is { Id.Length: > 0 } clip => "c:" + clip.Id,
        _ => null,
    };

    private static void Count(ArchiveStream stream, ArchiveLine line)
    {
        switch (line.Kind)
        {
            case ArchiveKind.Chat:
                stream.Messages++;
                break;
            case ArchiveKind.Donation:
                stream.Donations++;
                break;
            case ArchiveKind.Subscriber when line.Subscriber?.Kind == SubscriptionKind.Bits:
                stream.Bits++;
                break;
            case ArchiveKind.Subscriber:
                stream.Subscriptions++;
                break;
            case ArchiveKind.Redemption:
                stream.Rewards++;
                break;
            case ArchiveKind.Moment when line.Moment?.IsRaid == true:
                stream.Raids++;
                break;
            case ArchiveKind.Clip:
                stream.Clips++;
                break;
        }
    }

    private List<ArchiveStream> LoadIndex()
    {
        var path = Path.Combine(_folder, IndexName);
        try
        {
            if (!File.Exists(path)) return new List<ArchiveStream>();
            return JsonSerializer.Deserialize<List<ArchiveStream>>(File.ReadAllText(path)) ?? new List<ArchiveStream>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Write("Reading the stream archive list failed: " + ex.Message);
            return new List<ArchiveStream>();
        }
    }

    private void SaveIndex()
    {
        _unsaved = 0;
        try
        {
            Directory.CreateDirectory(_folder);
            var path = Path.Combine(_folder, IndexName);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_streams.OrderBy(s => s.StartUtc).ToList(), IndexJson));
            File.Move(tmp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Saving the stream archive list failed: " + ex.Message);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            SaveIndex();
            _writer?.Dispose();
            _writer = null;
        }
    }
}
