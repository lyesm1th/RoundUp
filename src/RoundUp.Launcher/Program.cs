using System;
using System.Windows.Forms;

namespace RoundUp.Launcher;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        AppPaths.CreateDirectories();

        if (AppBootstrap.InstallAndRelaunchIfNeeded())
            return;

        Application.Run(new LauncherForm());
    }
}
