using System.Windows.Threading;

namespace NetScope.Desktop.Services;

/// <summary>Marshals session callbacks onto the UI thread; tests use the inline implementation.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action)
    {
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}

public sealed class InlineUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}
