using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Lathe.App.Infrastructure;
using Lathe.Core;

namespace Lathe.App;

public partial class App : Application
{
    public static event Action? FatalUiError;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Numbers are always entered/shown with '.' regardless of Windows locale (G-code is invariant too).
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => { if (a.ExceptionObject is Exception ex) CrashLog.Write(ex); };
        TaskScheduler.UnobservedTaskException += (_, a) => { CrashLog.Write(a.Exception); a.SetObserved(); };
        base.OnStartup(e);
        var w = new MainWindow();
        MainWindow = w;
        w.Show();
    }

    private bool _showingError;
    private DateTime _lastError;

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        CrashLog.Write(e.Exception);
        try { FatalUiError?.Invoke(); } catch { } // the main window feed-holds a running program
        // One dialog at a time, and not more than one every few seconds: a repeating fault must not
        // stack message boxes (each one pumps messages and could re-enter).
        if (_showingError || (DateTime.Now - _lastError).TotalSeconds < 3) return;
        _showingError = true; _lastError = DateTime.Now;
        try
        {
            MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}\n\nIf a program was running it has been paused (feed hold). Details were written to the log folder.",
                "LatheStudio", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _showingError = false; }
    }
}

