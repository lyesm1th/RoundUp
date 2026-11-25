using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RoundUp.Launcher;

internal sealed class LauncherForm : Form
{
    private static readonly Color Background = Color.FromArgb(12, 21, 29);
    private static readonly Color Surface = Color.FromArgb(22, 36, 47);
    private static readonly Color Stroke = Color.FromArgb(45, 65, 78);
    private static readonly Color Muted = Color.FromArgb(154, 174, 184);
    private static readonly Color Mint = Color.FromArgb(100, 226, 176);
    private static readonly Color White = Color.FromArgb(239, 248, 246);

    private readonly Label statusTitle = new();
    private readonly Label statusDetail = new();
    private readonly Label installedVersion = new();
    private readonly Label availableVersion = new();
    private readonly RoundedButton primary = new(true);
    private readonly RoundedButton retry = new(false);
    private readonly ProgressBar progress = new();
    private ReleaseInfo? latest;
    private bool busy;

    internal LauncherForm()
    {
        Text = "RoundUp — запуск і оновлення";
        ClientSize = new Size(612, 504);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;
        ForeColor = White;
        Font = new Font("Segoe UI", 10f);
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        // Use the branded embedded EXE icon instead of WinForms' default window icon.
        try
        {
            if (Environment.ProcessPath is { } exe)
                Icon = Icon.ExtractAssociatedIcon(exe) ?? Icon;
        }
        catch (Exception ex)
        {
            LauncherLog.Write("Window icon fallback: " + ex.Message);
        }

        var logo = new Panel { Location = new Point(35, 31), Size = new Size(79, 79), BackColor = Background };
        logo.Paint += PaintRoundUpLogo;
        Controls.Add(logo);

        Controls.Add(new Label
        {
            Text = "RoundUp",
            Location = new Point(127, 35),
            Size = new Size(315, 54),
            Font = new Font("Segoe UI", 30f, FontStyle.Bold),
            ForeColor = White,
            BackColor = Background
        });
        Controls.Add(new Label
        {
            Text = "Карта об'їздів та контроль охоплення територій",
            Location = new Point(130, 87),
            Size = new Size(446, 24),
            Font = new Font("Segoe UI", 10.2f),
            ForeColor = Muted,
            BackColor = Background
        });

        var statusCard = new RoundedPanel
        {
            Location = new Point(35, 137),
            Size = new Size(542, 140),
            FillColor = Surface,
            BorderColor = Stroke,
            CornerRadius = 18
        };
        Controls.Add(statusCard);

        var stateDot = new Panel
        {
            Location = new Point(25, 26),
            Size = new Size(19, 19),
            BackColor = Surface
        };
        stateDot.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(Mint);
            e.Graphics.FillEllipse(brush, 3, 3, 13, 13);
        };
        statusCard.Controls.Add(stateDot);

        statusTitle.Text = "Перевірка оновлень";
        statusTitle.Location = new Point(56, 23);
        statusTitle.Size = new Size(459, 40);
        statusTitle.ForeColor = White;
        statusTitle.BackColor = Surface;
        statusTitle.Font = new Font("Segoe UI", 15f, FontStyle.Bold);
        statusCard.Controls.Add(statusTitle);

        statusDetail.Text = "З'єднуємося з GitHub…";
        statusDetail.Location = new Point(26, 79);
        statusDetail.Size = new Size(487, 42);
        statusDetail.Font = new Font("Segoe UI", 10f);
        statusDetail.ForeColor = Muted;
        statusDetail.BackColor = Surface;
        statusCard.Controls.Add(statusDetail);

        var currentBox = CreateVersionCard(35, "ВСТАНОВЛЕНА ВЕРСІЯ", out installedVersion);
        var remoteBox = CreateVersionCard(313, "ДОСТУПНА ВЕРСІЯ", out availableVersion);
        Controls.Add(currentBox);
        Controls.Add(remoteBox);
        installedVersion.Text = ReleaseUpdater.InstalledVersion ?? "Не встановлена";
        availableVersion.Text = "Перевіряємо";

        primary.Location = new Point(35, 369);
        primary.Size = new Size(377, 58);
        primary.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
        primary.Text = "Перевіряємо…";
        primary.Enabled = false;
        primary.Click += async (_, _) => await PrimaryClickAsync();
        Controls.Add(primary);

        retry.Location = new Point(424, 369);
        retry.Size = new Size(153, 58);
        retry.Font = new Font("Segoe UI", 10.1f, FontStyle.Bold);
        retry.Text = "Оновити статус";
        retry.Click += async (_, _) => await CheckAsync();
        Controls.Add(retry);

        progress.Location = new Point(35, 441);
        progress.Size = new Size(542, 5);
        progress.Style = ProgressBarStyle.Marquee;
        progress.MarqueeAnimationSpeed = 28;
        progress.Visible = false;
        Controls.Add(progress);

        Controls.Add(new Label
        {
            Text = "●  Ваші проєкти, фото й архіви залишаються на цьому комп'ютері.",
            Location = new Point(37, 463),
            Size = new Size(541, 24),
            Font = new Font("Segoe UI", 9.2f),
            ForeColor = Muted,
            BackColor = Background
        });

        Shown += async (_, _) =>
        {
            LauncherLog.Write("ui-shown");
            _ = AppBootstrap.PrepareInstalledCopyAsync();
            await CheckAsync();
        };
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var gradient = new LinearGradientBrush(ClientRectangle,
            Color.FromArgb(18, 37, 44), Background, LinearGradientMode.Vertical);
        e.Graphics.FillRectangle(gradient, ClientRectangle);
    }

    private RoundedPanel CreateVersionCard(int x, string caption, out Label value)
    {
        var panel = new RoundedPanel
        {
            Location = new Point(x, 289),
            Size = new Size(264, 65),
            FillColor = Color.FromArgb(18, 30, 40),
            BorderColor = Color.FromArgb(31, 48, 59),
            CornerRadius = 13
        };
        panel.Controls.Add(new Label
        {
            Text = caption,
            Location = new Point(17, 11),
            Size = new Size(232, 18),
            Font = new Font("Segoe UI", 8.2f, FontStyle.Bold),
            ForeColor = Muted,
            BackColor = panel.FillColor
        });
        value = new Label
        {
            Location = new Point(17, 30),
            Size = new Size(235, 28),
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            ForeColor = White,
            BackColor = panel.FillColor
        };
        panel.Controls.Add(value);
        return panel;
    }

    private static void PaintRoundUpLogo(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var tile = Drawing.MakeRounded(new Rectangle(1, 1, 76, 76), 20);
        using var tileBrush = new LinearGradientBrush(new Rectangle(0, 0, 78, 78),
            Color.FromArgb(59, 156, 125), Color.FromArgb(16, 81, 78), 48f);
        g.FillPath(tileBrush, tile);
        using var path = new GraphicsPath();
        path.AddEllipse(23, 15, 32, 32);
        path.AddLine(51, 39, 39, 64);
        path.AddLine(39, 64, 27, 39);
        path.CloseFigure();
        using var outer = new SolidBrush(Color.FromArgb(134, 247, 197));
        g.FillPath(outer, path);
        using var inner = new SolidBrush(Color.FromArgb(18, 67, 66));
        g.FillEllipse(inner, 31, 23, 16, 16);
        using var white = new SolidBrush(Color.FromArgb(223, 252, 240));
        g.FillEllipse(white, 35, 27, 8, 8);
    }

    private void SetBusy(bool value)
    {
        busy = value;
        retry.Enabled = !value;
        primary.Enabled = !value;
        progress.Visible = value;
    }

    private async Task CheckAsync()
    {
        if (busy) return;
        SetBusy(true);
        statusTitle.Text = "Перевіряємо оновлення";
        statusDetail.Text = "Шукаємо останню збірку RoundUp на GitHub…";
        installedVersion.Text = ReleaseUpdater.InstalledVersion ?? "Не встановлена";
        availableVersion.Text = "Перевіряємо";

        try
        {
            latest = await ReleaseUpdater.CheckAsync();
            availableVersion.Text = latest.Tag;
            if (ReleaseUpdater.UpdateAvailable(ReleaseUpdater.InstalledVersion, latest.Tag))
            {
                statusTitle.Text = ReleaseUpdater.InstalledVersion is null
                    ? "RoundUp готовий до встановлення"
                    : "Доступне оновлення";
                statusDetail.Text = "Нову збірку можна завантажити одним натисканням.";
                primary.Text = ReleaseUpdater.InstalledVersion is null ? "Встановити RoundUp" : "Оновити RoundUp";
            }
            else
            {
                statusTitle.Text = "Усе актуально";
                statusDetail.Text = "Встановлена остання версія. Можна працювати.";
                primary.Text = "Запустити RoundUp";
            }
            LauncherLog.Write("update-check-success: " + latest.Tag);
        }
        catch (Exception ex)
        {
            latest = null;
            LauncherLog.Write("update-check-failed: " + ex);
            statusTitle.Text = ReleaseUpdater.InstalledVersion is not null
                ? "Офлайн-режим доступний"
                : "Збірку поки не знайдено";
            statusDetail.Text = ReleaseUpdater.InstalledVersion is not null
                ? "Не вдалося перевірити GitHub. Запускайте встановлену програму."
                : "Поки доступний лише лаунчер. Повний інтерпретатор ще не опублікований.";
            availableVersion.Text = "Недоступна";
            primary.Text = ReleaseUpdater.InstalledVersion is not null
                ? "Запустити RoundUp" : "Збірка недоступна";
        }
        finally
        {
            SetBusy(false);
            installedVersion.Text = ReleaseUpdater.InstalledVersion ?? "Не встановлена";
            primary.Enabled = File.Exists(AppPaths.AppExe) ||
                (latest is not null &&
                ReleaseUpdater.UpdateAvailable(ReleaseUpdater.InstalledVersion, latest.Tag));
        }
    }

    private async Task PrimaryClickAsync()
    {
        if (busy) return;

        if (latest is not null &&
            ReleaseUpdater.UpdateAvailable(ReleaseUpdater.InstalledVersion, latest.Tag))
        {
            SetBusy(true);
            try
            {
                await ReleaseUpdater.InstallAsync(latest, message => statusDetail.Text = message);
                statusTitle.Text = "Оновлення встановлено";
                statusDetail.Text = "Тепер можна запустити RoundUp.";
                installedVersion.Text = ReleaseUpdater.InstalledVersion ?? "Не встановлена";
                primary.Text = "Запустити RoundUp";
                LauncherLog.Write("installation-success: " + latest.Tag);
            }
            catch (Exception ex)
            {
                LauncherLog.Write("installation-failed: " + ex);
                statusTitle.Text = "Помилка оновлення";
                statusDetail.Text = "Попередню версію збережено. Повторіть спробу.";
                MessageBox.Show(ex.Message, "RoundUp — оновлення",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                SetBusy(false);
                primary.Enabled = File.Exists(AppPaths.AppExe);
            }
            return;
        }

        if (!File.Exists(AppPaths.AppExe))
        {
            await CheckAsync();
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.AppExe,
                WorkingDirectory = AppPaths.AppDirectory,
                UseShellExecute = true
            });
            LauncherLog.Write("app-start-success");
            Close();
        }
        catch (Exception ex)
        {
            LauncherLog.Write("app-start-failed: " + ex);
            MessageBox.Show(ex.Message, "RoundUp — запуск",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal static class Drawing
{
    internal static GraphicsPath MakeRounded(Rectangle bounds, int radius)
    {
        var r = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, r, r, 180, 90);
        path.AddArc(bounds.Right - r, bounds.Y, r, r, 270, 90);
        path.AddArc(bounds.Right - r, bounds.Bottom - r, r, r, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - r, r, r, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class RoundedPanel : Panel
{
    internal Color FillColor { get; set; } = Color.FromArgb(22, 36, 47);
    internal Color BorderColor { get; set; } = Color.FromArgb(45, 65, 78);
    internal int CornerRadius { get; set; } = 16;

    internal RoundedPanel()
    {
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Drawing.MakeRounded(new Rectangle(1, 1, Width - 3, Height - 3), CornerRadius);
        using var fill = new SolidBrush(FillColor);
        using var stroke = new Pen(BorderColor, 1f);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(stroke, path);
    }
}

internal sealed class RoundedButton : Button
{
    private readonly bool accent;

    internal RoundedButton(bool accent)
    {
        this.accent = accent;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var normal = accent ? Color.FromArgb(100, 226, 176) : Color.FromArgb(38, 57, 69);
        var inactive = Color.FromArgb(55, 69, 76);
        using var fill = new SolidBrush(Enabled ? normal : inactive);
        using var path = Drawing.MakeRounded(new Rectangle(0, 0, Width - 1, Height - 1), 13);
        e.Graphics.FillPath(fill, path);
        if (!accent)
        {
            using var stroke = new Pen(Color.FromArgb(68, 90, 99), 1f);
            e.Graphics.DrawPath(stroke, path);
        }
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle,
            Enabled && accent ? Color.FromArgb(9, 44, 36) : Color.FromArgb(223, 236, 236),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine);
    }
}
