using System;
using System.Windows.Forms;

namespace RoundUp.Launcher;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        try
        {
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) =>
            {
                LauncherLog.Write("UI crash: " + e.Exception);
                MessageBox.Show(e.Exception.Message, "RoundUp — помилка",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                LauncherLog.Write("Fatal crash: " + e.ExceptionObject);

            AppPaths.CreateDirectories();
            LauncherLog.Write("RoundUp launcher process started");
            // Never silently exit or relaunch before showing the window.
            // Installation problems must not prevent opening the launcher.
            Application.Run(new LauncherForm());
        }
        catch (Exception ex)
        {
            LauncherLog.Write("Startup failed: " + ex);
            MessageBox.Show(
                "Не вдалося відкрити RoundUp. Подробиці записані у журнал запуску.\n\n" + ex,
                "RoundUp — помилка запуску", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
