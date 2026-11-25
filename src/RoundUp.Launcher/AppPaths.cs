using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace RoundUp.Launcher;

internal static class AppPaths
{
    internal static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoundUp");
    internal static readonly string LauncherExe = Path.Combine(Root, "RoundUpLauncher.exe");
    internal static readonly string AppDirectory = Path.Combine(Root, "app");
    internal static readonly string AppExe = Path.Combine(AppDirectory, "RoundUp.exe");
    internal static readonly string Projects = Path.Combine(Root, "projects");
    internal static readonly string Photos = Path.Combine(Root, "photos");
    internal static readonly string Imports = Path.Combine(Root, "imports");
    internal static readonly string Backups = Path.Combine(Root, "backups");
    internal static readonly string AppBackups = Path.Combine(Backups, "app");
    internal static readonly string ProjectBackups = Path.Combine(Backups, "projects");
    internal static readonly string Updates = Path.Combine(Root, "updates");
    internal static readonly string Logs = Path.Combine(Root, "logs");
    internal static readonly string LocalVersion = Path.Combine(AppDirectory, "version.txt");

    internal static void CreateDirectories()
    {
        foreach (var path in new[] { Root, AppDirectory, Projects, Photos, Imports,
                                      Backups, AppBackups, ProjectBackups, Updates, Logs })
            Directory.CreateDirectory(path);
    }
}

internal static class AppBootstrap
{
    internal static async System.Threading.Tasks.Task PrepareInstalledCopyAsync()
    {
        var current = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(current) ||
            !Path.GetFileName(current).Equals("RoundUpLauncher.exe", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            if (!Path.GetFullPath(current).Equals(
                    Path.GetFullPath(AppPaths.LauncherExe), StringComparison.OrdinalIgnoreCase))
            {
                // A 100+ MB self-contained exe must never delay the first window.
                await System.Threading.Tasks.Task.Run(() =>
                    File.Copy(current, AppPaths.LauncherExe, overwrite: true));
            }
            // Continuation runs on the WinForms UI thread (STA), as required by WScript.Shell.
            CreateDesktopShortcut();
            LauncherLog.Write("Installed launcher prepared: " + AppPaths.LauncherExe);
        }
        catch (Exception ex)
        {
            LauncherLog.Write("Shortcut or installed copy failed (nonfatal): " + ex);
        }
    }

    private static void CreateDesktopShortcut()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop))
            return;
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
            return;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(Path.Combine(desktop, "RoundUp.lnk"));
        shortcut.TargetPath = AppPaths.LauncherExe;
        shortcut.WorkingDirectory = AppPaths.Root;
        shortcut.IconLocation = AppPaths.LauncherExe + ",0";
        shortcut.Description = "RoundUp — об'їзди, точки, карта";
        shortcut.Save();
    }
}
