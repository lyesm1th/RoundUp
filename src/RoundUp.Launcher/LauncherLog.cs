using System;
using System.IO;

namespace RoundUp.Launcher;

internal static class LauncherLog
{
    internal static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Logs);
            File.AppendAllText(Path.Combine(AppPaths.Logs, "launcher.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine);
        }
        catch
        {
            // Logging must never prevent the application from opening.
        }
    }
}
