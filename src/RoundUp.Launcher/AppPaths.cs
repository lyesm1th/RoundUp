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
    internal static bool InstallAndRelaunchIfNeeded()
    {
        var current = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(current) ||
            !Path.GetFileName(current).Equals("RoundUpLauncher.exe", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFullPath(current).Equals(Path.GetFullPath(AppPaths.LauncherExe), StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            File.Copy(current, AppPaths.LauncherExe, overwrite: true);
            CreateDesktopShortcut();
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.LauncherExe,
                WorkingDirectory = AppPaths.Root,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не вдалося встановити лаунчер: " + ex.Message,
                "RoundUp", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private static void CreateDesktopShortcut()
    {
        // Windows Script Host creates a native .lnk; no .bat or .cmd files required.
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop))
            return;
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
            return;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(Path.Combine(desktop, "RoundUp.lnk"));
        shortcut.TargetPath = AppPaths.LauncherExe;
        shortcut.WorkingDirectory = AppPaths.Root;
        shortcut.IconLocation = AppPaths.LauncherExe + ",0";
        shortcut.Description = "RoundUp — карта польових об'їздів";
        shortcut.Save();
    }
}
