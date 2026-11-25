using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace RoundUp.App;

// RoundUp starts the unchanged GeoZamir core and displays its *own server*
// in WebView2. There is no ASP.NET, HTTP intermediary, or /api proxy.
internal static class Program
{
    private static readonly string Data = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoundUp");
    private static readonly string Projects = Path.Combine(Data, "projects");
    private static readonly string Logs = Path.Combine(Data, "logs");
    private static readonly HttpClient CoreClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly CancellationTokenSource Stop = new();
    private static readonly ConcurrentDictionary<string, object> ProjectLocks = new();
    private static readonly TaskCompletionSource<RoundUpWindow> WindowReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Process? core;
    private static Uri? coreUri;
    private static string? startupError;
    private static HashSet<int> knownChromeIds = [];
    private static Thread? desktopThread;

    [STAThread]
    private static async Task Main()
    {
        foreach (var path in new[]
        {
            Projects, Logs, Path.Combine(Projects, "coverage"),
            Path.Combine(Data, "photos"), Path.Combine(Data, "imports"),
            Path.Combine(Data, "backups", "projects")
        }) Directory.CreateDirectory(path);

        using var instance = new Mutex(true, @"Local\RoundUpDesktopHost_" + Environment.UserName, out bool first);
        if (!first) { ShowExistingWindow(); return; }

        try
        {
            WriteLog("RoundUp started: direct GeoZamir connection, no HTTP proxy");
            knownChromeIds = Process.GetProcessesByName("chrome").Select(p => p.Id).ToHashSet();
            StartNativeWindow();
            _ = Task.Run(InitializeCoreSafelyAsync);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, Stop.Token); }
            catch (OperationCanceledException) { }
        }
        catch (Exception error) { WriteLog("FATAL: " + error); }
        finally
        {
            Stop.Cancel();
            try
            {
                if (core is { HasExited: false }) core.Kill(entireProcessTree: true);
            }
            catch { }
            WriteLog("RoundUp stopped");
        }
    }

    private static async Task InitializeCoreSafelyAsync()
    {
        try
        {
            await LaunchCoreAsync();
            var origin = coreUri ?? throw new InvalidOperationException("Не знайдено локальний сервер інтерпретатора.");
            var token = await ReadCoreTokenAsync(origin);
            startupError = null;
            WriteLog("Direct backend ready at " + origin);
            var window = await WindowReady.Task.WaitAsync(Stop.Token);
            if (!window.IsDisposed)
                window.BeginInvoke(new Action(() => window.Connect(origin, token)));
        }
        catch (OperationCanceledException) when (Stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            startupError = error.Message;
            WriteLog("CORE STARTUP: " + error);
            if (WindowReady.Task.IsCompletedSuccessfully)
            {
                var window = WindowReady.Task.Result;
                if (!window.IsDisposed)
                    window.BeginInvoke(new Action(() => window.ShowConnectionError(error.Message)));
            }
        }
    }

    private static async Task<string> ReadCoreTokenAsync(Uri origin)
    {
        var html = await CoreClient.GetStringAsync(origin);
        var match = Regex.Match(html, @"GEOZAMIR_TOKEN\s*=\s*[""']([^""']+)[""']");
        if (!match.Success) throw new InvalidDataException("Оригінальний сервер не повернув ключ доступу.");
        return match.Groups[1].Value;
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    private static async Task HideLegacyWindowAsync()
    {
        // Old Go runtime opens a separate Chrome app window. Hide ONLY windows
        // newly started by this launch, never the user's already-open Chrome.
        var hidden = new HashSet<int>();
        for (var attempt = 0; attempt < 260 && !Stop.IsCancellationRequested; attempt++)
        {
            foreach (var process in Process.GetProcessesByName("chrome"))
            {
                try
                {
                    if (knownChromeIds.Contains(process.Id)) continue;
                    var title = process.MainWindowTitle;
                    if ((title.Contains("GeoZamir", StringComparison.OrdinalIgnoreCase) ||
                         title.Contains("ГеоЗамір", StringComparison.OrdinalIgnoreCase) ||
                         title.Contains("ГеоЗамiр", StringComparison.OrdinalIgnoreCase)) &&
                        process.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(process.MainWindowHandle, 0);
                        if (hidden.Add(process.Id))
                            WriteLog("Hidden legacy core browser window. Main UI stays in native RoundUp.");
                    }
                }
                catch { }
                finally { process.Dispose(); }
            }
            try { await Task.Delay(350, Stop.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            // Chrome may leave lockfiles; they are not project data.
            if (Path.GetFileName(file).Equals("lockfile", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);
        }
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    private static async Task LaunchCoreAsync()
    {
        var bundle = Path.Combine(AppContext.BaseDirectory, "GeoZamirCore.exe");
        if (!File.Exists(bundle))
            throw new FileNotFoundException("Не знайдено компонент інтерпретатора GeoZamirCore.exe", bundle);
        var workspace = Path.Combine(Projects, "workspace");
        Directory.CreateDirectory(workspace);
        // IMPORTANT: legacy Go core writes GeoZamir_Data NEXT TO ITS EXECUTABLE.
        // It must run from the persistent project directory, not from updateable app/.
        // Copy the small executable on upgrade; never replace its GeoZamir_Data database.
        var filename = Path.Combine(workspace, "GeoZamirCore.exe");
        if (!File.Exists(filename) ||
            !SHA256.HashData(File.ReadAllBytes(bundle)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(filename))))
            File.Copy(bundle, filename, overwrite: true);
        var oldData = Path.Combine(AppContext.BaseDirectory, "GeoZamir_Data");
        var newData = Path.Combine(workspace, "GeoZamir_Data");
        if (Directory.Exists(oldData) && !Directory.Exists(newData))
        {
            // Migrate a database from an older RoundUp bundle without removing its source.
            CopyDirectory(oldData, newData);
            WriteLog("Migrated existing local data into persistent workspace.");
        }
        // Record ports before launching the core, without relying on netstat text,
        // process IDs or Windows display language.
        var previousListeners = NewTcpEndpoints().Select(p => p.Port).ToHashSet();
        core = Process.Start(new ProcessStartInfo
        {
            FileName = filename,
            WorkingDirectory = workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        }) ?? throw new InvalidOperationException("Не вдалося відкрити ядро інтерпретатора.");
        _ = Task.Run(HideLegacyWindowAsync);
        _ = Task.Run(async () =>
        {
            while (!core.HasExited)
            {
                var line = await core.StandardOutput.ReadLineAsync();
                if (line is null) break;
                WriteLog("CORE " + line);
            }
        });
        _ = Task.Run(async () =>
        {
            while (!core.HasExited)
            {
                var line = await core.StandardError.ReadLineAsync();
                if (line is null) break;
                WriteLog("CORE ERR " + line);
            }
        });
        for (int i = 0; i < 150; ++i)
        {
            // The Go runtime may spawn a child process to own the HTTP listener.
            // We detect the server by its unique HTML signature, not by PID.
            var endpoints = NewTcpEndpoints()
                .Where(e => e.Port > 1023 &&
                            (i > 40 || !previousListeners.Contains(e.Port)))
                .OrderBy(e => previousListeners.Contains(e.Port) ? 1 : 0)
                .DistinctBy(e => e.Address + ":" + e.Port)
                .Take(80)
                .ToList();
            if (i % 15 == 0)
                WriteLog("Backend discovery pass " + i + ": " + endpoints.Count +
                         " listeners, core running=" + !core.HasExited);
            foreach (var endpoint in endpoints)
            {
                var address = endpoint.Address.AddressFamily == AddressFamily.InterNetworkV6
                    ? "[::1]" : "127.0.0.1";
                Uri candidate;
                try { candidate = new Uri("http://" + address + ":" + endpoint.Port + "/"); }
                catch { continue; }
                using var probeTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(850));
                try
                {
                    using var response = await CoreClient.GetAsync(candidate,
                        HttpCompletionOption.ResponseHeadersRead, probeTimeout.Token);
                    if (!response.IsSuccessStatusCode ||
                        response.Content.Headers.ContentLength > 250_000) continue;
                    var html = await response.Content.ReadAsStringAsync(probeTimeout.Token);
                    if (html.Contains("window.GEOZAMIR_TOKEN=", StringComparison.Ordinal) &&
                        html.Contains("<html", StringComparison.OrdinalIgnoreCase))
                    {
                        coreUri = candidate;
                        WriteLog("Original interpreter connected on port " + candidate.Port);
                        return;
                    }
                }
                catch (OperationCanceledException) { }
                catch (HttpRequestException) { }
                catch (IOException) { }
            }
            // The old executable may take longer when recovering a large database.
            await Task.Delay(550);
        }
        throw new TimeoutException("Не вдалося встановити з'єднання з локальною базою протягом 90 секунд. " +
                                   "RoundUp залишився відкритим для повторної спроби.");
    }

    private static IEnumerable<IPEndPoint> NewTcpEndpoints()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Where(x => x.Port > 0 &&
                            (IPAddress.IsLoopback(x.Address) ||
                             x.Address.Equals(IPAddress.Any) ||
                             x.Address.Equals(IPAddress.IPv6Any)))
                .ToArray();
        }
        catch (NetworkInformationException ex)
        {
            WriteLog("Cannot enumerate local listeners: " + ex.Message);
            return [];
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    private static void ShowExistingWindow()
    {
        for (int attempt = 0; attempt < 12; attempt++)
        {
            var handle = FindWindow(null, RoundUpWindow.WindowTitle);
            if (handle != IntPtr.Zero)
            {
                ShowWindow(handle, 9);
                SetForegroundWindow(handle);
                return;
            }
            Thread.Sleep(150);
        }
    }

    private static void StartNativeWindow()
    {
        desktopThread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var window = new RoundUpWindow(
                    Data, Projects, WriteLog, () => Stop.Cancel(), primary: true);
                window.Shown += (_, _) => WindowReady.TrySetResult(window);
                Application.Run(window);
            }
            catch (Exception error)
            {
                WriteLog("Native window FATAL: " + error);
                Stop.Cancel();
            }
        })
        { IsBackground = true, Name = "RoundUpDesktopSTA" };
        desktopThread.SetApartmentState(ApartmentState.STA);
        desktopThread.Start();
    }

    private static void WriteLog(string message)
    {
        try
        {
            Directory.CreateDirectory(Logs);
            lock (ProjectLocks.GetOrAdd("log", _ => new object()))
                File.AppendAllText(Path.Combine(Logs, "roundup-host.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
        }
        catch { }
    }
}
