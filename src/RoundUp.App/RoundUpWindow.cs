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

        var toolbar = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            BackColor = Color.FromArgb(19, 45, 51),
            ForeColor = Color.FromArgb(239, 252, 246),
            Renderer = new ToolStripProfessionalRenderer(),
            Padding = new Padding(12, 5, 12, 5),
            AutoSize = true
        };
        var brand = new ToolStripLabel("◉  RoundUp")
        {
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(137, 236, 183),
            Margin = new Padding(0, 0, 24, 0)
        };
        var homeButton = MakeButton("⌂  Карта", () => _browser.CoreWebView2?.Navigate(_host.ToString()));
        var refreshButton = MakeButton("⟳  Оновити", () => _browser.CoreWebView2?.Reload());
        var folderButton = MakeButton("▣  Мої проєкти", OpenProjectsDirectory);
        toolbar.Items.Add(brand);
        toolbar.Items.Add(homeButton);
        toolbar.Items.Add(refreshButton);
        toolbar.Items.Add(folderButton);

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
        Controls.Add(toolbar);

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

    private ToolStripButton MakeButton(string text, Action click)
    {
        var item = new ToolStripButton(text)
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ForeColor = Color.FromArgb(230, 248, 240),
            Font = new Font("Segoe UI", 9, FontStyle.Regular),
            AutoSize = true,
            Margin = new Padding(0, 0, 8, 0),
            Padding = new Padding(7, 4, 7, 4)
        };
        item.Click += (_, _) => click();
        return item;
    }

    private void OpenProjectsDirectory()
    {
        try
        {
            Directory.CreateDirectory(_projectsDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _projectsDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception e) { _log("Open projects error: " + e.Message); }
    }

    private async Task InitializeWebViewAsync()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            // WebView2 must be initialized on this Form's STA message-loop thread.
            // User profile is outside app/ and therefore survives launcher updates.
            var profile = Path.Combine(_dataDirectory, "webview");
            Directory.CreateDirectory(profile);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
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
