using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace StreamHelper.Storage;

public interface ISeenItem : INotifyPropertyChanged
{
    bool Seen { get; set; }
}

public readonly record struct RemovedItem<T>(T Item, int Index);

public abstract class EventStore<T> : INotifyPropertyChanged where T : class, ISeenItem
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly HashSet<string> _persistedProperties;
    private int _unseenCount;

    protected EventStore(string path, params string[] persistedProperties)
    {
        _path = path;
        _persistedProperties = new HashSet<string>(persistedProperties);
    }

    public ObservableCollection<T> Items { get; } = new();

    public int UnseenCount
    {
        get => _unseenCount;
        private set
        {
            if (_unseenCount == value) return;
            _unseenCount = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnseenCount)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected abstract string KeyOf(T item);

    protected abstract IEnumerable<T> OrderAscending(IEnumerable<T> items);

    public IReadOnlyList<T> AddRange(IEnumerable<T> items)
    {
        var known = Items.Select(KeyOf).ToHashSet();
        var added = new List<T>();
        foreach (var item in OrderAscending(items))
        {
            if (!known.Add(KeyOf(item))) continue;
            item.PropertyChanged += OnItemChanged;
            Items.Insert(0, item);
            added.Add(item);
        }
        if (added.Count > 0) Changed();
        return added;
    }

    public int Remove(T item)
    {
        var index = Items.IndexOf(item);
        if (index < 0) return -1;
        item.PropertyChanged -= OnItemChanged;
        Items.RemoveAt(index);
        Changed();
        return index;
    }

    public void Restore(T item, int index)
    {
        if (Reinsert(item, index)) Changed();
    }

    public IReadOnlyList<RemovedItem<T>> RemoveRange(IEnumerable<T> items)
    {
        var removed = new List<RemovedItem<T>>();
        foreach (var item in items.ToList())
        {
            var index = Items.IndexOf(item);
            if (index < 0) continue;
            item.PropertyChanged -= OnItemChanged;
            Items.RemoveAt(index);
            removed.Add(new RemovedItem<T>(item, index));
        }
        if (removed.Count > 0) Changed();
        return removed;
    }

    public void RestoreRange(IReadOnlyList<RemovedItem<T>> removed)
    {
        var any = false;
        for (var i = removed.Count - 1; i >= 0; i--) any |= Reinsert(removed[i].Item, removed[i].Index);
        if (any) Changed();
    }

    private bool Reinsert(T item, int index)
    {
        var key = KeyOf(item);
        if (Items.Any(existing => KeyOf(existing) == key)) return false;
        item.PropertyChanged += OnItemChanged;
        Items.Insert(Math.Min(Math.Max(index, 0), Items.Count), item);
        return true;
    }

    protected void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var loaded = JsonSerializer.Deserialize<List<T>>(File.ReadAllText(_path)) ?? new();
            foreach (var item in loaded)
            {
                item.PropertyChanged += OnItemChanged;
                Items.Add(item);
            }
            UnseenCount = Items.Count(item => !item.Seen);
        }
        catch (Exception ex)
        {
            Log.Write($"{Path.GetFileName(_path)} load failed: {ex.Message}");
            try
            {
                File.Copy(_path, _path + ".broken", true);
            }
            catch
            {
            }
        }
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != null && _persistedProperties.Contains(e.PropertyName)) Changed();
    }

    private void Changed()
    {
        UnseenCount = Items.Count(item => !item.Seen);
        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Items.ToList(), Json));
            File.Move(tmp, _path, true);
        }
        catch (Exception ex)
        {
            Log.Write($"{Path.GetFileName(_path)} save failed: {ex.Message}");
        }
    }
}
