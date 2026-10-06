using System.Windows.Threading;
using Deviant.Browser;

namespace Deviant.Desktop;

public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action, DispatcherPriority.DataBind).Task;
    }
}
