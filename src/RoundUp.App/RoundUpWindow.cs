using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RoundUp.App;

/// <summary>
/// A proper Windows desktop window. WebView2 is embedded in this WinForms form;
/// ordinary map navigation never opens Chrome or another external browser.
/// </summary>
internal sealed class RoundUpWindow : Form
{
    internal const string WindowTitle = "RoundUp — карта об'їздів";
    private readonly Uri _address;
    private readonly Uri _host;
    private readonly string _dataDirectory;
    private readonly string _projectsDirectory;
    private readonly Action<string> _log;
    private readonly Action _onPrimaryClosed;
    private readonly bool _primary;
    private readonly WebView2 _browser;
    private readonly Label _status;
    private bool _initialized;

    internal RoundUpWindow(
        Uri address,
        Uri host,
        string dataDirectory,
        string projectsDirectory,
        Action<string> log,
        Action onPrimaryClosed,
        bool primary)
    {
        _address = address;
        _host = host;
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
            Text = "RoundUp\n\nВідкриваємо вашу карту...",
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
        FormClosed += (_, _) =>
        {
            if (_primary) _onPrimaryClosed();
        };
    }

    // Windows 11 supports custom caption colors. Older Windows simply
    // keep their system title bar; RoundUp never draws a duplicate header.
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

    private async Task InitializeWebViewAsync()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            _log("webview-init-started");
            _log("webview-runtime: " + CoreWebView2Environment.GetAvailableBrowserVersionString());
            // WebView2 must be initialized on this Form's STA message-loop thread.
            // User profile is outside app/ and therefore survives launcher updates.
            var profile = Path.Combine(_dataDirectory, "webview");
            Directory.CreateDirectory(profile);
            _log("webview-environment-creating");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            _log("webview-control-initializing");
            await _browser.EnsureCoreWebView2Async(environment);
            _browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _browser.CoreWebView2.Settings.IsZoomControlEnabled = true;
            _browser.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
            _browser.CoreWebView2.NavigationStarting += OnNavigationStarting;
            _browser.NavigationCompleted += (_, args) =>
                _log("native-navigation-complete: " + (args.IsSuccess ? "ok" : args.WebErrorStatus.ToString()));
            _browser.Visible = true;
            _status.Visible = false;
            _browser.Source = _address;
            _log("webview-ready: " + _address);
        }
        catch (Exception e)
        {
            _browser.Visible = false;
            _status.Visible = true;
            _status.Text = "RoundUp не зміг відкрити вбудовану карту.\n\n" +
                "Потрібен Microsoft Edge WebView2 Runtime (безкоштовний компонент Windows).\n\n" +
                "Перевірте його наявність або встановіть із сайту Microsoft.\n" +
                "Деталі: %LOCALAPPDATA%\\RoundUp\\logs\\roundup-host.log";
            _log("Native WebView2 init error: " + e);
        }
    }

    private bool IsRoundUpAddress(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp &&
        uri.Host.Equals(_host.Host, StringComparison.OrdinalIgnoreCase) &&
        uri.Port == _host.Port;

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var url) ||
            IsRoundUpAddress(url) || url.Scheme == "about") return;
        args.Cancel = true;
        OpenExternalLink(url);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        // Prevent a native web popup from falling back to external Chrome.
        args.Handled = true;
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri)) return;
        if (IsRoundUpAddress(uri))
        {
            var child = new RoundUpWindow(
                uri, _host, _dataDirectory, _projectsDirectory,
                _log, _onPrimaryClosed, primary: false);
            child.Show(this);
        }
        else OpenExternalLink(uri);
    }

    private void OpenExternalLink(Uri uri)
    {
        // Links to Google Maps and other sites deliberately open in the user's
        // default browser; the RoundUp application itself stays a native window.
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return;
        try { Process.Start(new ProcessStartInfo { FileName = uri.ToString(), UseShellExecute = true }); }
        catch (Exception e) { _log("Open external link error: " + e.Message); }
    }
}
