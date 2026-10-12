using System.IO;
using System.Windows;

namespace StressLab;

public partial class App : Application
{
    public static string[] CliArgs { get; } = Environment.GetCommandLineArgs();

    public static bool IsSmoke =>
        CliArgs.Any(a => a.Equals("--smoke", StringComparison.OrdinalIgnoreCase));

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        HookGlobalExceptionNets();
        Breadcrumb($"startup — args: {string.Join(" ", CliArgs.Skip(1))}");
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "crash-log.txt"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} — {args.Exception}\n\n");
            }
            catch
            {
                // never let the breadcrumb writer itself crash the app
            }
            args.Handled = true;
        };
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    public static void Breadcrumb(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "crash-log.txt"),
                $"{DateTime.Now:HH:mm:ss.fff} [{message}]\n");
        }
        catch
        {
        }
    }

    private static void HookGlobalExceptionNets()
    {
        // Background-thread crashes: logged for diagnosis (the runtime still terminates,
        // but the breadcrumb explains exactly what happened).
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Breadcrumb($"FATAL (AppDomain): {args.ExceptionObject}");

        // Fire-and-forget task faults: never crash, always log.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Breadcrumb($"Unobserved task exception: {args.Exception?.GetBaseException()?.Message}");
            args.SetObserved();
        };
    }
}
