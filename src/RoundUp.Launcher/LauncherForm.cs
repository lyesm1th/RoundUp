using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace RoundUp.Launcher;

internal sealed class LauncherForm : Form
{
    private readonly Label status = new();
    private readonly Label versions = new();
    private readonly Button primary = new();
    private readonly Button retry = new();
    private readonly ProgressBar progress = new();
    private ReleaseInfo? latest;
    private bool busy;

    internal LauncherForm()
    {
        Text = "RoundUp — запуск і оновлення";
        ClientSize = new Size(564, 448);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(16, 25, 33);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10f);

        var logo = new Panel
        {
            Location = new Point(44, 42),
            Size = new Size(78, 78),
            BackColor = BackColor
        };
        logo.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var green = new SolidBrush(Color.FromArgb(86, 208, 153));
            using var dark = new SolidBrush(Color.FromArgb(16, 25, 33));
            using var line = new Pen(Color.FromArgb(16, 25, 33), 5f);
            g.FillEllipse(green, 2, 2, 72, 72);
            g.FillEllipse(dark, 23, 23, 30, 30);
            g.DrawLine(line, 28, 50, 50, 28);
        };
        Controls.Add(logo);

        Controls.Add(new Label
        {
            Text = "ROUNDUP",
            Location = new Point(143, 42),
            Size = new Size(380, 54),
            Font = new Font("Segoe UI", 31f, FontStyle.Bold),
            ForeColor = Color.White
        });
        Controls.Add(new Label
        {
            Text = "Карта об'їздів. Вся робота на своїх місцях.",
            Location = new Point(146, 99),
            Size = new Size(390, 27),
            Font = new Font("Segoe UI", 10f),
            ForeColor = Color.FromArgb(171, 191, 198)
        });

        var box = new Panel
        {
            Location = new Point(34, 160),
            Size = new Size(496, 153),
            BackColor = Color.FromArgb(29, 42, 52)
        };
        Controls.Add(box);

        status.Text = "Перевіряємо доступність оновлень…";
        status.Location = new Point(18, 18);
        status.Size = new Size(460, 63);
        status.ForeColor = Color.FromArgb(233, 243, 239);
        status.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        box.Controls.Add(status);

        versions.Text = "Встановлена версія: " + (ReleaseUpdater.InstalledVersion ?? "немає");
        versions.Location = new Point(18, 87);
        versions.Size = new Size(460, 44);
        versions.ForeColor = Color.FromArgb(167, 186, 192);
        box.Controls.Add(versions);

        primary.Location = new Point(34, 331);
        primary.Size = new Size(360, 56);
        primary.BackColor = Color.FromArgb(81, 202, 151);
        primary.ForeColor = Color.FromArgb(12, 32, 27);
        primary.Font = new Font("Segoe UI", 14f, FontStyle.Bold);
        primary.FlatStyle = FlatStyle.Flat;
        primary.FlatAppearance.BorderSize = 0;
        primary.Enabled = false;
        primary.Text = "Перевірка…";
        primary.Click += async (_, _) => await PrimaryClickAsync();
        Controls.Add(primary);

        retry.Location = new Point(406, 331);
        retry.Size = new Size(124, 56);
        retry.BackColor = Color.FromArgb(44, 59, 70);
        retry.ForeColor = Color.FromArgb(232, 240, 241);
        retry.FlatStyle = FlatStyle.Flat;
        retry.FlatAppearance.BorderSize = 0;
        retry.Text = "Перевірити";
        retry.Click += async (_, _) => await CheckAsync();
        Controls.Add(retry);

        progress.Location = new Point(34, 400);
        progress.Size = new Size(496, 5);
        progress.Style = ProgressBarStyle.Marquee;
        progress.MarqueeAnimationSpeed = 20;
        progress.Visible = false;
        Controls.Add(progress);

        Controls.Add(new Label
        {
            Text = "Проєкти, фотографії та резервні копії — тільки на цьому комп'ютері.",
            Location = new Point(35, 417),
            Size = new Size(495, 25),
            ForeColor = Color.FromArgb(139, 162, 172),
            Font = new Font("Segoe UI", 8f)
        });

        Shown += async (_, _) => await CheckAsync();
    }

    private void SetBusy(bool value)
    {
        busy = value;
        retry.Enabled = !value;
        primary.Enabled = !value;
        progress.Visible = value;
    }

    private async System.Threading.Tasks.Task CheckAsync()
    {
        if (busy) return;
        SetBusy(true);
        status.Text = "Перевіряємо оновлення GitHub…";
        versions.Text = "Встановлена версія: " + (ReleaseUpdater.InstalledVersion ?? "немає");
        try
        {
            latest = await ReleaseUpdater.CheckAsync();
            versions.Text = "Встановлена: " + (ReleaseUpdater.InstalledVersion ?? "немає") +
                            "    •    Доступна: " + latest.Tag;
            if (ReleaseUpdater.UpdateAvailable(ReleaseUpdater.InstalledVersion, latest.Tag))
            {
                status.Text = ReleaseUpdater.InstalledVersion is null
                    ? "RoundUp готовий до першого встановлення."
                    : "Знайдено нову версію програми.";
                primary.Text = ReleaseUpdater.InstalledVersion is null ? "Встановити" : "Оновити";
            }
            else
            {
                status.Text = "У вас актуальна версія RoundUp.";
                primary.Text = "Запустити";
            }
        }
        catch (Exception ex)
        {
            latest = null;
            status.Text = ReleaseUpdater.InstalledVersion is not null
                ? "Не вдалося перевірити оновлення. Можна працювати офлайн."
                : "Наразі неможливо завантажити RoundUp.";
            versions.Text = ex is System.Net.Http.HttpRequestException
                ? "Перевірте інтернет або доступність публічного релізу GitHub."
                : ex.Message;
            primary.Text = ReleaseUpdater.InstalledVersion is not null ? "Запустити" : "Немає збірки";
        }
        finally
        {
            SetBusy(false);
            primary.Enabled = File.Exists(AppPaths.AppExe) ||
                (latest is not null && ReleaseUpdater.UpdateAvailable(ReleaseUpdater.InstalledVersion, latest.Tag));
        }
    }

    private async System.Threading.Tasks.Task PrimaryClickAsync()
    {
        if (busy) return;
        if (latest is not null &&
            ReleaseUpdater.UpdateAvailable(ReleaseUpdater.InstalledVersion, latest.Tag))
        {
            SetBusy(true);
            try
            {
                await ReleaseUpdater.InstallAsync(latest, message => status.Text = message);
                primary.Text = "Запустити";
                versions.Text = "Встановлена: " + (ReleaseUpdater.InstalledVersion ?? "немає");
                status.Text = "Оновлення встановлене. Можна запускати програму.";
            }
            catch (Exception ex)
            {
                status.Text = "Не вдалося встановити оновлення.";
                MessageBox.Show(ex.Message, "RoundUp", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "RoundUp", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
