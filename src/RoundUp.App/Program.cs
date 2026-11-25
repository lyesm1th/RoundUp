using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RoundUp.App;

internal static class Program
{
    private const int PreferredPort = 45672;
    private static readonly string Data = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoundUp");
    private static readonly string Projects = Path.Combine(Data, "projects");
    private static readonly string Coverage = Path.Combine(Projects, "coverage");
    private static readonly string Logs = Path.Combine(Data, "logs");
    private static readonly string Ui = Path.Combine(AppContext.BaseDirectory, "ui");
    private static readonly HttpClient CoreClient = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromMinutes(8) };
    private static readonly CancellationTokenSource Stop = new();
    private static readonly SemaphoreSlim SaveLock = new(1, 1);
    private static readonly ConcurrentDictionary<string, object> ProjectLocks = new();
    private static Process? core;
    private static Uri? coreUri;
    private static Uri? localUri;
    private static Thread? desktopThread;
    private static string? startupError;
    private static HashSet<int> knownChromeIds = [];

    [STAThread]
    private static async Task Main(string[] args)
    {
        Directory.CreateDirectory(Projects);
        Directory.CreateDirectory(Coverage);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Path.Combine(Data, "photos"));
        Directory.CreateDirectory(Path.Combine(Data, "imports"));
        Directory.CreateDirectory(Path.Combine(Data, "backups", "projects"));

        // A second click on the shortcut reopens the existing local instance.
        using var instance = new Mutex(true, @"Local\RoundUpDesktopHost_" + Environment.UserName, out bool first);
        if (!first)
        {
            ShowExistingWindow();
            return;
        }

        try
        {
            WriteLog("RoundUp started");
            knownChromeIds = Process.GetProcessesByName("chrome").Select(p => p.Id).ToHashSet();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = AppContext.BaseDirectory
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o =>
            {
                o.Limits.MaxRequestBodySize = null;
                o.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(4);
            });
            var port = FindPort();
            localUri = new Uri("http://127.0.0.1:" + port + "/");
            builder.WebHost.UseUrls(localUri.ToString());
            var app = builder.Build();

            app.MapGet("/roundup/status", () =>
                Results.Json(new { name = "RoundUp", ready = coreUri is not null, error = startupError, version = "6.1.0" }));
            app.MapGet("/roundup/coverage", (HttpContext ctx) =>
            {
                var path = CoveragePath(ctx.Request.Query["project"].ToString());
                return Results.File(File.Exists(path)
                    ? File.ReadAllBytes(path)
                    : Encoding.UTF8.GetBytes("{\"version\":1,\"shapes\":[],\"cells\":[]}"),
                    contentType: "application/json");
            });
            app.MapPut("/roundup/coverage", async (HttpContext ctx) =>
            {
                // Browser-only local API, JSON with a custom same-origin header.
                if (!string.Equals(ctx.Request.Headers["X-RoundUp-Local"], "1", StringComparison.Ordinal))
                    return Results.StatusCode(403);
                if (ctx.Request.ContentLength > 4_000_000) return Results.StatusCode(413);
                using var memory = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(memory);
                if (memory.Length > 4_000_000) return Results.StatusCode(413);
                memory.Position = 0;
                try
                {
                    using var doc = await JsonDocument.ParseAsync(memory);
                    if (!doc.RootElement.TryGetProperty("shapes", out var shapes) ||
                        shapes.ValueKind != JsonValueKind.Array ||
                        !doc.RootElement.TryGetProperty("cells", out var cells) ||
                        cells.ValueKind != JsonValueKind.Array ||
                        shapes.GetArrayLength() > 2000 || cells.GetArrayLength() > 25000)
                        return Results.BadRequest("Некоректні геодані охоплення.");
                }
                catch (JsonException) { return Results.BadRequest("Некоректний JSON."); }
                var path = CoveragePath(ctx.Request.Query["project"].ToString());
                var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await SaveLock.WaitAsync();
                try
                {
                    await File.WriteAllBytesAsync(tmp, memory.ToArray());
                    File.Move(tmp, path, true);  // atomic replace on same volume
                }
                finally { SaveLock.Release(); }
                return Results.Json(new { saved = true });
            });
            app.Map("/{**path}", async ctx =>
            {
                var path = ctx.Request.Path.Value ?? "/";
                if (ctx.Request.Method is "GET" or "HEAD" &&
                    (path == "/" || path == "/index.html" || SafeAsset(path) is { } && File.Exists(SafeAsset(path))))
                {
                    var asset = path == "/" || path == "/index.html" ? Path.Combine(Ui, "index.html") : SafeAsset(path);
                    if (asset is not null && File.Exists(asset))
                    {
                        var bytes = await File.ReadAllBytesAsync(asset);
                        if (path == "/" || path == "/index.html")
                        {
                            if (coreUri is null)
                                bytes = Encoding.UTF8.GetBytes(StartingPage);
                            else
                            {
                                var html = Encoding.UTF8.GetString(bytes);
                                var token = await ReadCoreTokenAsync();
                                html = html.Replace("__RUNTIME_TOKEN__", System.Net.WebUtility.HtmlEncode(token));
                                bytes = Encoding.UTF8.GetBytes(html);
                            }
                        }
                        ctx.Response.ContentType = ContentType(asset);
                        ctx.Response.Headers.CacheControl = "no-store";
                        ctx.Response.ContentLength = bytes.Length;
                        if (ctx.Request.Method != "HEAD") await ctx.Response.Body.WriteAsync(bytes);
                        return;
                    }
                }
                await ProxyAsync(ctx);
            });
            await app.StartAsync(Stop.Token);
            WriteLog("RoundUp UI listening at " + localUri);
            StartNativeWindow();
            _ = Task.Run(InitializeCoreSafelyAsync);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, Stop.Token); }
            catch (OperationCanceledException) { }
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await app.StopAsync(stopTimeout.Token);
        }
        catch (Exception error)
        {
            WriteLog("FATAL: " + error);
            MessageBox.Show("Не вдалося запустити RoundUp.\n\n" + error.Message +
                "\n\nДеталі: " + Path.Combine(Logs, "roundup-host.log"),
                "RoundUp — помилка запуску", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Stop.Cancel();
            if (core is { HasExited: false })
            {
                try { core.Kill(entireProcessTree: true); } catch { }
            }
            WriteLog("RoundUp stopped");
        }
    }


    private const string StartingPage = """
<!doctype html><html lang="uk"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta http-equiv="Cache-Control" content="no-store"><title>RoundUp — підготовка карти</title>
<style>*{box-sizing:border-box}body{margin:0;height:100vh;display:grid;place-items:center;background:linear-gradient(120deg,#0c1d2e,#133c42);color:#f1fff8;font-family:Segoe UI,Arial,sans-serif}
main{max-width:460px;margin:22px;padding:42px;text-align:center;background:#173c45;border:1px solid #29646a;border-radius:25px;box-shadow:0 20px 70px #051b28aa}
.logo{font-size:38px;font-weight:800;letter-spacing:-1px}.logo span{color:#84e5b9}.pin{font-size:43px;color:#72deb2}h1{font-size:22px;font-weight:700;margin:28px 0 10px}
p{line-height:1.6;color:#bfd5d7;font-size:15px}.loader{width:32px;height:32px;border:3px solid #496f72;border-top-color:#87e9bb;border-radius:50%;animation:spin 1s linear infinite;margin:27px auto}
@keyframes spin{to{transform:rotate(360deg)}}#hint{font-size:12px;color:#91b8ba}</style></head>
<body><main><div class="pin">◉</div><div class="logo">Round<span>Up</span></div>
<h1>Готуємо вашу карту</h1><p>Підключаємо локальну базу проєктів, фотографій та геоміток.</p>
<div class="loader"></div><p id="hint">Зазвичай це займає кілька секунд. Не закривайте RoundUp.</p></main>
<script>setInterval(async()=>{try{const r=await fetch('/roundup/status',{cache:'no-store'});const s=await r.json();
if(s.ready){location.replace('/?started=1');return}
if(s.error)document.querySelector('#hint').textContent='Ядро не відповідає. Закрийте вікно RoundUp та запустіть програму знову. Журнал: %LOCALAPPDATA%\\RoundUp\\logs\\roundup-host.log';
}catch(_){}},1000)</script></body></html>
""";

    private static async Task InitializeCoreSafelyAsync()
    {
        try
        {
            await LaunchCoreAsync();
            startupError = null;
            // The legacy Chrome app is hidden separately; no window swap here.
        }
        catch (Exception ex)
        {
            startupError = ex.Message;
            WriteLog("CORE STARTUP: " + ex);
            // Never kill the new RoundUp application when the legacy backend is slow.
            // The loading screen, project folders and diagnostic logs stay available.
        }
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

    private static string? SafeAsset(string requestPath)
    {
        var relative = Uri.UnescapeDataString(requestPath).TrimStart('/');
        if (relative.Length == 0 || relative.Contains('\\') ||
            relative.Split('/').Any(p => p is "." or "..")) return null;
        var absolute = Path.GetFullPath(Path.Combine(Ui, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.GetFullPath(Ui).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return absolute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? absolute : null;
    }

    private static string ContentType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "application/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".json" => "application/json",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream"
    };

    private static string CoveragePath(string project)
    {
        // A deterministic hash prevents project IDs from ever escaping this folder.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(project))).ToLowerInvariant();
        return Path.Combine(Coverage, hash + ".json");
    }

    private static async Task<string> ReadCoreTokenAsync()
    {
        if (coreUri is null) throw new InvalidOperationException("Сервер інтерпретатора недоступний.");
        var html = await CoreClient.GetStringAsync(coreUri);
        var match = Regex.Match(html, @"GEOZAMIR_TOKEN\s*=\s*[""']([^""']+)[""']");
        if (!match.Success) throw new InvalidDataException("Не вдалося отримати локальний ключ доступу до інтерпретатора.");
        return match.Groups[1].Value;
    }

    private static async Task ProxyAsync(HttpContext ctx)
    {
        if (coreUri is null)
        {
            ctx.Response.StatusCode = 503;
            await ctx.Response.WriteAsync("Інтерпретатор запускається...");
            return;
        }
        var remote = new Uri(coreUri, ctx.Request.Path.Value?.TrimStart('/') + ctx.Request.QueryString);
        using var request = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), remote);
        if (ctx.Request.ContentLength > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
            request.Content = new StreamContent(ctx.Request.Body);
        foreach (var pair in ctx.Request.Headers)
        {
            if (pair.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            if (!request.Headers.TryAddWithoutValidation(pair.Key, (IEnumerable<string>)pair.Value))
                request.Content?.Headers.TryAddWithoutValidation(pair.Key, (IEnumerable<string>)pair.Value);
        }
        try
        {
            using var response = await CoreClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
            ctx.Response.StatusCode = (int)response.StatusCode;
            foreach (var pair in response.Headers)
                if (!pair.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
                    !pair.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                    ctx.Response.Headers[pair.Key] = pair.Value.ToArray();
            foreach (var pair in response.Content.Headers)
                if (!pair.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    ctx.Response.Headers[pair.Key] = pair.Value.ToArray();
            ctx.Response.Headers.Remove("transfer-encoding");
            if (response.Headers.Location is { IsAbsoluteUri: true } redirect &&
                redirect.Host == coreUri.Host && redirect.Port == coreUri.Port && localUri is not null)
                ctx.Response.Headers.Location = new Uri(localUri, redirect.PathAndQuery).ToString();
            if (ctx.Request.Method != "HEAD")
                await response.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = 502;
                await ctx.Response.WriteAsync("З'єднання з інтерпретатором тимчасово недоступне.");
            }
            WriteLog("Proxy error: " + ex.Message);
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
                .Where(e => e.Port > 1023 && e.Port != localUri?.Port &&
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

    private static int FindPort()
    {
        for (int port = PreferredPort; port < PreferredPort + 8; port++)
        {
            try
            {
                using var socket = new TcpListener(IPAddress.Loopback, port);
                socket.Start();
                return port;
            }
            catch (SocketException) { }
        }
        throw new IOException("Локальні порти RoundUp зайняті.");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    private static void ShowExistingWindow()
    {
        // Reopen the actual Windows app instead of launching Chrome on a second click.
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var handle = FindWindow(null, RoundUpWindow.WindowTitle);
            if (handle != IntPtr.Zero)
            {
                ShowWindow(handle, 9); // SW_RESTORE if minimized
                SetForegroundWindow(handle);
                return;
            }
            Thread.Sleep(150);
        }
    }

    private static void StartNativeWindow()
    {
        // Main() resumes after awaits on a worker thread. WebView2 requires
        // a separate dedicated STA thread with a live WinForms message pump.
        desktopThread = new Thread(() =>
        {
            try
            {
                System.Windows.Forms.Application.EnableVisualStyles();
                System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
                using var window = new RoundUpWindow(
                    localUri!, localUri!, Data, Projects, WriteLog,
                    () => Stop.Cancel(), primary: true);
                System.Windows.Forms.Application.Run(window);
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
