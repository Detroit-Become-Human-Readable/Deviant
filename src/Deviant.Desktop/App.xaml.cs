using System.Windows;
using Deviant.Browser;
using Deviant.Assets;
using System.IO;
using System.Reflection;

namespace Deviant.Desktop;

public partial class App : Application
{
    public bool? ThemeOverride { get; set; }
    public Func<AudioWindow>? AudioWindowFactory { get; set; }
    public Func<IUiDispatcher, IBrowserController>? ControllerFactory { get; set; }
    public Action<MainWindow>? WindowInitialized { get; set; }
    protected override void OnStartup(StartupEventArgs e)
    {
        if (AudioWindowFactory is null && ControllerFactory is null && WindowInitialized is null)
        {
            try
            {
                string build = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
                WorkflowDiagnostics.Current = new LocalDiagnosticLog(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deviant", "logs"), build);
                using var startup = WorkflowDiagnostics.Begin("Application.Startup"); startup?.Complete();
            }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("Unable to start diagnostics: {0}", error); }
        }
        DispatcherUnhandledException += (_, error) => CaptureFatal("Application.Dispatcher", error.Exception);
        AppDomain.CurrentDomain.UnhandledException += OnDomainFailure;
        TaskScheduler.UnobservedTaskException += OnUnobservedFailure;
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

    private static void OnDomainFailure(object sender, UnhandledExceptionEventArgs args)
    { if (args.ExceptionObject is Exception error) CaptureFatal("Application.Fatal", error); }
    private static void OnUnobservedFailure(object? sender, UnobservedTaskExceptionEventArgs args) =>
        CaptureFatal("Application.UnobservedTask", args.Exception);
    private static void CaptureFatal(string operation, Exception error)
    {
        WorkflowDiagnostics.Failure(operation, error);
        try { WorkflowDiagnostics.Current?.FlushAsync().Wait(TimeSpan.FromSeconds(1)); }
        catch (Exception flushError) { System.Diagnostics.Trace.TraceError("Unable to flush diagnostics: {0}", flushError); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException -= OnDomainFailure;
        TaskScheduler.UnobservedTaskException -= OnUnobservedFailure;
        var log = WorkflowDiagnostics.Current; WorkflowDiagnostics.Current = null;
        if (log is not null)
        {
            try { log.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("Unable to flush diagnostics: {0}", error); }
        }
        ThemePalette.Dispose();
        base.OnExit(e);
    }
}

