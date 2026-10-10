using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommuteCast.Desktop;
using CommuteCast.Infrastructure;

namespace CommuteCast.Desktop.Tests;

[CollectionDefinition("Desktop", DisableParallelization = true)]
public class DesktopCollection { }

/// <summary>Real WPF bindings on one STA dispatcher, isolated from the user's application and data.</summary>
internal static class DesktopHost
{
    private static readonly TaskCompletionSource<Dispatcher> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    static DesktopHost()
    {
        var thread = new Thread(() =>
        {
            try
            {
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                App.ToggleTheme(); App.ToggleTheme();
                Ready.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            }
            catch (Exception error) { Ready.TrySetException(error); }
        }) { IsBackground = true, Name = "Isolated WPF acceptance" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
    }
    public static async Task Run(Func<Task> action)
    {
        var dispatcher = await Ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await action(); result.SetResult(); }
            catch (Exception error) { result.SetException(error); }
        }));
        await result.Task.WaitAsync(TimeSpan.FromSeconds(90));
    }
    public static Workspace Workspace() => new(Path.GetFullPath(Path.Combine("artifacts", "desktop-tests", Guid.NewGuid().ToString("N"))));
    public static void Layout(Window window, double width = 1380, double height = 900)
    {
        window.Width = width; window.Height = height;
        window.Measure(new Size(width, height)); window.Arrange(new Rect(0, 0, width, height)); window.UpdateLayout();
    }
}
