using System.ComponentModel;

namespace StreamHelper.Models;

public sealed class RewardSoundChoice : INotifyPropertyChanged
{
    private string _soundName;
    private bool _picked;

    public RewardSoundChoice(string id, string title, string soundName)
    {
        Id = id;
        Title = title;
        _soundName = soundName;
    }

    public string Id { get; }
    public string Title { get; }

    public string Name => string.IsNullOrWhiteSpace(Title.Replace("​", "")) ? "Награда без названия" : Title.Replace("​", "");

    public string SoundName
    {
        get => _soundName;
        set
        {
            if (_soundName == value) return;
            _soundName = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SoundName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SoundText)));
        }
    }

    public string SoundText => _soundName.Length == 0 ? "" : "♪ " + _soundName;

    public bool Picked
    {
        get => _picked;
        set
        {
            if (_picked == value) return;
            _picked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Picked)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
