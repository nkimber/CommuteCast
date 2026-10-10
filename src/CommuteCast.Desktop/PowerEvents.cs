using Microsoft.Win32;
using CommuteCast.Infrastructure;

namespace CommuteCast.Desktop;

public interface IPowerEvents : IDisposable
{
    event Action<PowerModes>? Changed;
}
internal sealed class WindowsPowerEvents : IPowerEvents
{
    private readonly bool subscribed;
    public event Action<PowerModes>? Changed;
    public WindowsPowerEvents()
    {
        try { SystemEvents.PowerModeChanged += OnPower; subscribed = true; }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        { AppLogging.Failure("PowerEventRegistration", error); }
    }
    private void OnPower(object sender, PowerModeChangedEventArgs e) => Changed?.Invoke(e.Mode);
    public void Dispose() { if (subscribed) SystemEvents.PowerModeChanged -= OnPower; }
}
