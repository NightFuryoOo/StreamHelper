using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace StreamHelper.Storage;

public interface ISelectable : INotifyPropertyChanged
{
    bool Selected { get; set; }

    bool CanSelect { get; }
}

public sealed class SelectionTracker<T> : INotifyPropertyChanged where T : class, ISelectable
{
    private readonly ObservableCollection<T> _items;

    public SelectionTracker(ObservableCollection<T> items)
    {
        _items = items;
        foreach (var item in items) item.PropertyChanged += OnItemChanged;
        items.CollectionChanged += OnCollectionChanged;
    }

    public int Count => _items.Count(IsPicked);

    public bool HasAny => Count > 0;

    public bool AllSelected
    {
        get
        {
            var eligible = _items.Count(item => item.CanSelect);
            return eligible > 0 && Count == eligible;
        }
    }

    public string ToggleAllText => AllSelected ? "Снять выбор" : "Выбрать все";

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<T> Picked() => _items.Where(IsPicked).ToList();

    public void ToggleAll()
    {
        var select = !AllSelected;
        foreach (var item in _items.ToList()) item.Selected = select && item.CanSelect;
    }

    private static bool IsPicked(T item) => item.Selected && item.CanSelect;

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ISelectable.Selected) or nameof(ISelectable.CanSelect)) RaiseAll();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (T item in e.OldItems) item.PropertyChanged -= OnItemChanged;
        if (e.NewItems != null) foreach (T item in e.NewItems) item.PropertyChanged += OnItemChanged;
        RaiseAll();
    }

    private void RaiseAll()
    {
        Raise(nameof(Count));
        Raise(nameof(HasAny));
        Raise(nameof(AllSelected));
        Raise(nameof(ToggleAllText));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
