using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Http.Headers;
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
    private static Thread? trayThread;

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
            OpenBrowser("http://127.0.0.1:" + PreferredPort + "/");
            return;
        }

        try
        {
            WriteLog("RoundUp started");
            await LaunchCoreAsync();
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
                Results.Json(new { name = "RoundUp", ready = coreUri is not null, version = "6.0.0" }));
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
            app.MapFallback(async ctx =>
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
                            var html = Encoding.UTF8.GetString(bytes);
                            var token = await ReadCoreTokenAsync();
                            html = html.Replace("__RUNTIME_TOKEN__", System.Net.WebUtility.HtmlEncode(token));
                            bytes = Encoding.UTF8.GetBytes(html);
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
            StartTray();
            OpenBrowser(localUri.ToString());
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

    private static async Task LaunchCoreAsync()
    {
        var filename = Path.Combine(AppContext.BaseDirectory, "GeoZamirCore.exe");
        if (!File.Exists(filename))
            throw new FileNotFoundException("Не знайдено компонент інтерпретатора GeoZamirCore.exe",filename);
        var workspace = Path.Combine(Projects, "workspace");
        Directory.CreateDirectory(workspace);
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
        for (int i = 0; i < 65; ++i)
        {
            if (core.HasExited) throw new InvalidOperationException("Ядро інтерпретатора завершилось з кодом " + core.ExitCode);
            var ports = await ListenPorts(core.Id);
            foreach (var port in ports)
            {
                try
                {
                    var candidate = new Uri("http://127.0.0.1:" + port + "/");
                    var response = await CoreClient.GetStringAsync(candidate);
                    if (response.Contains("GEOZAMIR_TOKEN", StringComparison.Ordinal))
                    {
                        coreUri = candidate;
                        WriteLog("Legacy interpreter ready (port " + port + ")");
                        return;
                    }
                }
                catch { }
            }
            await Task.Delay(420);
        }
        throw new TimeoutException("Ядро інтерпретатора не відкрило свій локальний вебсервер за 30 секунд.");
    }

    private static async Task<List<int>> ListenPorts(int pid)
    {
        using var probe = Process.Start(new ProcessStartInfo
        {
            FileName = "netstat.exe",
            Arguments = "-ano -p tcp",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (probe is null) return [];
        var output = await probe.StandardOutput.ReadToEndAsync();
        await probe.WaitForExitAsync();
        var ports = new List<int>();
        foreach (var line in output.Split('\n'))
        {
            var m = Regex.Match(line, @"^\s*TCP\s+(?:127\.0\.0\.1|0\.0\.0\.0):(\d+)\s+\S+\s+\S+\s+(\d+)\s*$",
                RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[2].Value, out var owner) && owner == pid &&
                int.TryParse(m.Groups[1].Value, out var port) && port > 0)
                ports.Add(port);
        }
        return ports;
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

    private static void StartTray()
    {
        trayThread = new Thread(() =>
        {
            try
            {
                using var menu = new ContextMenuStrip();
                menu.Items.Add("Відкрити RoundUp", null, (_, _) => OpenBrowser(localUri?.ToString() ?? ""));
                menu.Items.Add("Відкрити папку проєктів", null, (_, _) =>
                    Process.Start(new ProcessStartInfo { FileName = Projects, UseShellExecute = true }));
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Закрити RoundUp", null, (_, _) =>
                {
                    Stop.Cancel();
                    System.Windows.Forms.Application.ExitThread();
                });
                using var tray = new NotifyIcon
                {
                    Text = "RoundUp — карта об'їздів",
                    Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application,
                    Visible = true,
                    ContextMenuStrip = menu
                };
                tray.DoubleClick += (_, _) => OpenBrowser(localUri?.ToString() ?? "");
                System.Windows.Forms.Application.Run(new ApplicationContext());
                tray.Visible = false;
            }
            catch (Exception ex) { WriteLog("Tray error: " + ex.Message); }
        })
        { IsBackground = true, Name = "RoundUpTray" };
        trayThread.SetApartmentState(ApartmentState.STA);
        trayThread.Start();
    }

    private static void OpenBrowser(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            var chrome = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
            }.FirstOrDefault(File.Exists);
            Process.Start(chrome is not null
                ? new ProcessStartInfo { FileName = chrome, Arguments = "--new-window \"" + url + "\"", UseShellExecute = true }
                : new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex) { WriteLog("Browser error: " + ex.Message); }
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
