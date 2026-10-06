using System.Runtime.CompilerServices;
using System.Windows;

namespace Deviant.Desktop.Tests;

internal static class WindowTestLifetime
{
    private static readonly ConditionalWeakTable<Window,Completion> completions=new();
    public static void Observe(Window window)=>_ = completions.GetValue(window,Create);
    public static bool IsClosed(Window window)=>completions.TryGetValue(window,out var completion)&&completion.Task.IsCompletedSuccessfully;
    public static Task WaitClosedAsync(Window window)
    {Observe(window);return completions.GetValue(window,Create).Task.WaitAsync(TimeSpan.FromSeconds(5));}
    public static Task CloseAsync(Window window)
    {Observe(window);if(!IsClosed(window))window.Close();return WaitClosedAsync(window);}
    private static Completion Create(Window window)
    {
        var source=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? handler=null;handler=(_,_)=>{window.Closed-=handler;source.TrySetResult();};window.Closed+=handler;
        return new(source.Task);
    }
    private sealed record Completion(Task Task);
}
