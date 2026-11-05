using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NetScope.Desktop.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    private string? _statusMessage;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Short user-facing feedback for the current page; null when there is nothing to say.</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        protected set
        {
            if (Set(ref _statusMessage, value)) Raise(nameof(HasStatus));
        }
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
