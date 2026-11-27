using System.Diagnostics;
using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RoundUp.App;

/// <summary>
/// Native Windows window that opens the UNMODIFIED original GeoZamir server.
/// It replaces only page styling and static UI resources inside WebView2.
/// All /api/* requests go directly from WebView2 to GeoZamir: no proxy exists.
/// </summary>
internal sealed class RoundUpWindow : Form
{
    internal const string WindowTitle = "RoundUp — карта об'їздів";
    private static readonly object CoverageSync = new();
    private readonly string _dataDirectory;
    private readonly string _projectsDirectory;
    private readonly Action<string> _log;
    private readonly Action _onPrimaryClosed;
    private readonly bool _primary;
    private readonly WebView2 _browser;
    private readonly Label _status;
    private readonly string _uiDirectory = Path.Combine(AppContext.BaseDirectory, "ui");
    private Uri? _coreAddress;
    private Uri? _initialAddress;
    private string? _coreToken;
    private WorkspaceBridge? _workspaceBridge;
    private bool _webViewReady;
    private bool _navigationStarted;

    internal RoundUpWindow(
        string dataDirectory, string projectsDirectory,
        Action<string> log, Action onPrimaryClosed, bool primary)
    {
        _dataDirectory = dataDirectory;
        _projectsDirectory = projectsDirectory;
        _log = log;
        _onPrimaryClosed = onPrimaryClosed;
        _primary = primary;
        Text = primary ? WindowTitle : "RoundUp — додаткове вікно";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(860, 600);
        Size = new Size(1440, 900);
        BackColor = Color.FromArgb(12, 30, 39);
        ForeColor = Color.White;
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application; }
        catch { Icon = SystemIcons.Application; }

        _status = new Label
        {
            Dock = DockStyle.Fill,
            Text = "RoundUp\n\nЗапускаємо інтерпретатор та відкриваємо карту...",
            BackColor = Color.FromArgb(12, 30, 39),
            ForeColor = Color.FromArgb(221, 242, 231),
            Font = new Font("Segoe UI", 17, FontStyle.Regular),
            TextAlign = ContentAlignment.MiddleCenter
        };
        _browser = new WebView2
        {
            Dock = DockStyle.Fill,
            Visible = false,
            DefaultBackgroundColor = Color.FromArgb(12, 30, 39)
        };
        Controls.Add(_status);
        Controls.Add(_browser);
        Shown += (_, _) =>
        {
            _log("native-window-shown");
            if (_primary) WindowState = FormWindowState.Maximized;
        };
        Load += async (_, _) => await InitializeWebViewAsync();
        FormClosed += (_, _) => { if (_primary) _onPrimaryClosed(); };
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr handle, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int dark = 1;
            DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
            int caption = ColorTranslator.ToWin32(Color.FromArgb(18, 39, 43));
            DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
            int titleColor = ColorTranslator.ToWin32(Color.FromArgb(234, 248, 239));
            DwmSetWindowAttribute(Handle, 36, ref titleColor, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    internal void Connect(Uri server, string token, Uri? initialAddress = null)
    {
        _coreAddress = server;
        _initialAddress = initialAddress ?? server;
        _coreToken = token;
        _log("Native window connected directly to original GeoZamir at " + server);
        StartNavigation();
    }

    internal void ShowConnectionError(string error)
    {
        if (_navigationStarted) return;
        _status.Visible = true;
        _status.Text = "RoundUp\n\nНе вдалося під'єднатися до інтерпретатора.\n" +
            error + "\n\nДіагностика: %LOCALAPPDATA%\\RoundUp\\logs\\roundup-host.log";
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            _log("webview-init-started");
            _log("webview-runtime: " + CoreWebView2Environment.GetAvailableBrowserVersionString());
            var profile = Path.Combine(_dataDirectory, "webview");
            Directory.CreateDirectory(profile);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            await _browser.EnsureCoreWebView2Async(environment);
            _browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _browser.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
            _browser.CoreWebView2.NavigationStarting += OnNavigationStarting;
            _workspaceBridge = new WorkspaceBridge(_browser.CoreWebView2, _dataDirectory, _log);
            _browser.CoreWebView2.WebMessageReceived += (sender, message) =>
            {
                // Messages must originate from our own embedded local interpreter page.
                if (Uri.TryCreate(message.Source, UriKind.Absolute, out var source) && IsCoreAddress(source))
                    _workspaceBridge?.OnMessage(sender, message);
            };
            _browser.NavigationCompleted += OnNavigationCompleted;
            _webViewReady = true;
            _log("webview-ready");
            StartNavigation();
        }
        catch (Exception error)
        {
            _log("WebView2 init error: " + error);
            _browser.Visible = false;
            _status.Visible = true;
            _status.Text = "RoundUp не зміг відкрити карту.\n\n" +
                "Перевірте Microsoft Edge WebView2 Runtime та перезапустіть RoundUp.\n" +
                "%LOCALAPPDATA%\\RoundUp\\logs\\roundup-host.log";
        }
    }

    private void StartNavigation()
    {
        if (!_webViewReady || _coreAddress is null || _coreToken is null || _navigationStarted) return;
        _navigationStarted = true;
        // Every request (especially POST /api/import and PUT /api/index) goes
        // directly to the original server. Only the static appearance is swapped.
        _browser.CoreWebView2.AddWebResourceRequestedFilter(
            _coreAddress.GetLeftPart(UriPartial.Authority) + "/*",
            CoreWebView2WebResourceContext.All);
        _browser.CoreWebView2.WebResourceRequested += OnWebResourceRequested;
        _browser.Visible = true;
        _status.Visible = false;
        _browser.Source = _initialAddress ?? _coreAddress;
        _log("direct-core-navigation-started: " + _coreAddress);
    }

    private bool IsCoreAddress(Uri uri) =>
        _coreAddress is not null &&
        uri.Scheme.Equals(_coreAddress.Scheme, StringComparison.OrdinalIgnoreCase) &&
        uri.Host.Equals(_coreAddress.Host, StringComparison.OrdinalIgnoreCase) &&
        uri.Port == _coreAddress.Port;

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        try
        {
            if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri) || !IsCoreAddress(uri))
                return;
            var route = uri.AbsolutePath;
            // API and all project/media requests are NEVER intercepted.
            if (route.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) return;
            if (route.Equals("/roundup/coverage", StringComparison.OrdinalIgnoreCase) ||
                route.Equals("/roundup/profiles", StringComparison.OrdinalIgnoreCase) ||
                route.Equals("/roundup/project-trash", StringComparison.OrdinalIgnoreCase))
            {
                HandleCoverage(args, uri);
                return;
            }
            string? file = route switch
            {
                "/" or "/index.html" => "index.html",
                "/app.js" => "app.js",
                "/style.css" => "style.css",
                "/roundup-theme.css" => "roundup-theme.css",
                "/roundup-addon.js" => "roundup-addon.js",
                "/roundup-profiles.js" => "roundup-profiles.js",
                "/roundup-projects.js" => "roundup-projects.js",
                "/roundup-cloud.js" => "roundup-cloud.js",
                "/roundup-cloud.css" => "roundup-cloud.css",
                "/roundup-profiles.css" => "roundup-profiles.css",
                "/favicon.svg" => "favicon.svg",
                "/vendor/jszip.min.js" => Path.Combine("vendor", "jszip.min.js"),
                _ => null
            };
            if (file is null) return;
            var path = Path.Combine(_uiDirectory, file);
            if (!File.Exists(path)) { _log("Missing visual asset: " + path); return; }
            byte[] bytes;
            if (file == "index.html")
            {
                var html = File.ReadAllText(path)
                    .Replace("__RUNTIME_TOKEN__", System.Net.WebUtility.HtmlEncode(_coreToken));
                bytes = Encoding.UTF8.GetBytes(html);
                _log("direct-core-ui-override: original server, RoundUp HTML");
            }
            else bytes = File.ReadAllBytes(path);
            var type = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".html" => "text/html; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".js" => "application/javascript; charset=utf-8",
                ".svg" => "image/svg+xml",
                _ => "application/octet-stream"
            };
            args.Response = MakeResponse(bytes, 200, "OK", type);
        }
        catch (Exception error) { _log("Static visual override error: " + error); }
    }

    private CoreWebView2WebResourceResponse MakeResponse(
        byte[] content, int status, string reason, string type) =>
        _browser.CoreWebView2.Environment.CreateWebResourceResponse(
            new MemoryStream(content, writable: false),
            status, reason, "Content-Type: " + type + "\r\nCache-Control: no-store");

    private void HandleCoverage(CoreWebView2WebResourceRequestedEventArgs args, Uri uri)
    {
        // RoundUp-specific territory marks are private local files, not part of
        // the GeoZamir API. They are served without opening a second HTTP port.
        try
        {
            string project = "default";
            foreach (var parameter in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = parameter.Split('=', 2);
                if (pair.Length == 2 && pair[0] == "project")
                {
                    project = Uri.UnescapeDataString(pair[1].Replace("+", " "));
                    break;
                }
            }
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(project))).ToLowerInvariant();
            var profiles = uri.AbsolutePath.Equals("/roundup/profiles", StringComparison.OrdinalIgnoreCase);
            var trash = uri.AbsolutePath.Equals("/roundup/project-trash", StringComparison.OrdinalIgnoreCase);
            var folder = Path.Combine(_projectsDirectory, trash ? "settings" : profiles ? "profiles" : "coverage");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, trash ? "trashed-projects.json" : hash + ".json");
            var method = args.Request.Method.ToUpperInvariant();
            if (method == "GET")
            {
                var bytes = File.Exists(path)
                    ? File.ReadAllBytes(path)
                    : Encoding.UTF8.GetBytes(trash
                        ? "{\"version\":1,\"deleted\":[]}"
                        : profiles
                        ? "{\"version\":1,\"managers\":[],\"assignments\":{},\"sourceAssignments\":{},\"routes\":[]}"
                        : "{\"version\":1,\"shapes\":[],\"cells\":[]}");
                args.Response = MakeResponse(bytes, 200, "OK", "application/json; charset=utf-8");
                return;
            }
            if (method == "PUT")
            {
                using var memory = new MemoryStream();
                args.Request.Content?.CopyTo(memory);
                if (memory.Length > (trash ? 200_000 : profiles ? 16_000_000 : 4_000_000))
                {
                    args.Response = MakeResponse(Encoding.UTF8.GetBytes("{\"error\":\"too large\"}"),
                        413, "Payload Too Large", "application/json");
                    return;
                }
                using var document = JsonDocument.Parse(memory.ToArray());
                var root = document.RootElement;
                if (trash)
                {
                    if (!root.TryGetProperty("deleted", out var removed) || removed.ValueKind != JsonValueKind.Array || removed.GetArrayLength() > 1000)
                        throw new JsonException("Invalid trash payload");
                    foreach (var entry in removed.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(entry.GetString()) || entry.GetString()!.Length > 180)
                            throw new JsonException("Invalid trashed project ID");
                    }
                }
                else if (profiles)
                {
                    if (!root.TryGetProperty("managers", out var managers) || managers.ValueKind != JsonValueKind.Array ||
                        !root.TryGetProperty("assignments", out var assignments) || assignments.ValueKind != JsonValueKind.Object ||
                        !root.TryGetProperty("routes", out var routes) || routes.ValueKind != JsonValueKind.Array)
                        throw new JsonException("Invalid profiles payload");
                }
                else if (!root.TryGetProperty("shapes", out var shapes) ||
                    shapes.ValueKind != JsonValueKind.Array ||
                    !root.TryGetProperty("cells", out var cells) ||
                    cells.ValueKind != JsonValueKind.Array)
                    throw new JsonException("Invalid coverage shape");
                lock (CoverageSync)
                {
                    var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllBytes(temporary, memory.ToArray());
                    File.Move(temporary, path, overwrite: true);
                }
                args.Response = MakeResponse(Encoding.UTF8.GetBytes("{\"saved\":true}"),
                    200, "OK", "application/json");
                return;
            }
            args.Response = MakeResponse([], 405, "Method Not Allowed", "text/plain");
        }
        catch (Exception error)
        {
            _log("Coverage local file error: " + error);
            args.Response = MakeResponse(Encoding.UTF8.GetBytes("{\"error\":\"local storage\"}"),
                500, "Internal Server Error", "application/json");
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!args.IsSuccess) { _log("Direct core navigation failed: " + args.WebErrorStatus); return; }
        try
        {
            var title = await _browser.CoreWebView2.ExecuteScriptAsync("document.title");
            if (title.Contains("RoundUp", StringComparison.OrdinalIgnoreCase))
            {
                _log("direct-core-ui-ready: " + title);
                var profileReady = await _browser.CoreWebView2.ExecuteScriptAsync(
                    "Boolean(window.RoundUpProfiles && typeof window.RoundUpProfiles.beforeImport === 'function' && typeof window.RoundUpProfiles.allowsRecord === 'function' && typeof window.RoundUpProfiles.isDrawing === 'function' && typeof window.RoundUpCoverage?.isDrawing === 'function' && document.getElementById('routeDrawMapBtn') && document.querySelector('.ru-map-route-controls'))");
                if (profileReady == "true") _log("roundup-profiles-ready: managers, dates, routes");
                else _log("RoundUp profile startup error: " + profileReady);
                var projectsReady = await _browser.CoreWebView2.ExecuteScriptAsync(
                    "Boolean(window.RoundUpProjectTrash && typeof window.RoundUpProjectTrash.deleteProject === 'function')");
                if (projectsReady == "true") _log("roundup-projects-ready: named projects and recoverable trash");
                else _log("RoundUp projects startup error: " + projectsReady);
            }
            else
                _log("Direct core loaded unexpected title: " + title);
        }
        catch (Exception error) { _log("Direct core page check: " + error.Message); }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) ||
            IsCoreAddress(uri) || uri.Scheme == "about") return;
        args.Cancel = true;
        OpenExternalLink(uri);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri)) return;
        if (IsCoreAddress(uri) && _coreAddress is not null && _coreToken is not null)
        {
            var child = new RoundUpWindow(
                _dataDirectory, _projectsDirectory, _log, _onPrimaryClosed, primary: false);
            child.Connect(_coreAddress, _coreToken, uri);
            child.Show(this);
        }
        else OpenExternalLink(uri);
    }

    private void OpenExternalLink(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return;
        try { Process.Start(new ProcessStartInfo { FileName = uri.ToString(), UseShellExecute = true }); }
        catch (Exception error) { _log("Open external link error: " + error.Message); }
    }
}
