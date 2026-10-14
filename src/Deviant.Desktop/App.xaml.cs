using System.Windows;
using Deviant.Browser;

namespace Deviant.Desktop;

public partial class App : Application
{
    public bool? ThemeOverride { get; set; }
    public Func<AudioWindow>? AudioWindowFactory { get; set; }
    public Func<IUiDispatcher, IBrowserController>? ControllerFactory { get; set; }
    public Action<MainWindow>? WindowInitialized { get; set; }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemePalette.Initialize(this, ThemeOverride);
        var ui = new WpfUiDispatcher(Dispatcher);
        if (ControllerFactory is null && WindowInitialized is null)
        {
            var audioWindow = AudioWindowFactory?.Invoke() ?? new AudioWindow();
            if (AudioWindowFactory is null)
                audioWindow.SourceInitialized += (_, _) => WindowPlacement.ApplyInitial(audioWindow);
            MainWindow = audioWindow;
            audioWindow.Show();
        }
        else
        {
            var window = new MainWindow(ControllerFactory?.Invoke(ui) ?? new BrowserController(ui));
            MainWindow = window;
            WindowInitialized?.Invoke(window);
            window.Show();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ThemePalette.Dispose();
        base.OnExit(e);
    }
}

