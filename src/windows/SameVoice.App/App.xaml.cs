using System.IO;
using System.Threading;
using System.Windows;

namespace SameVoice.App;

public partial class App : Application
{
    private const string ShowEventName = @"Local\SameVoice.ShowWindow";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private Thread? _showThread;
    private bool _unhandledDialogShown;
    internal static string LogPath => Path.Combine(AppContext.BaseDirectory, "startup.log");

    internal static void Log(string message)
    {
#if DEBUG
        try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}"); }
        catch { /* ignore */ }
#endif
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Log("OnStartup begin");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log("AppDomain unhandled: " + args.ExceptionObject);
        _instanceMutex = new Mutex(true, @"Local\SameVoice.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            Log("already running; asking the existing instance to show its window");
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { /* first instance is not ready yet */ }
            Shutdown();
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showThread = new Thread(() =>
        {
            try
            {
                while (_showEvent.WaitOne())
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (MainWindow is MainWindow window) window.ShowFromTray();
                    });
                }
            }
            catch
            {
                // Application is shutting down.
            }
        })
        {
            IsBackground = true,
            Name = "SameVoice-ShowWindow"
        };
        _showThread.Start();

        base.OnStartup(e);
        Log("base startup");
        DispatcherUnhandledException += (_, args) =>
        {
            Log("Dispatcher unhandled: " + args.Exception);
            args.Handled = true;
            if (_unhandledDialogShown) return;
            _unhandledDialogShown = true;
            try
            {
                MessageBox.Show(
                    "同声遇到了一个未处理的问题：\n" + args.Exception.Message +
                    "\n\n后续同类错误将只记录，不再重复弹窗。",
                    "同声 SameVoice", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch
            {
                // Never let error reporting itself create a crash loop.
            }
        };

        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            Log("window shown");
        }
        catch (Exception ex)
        {
            Log("window creation failed: " + ex);
            throw;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log("OnExit " + e.ApplicationExitCode);
        try { _showEvent?.Dispose(); } catch { /* ignore */ }
        _showEvent = null;
        try
        {
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
        }
        catch { /* ignore */ }
        base.OnExit(e);
    }
}
