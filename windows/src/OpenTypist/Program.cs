using System.IO;
using System.Text;
using System.Windows;

namespace OpenTypist;

public static class Program
{
    private const string MutexName = @"Local\OpenTypist.Windows";
    public const string ShowSettingsEventName = @"Local\OpenTypist.Windows.ShowSettings";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--selftest-offline")) { AttachConsole(); return SelfTest.RunOffline(args); }
        if (args.Contains("--selftest")) { AttachConsole(); return SelfTest.Run(args); }
        if (args.Contains("--download")) { AttachConsole(); return SelfTest.Download(args); }
        if (args.Contains("--calibrate")) { AttachConsole(); return SelfTest.Calibrate(args); }
        if (args.Contains("--update-test")) { AttachConsole(); return SelfTest.UpdateTest(args); }
        if (args.Contains("--snapshot-settings")) { AttachConsole(); return SelfTest.SnapshotSettings(args); }
        // The new version, run from the download folder, swapping itself in.
        if (args.Contains("--finish-update")) return Updater.FinishUpdate(args);

        // One instance; opening the app again shows Settings, the way back in
        // when the tray icon is hidden in the overflow area.
        using var mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            try
            {
                // This process was just started by the user, so it may bring
                // windows to the front; let the running instance do it.
                Win32.AllowSetForegroundWindow(Win32.ASFW_ANY);
                using var show = EventWaitHandle.OpenExisting(ShowSettingsEventName);
                show.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            return 0;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // The engine and the downloader post their callbacks to the context
        // they were created on; make that the UI thread.
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        using var tray = new TrayApp(app);
        app.Startup += (_, _) =>
        {
            tray.Start(showSettings: args.Contains("--settings"));
            Updater.CleanUpAfterUpdate(args);
        };
        app.Exit += (_, _) => tray.Shutdown();
        return app.Run();
    }

    /// --selftest and friends print to the terminal they were started from.
    /// This is a GUI app, so it has no console of its own; output that is
    /// piped or redirected works as is.
    private static void AttachConsole()
    {
        if (!Console.IsOutputRedirected && Win32.AttachConsole(Win32.ATTACH_PARENT_PROCESS))
        {
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.SetError(stdout);
            Console.WriteLine();
        }
    }
}
