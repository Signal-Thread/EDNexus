using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using EDNexus.App.ViewModels;
using EDNexus.App.Views;

namespace EDNexus.App;

public partial class App : Application
{
    private MainWindowViewModel? _vm;
    private readonly UiExceptionPolicy _uiExceptions = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var boot = Program.Services;

            // A click-through overlay (or the Galnet/War Board windows) is a window too, and the
            // default — shut down when the *last* window closes — would keep the engine, the Twitch
            // publisher and Discord presence running behind a closed dashboard with nothing left to
            // click. The dashboard is the app: closing it ends the process and closes the rest.
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            Dispatcher.UIThread.UnhandledException += OnUiThreadUnhandledException;

            var vm = _vm = new MainWindowViewModel(boot);
            var window = new MainWindow { DataContext = vm };
            desktop.MainWindow = window;
            // Dispose is idempotent: shutdown reaches both, and the engine must be torn down exactly once.
            desktop.ShutdownRequested += (_, _) => vm.Dispose();
            desktop.Exit += (_, _) => vm.Dispose();

            vm.Start();

            // First run only: ask for consent (opt-in). Closing the dialog leaves it unasked.
            if (boot.Settings.CrashReportingEnabled is null)
                window.Opened += async (_, _) => await PromptConsentAsync(window, boot);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Last line of defence for anything that escapes onto the UI thread — a handler, a binding, a
    /// timer tick. Survivable errors are logged and reported and the app carries on; anything fatal
    /// (or a failure that will not stop recurring) is left to end the process, which still flushes
    /// crash reporting on the way out.
    /// </summary>
    private void OnUiThreadUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var swallow = _uiExceptions.ShouldSwallow(e.Exception);
        System.Diagnostics.Trace.TraceError(
            $"UI thread exception ({(swallow ? "handled" : "fatal")}): {e.Exception}");
        Program.Services.Crash.Capture(e.Exception);
        e.Handled = swallow;
    }

    private static async Task PromptConsentAsync(Window owner, Bootstrap boot)
    {
        var result = await new ConsentWindow().ShowDialog<bool?>(owner);
        if (result is bool choice)
            boot.ApplyCrashReportingChoice(choice);
    }
}
