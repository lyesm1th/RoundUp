using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RoundUp.Launcher;

internal sealed record ReleaseInfo(string Tag, string DownloadUrl, string Sha256);

internal static class ReleaseUpdater
{
    // Public release feed needed for anonymous updates. Never embed a GitHub token in an EXE.
    internal const string ReleaseRepository = "lyesm1th/RoundUp";
    private const string ZipAsset = "RoundUp-App-win-x64.zip";
    private const string ManifestAsset = "roundup-update.json";
    private static readonly HttpClient Client = CreateHttpClient();

    internal static string? InstalledVersion =>
        File.Exists(AppPaths.AppExe)
            ? (File.Exists(AppPaths.LocalVersion)
                ? File.ReadAllText(AppPaths.LocalVersion).Trim()
                : "0.0.0")
            : null;

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RoundUpUpdater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    internal static bool UpdateAvailable(string? installed, string online)
    {
        if (installed is null) return true;
        var a = installed.Trim().TrimStart('v', 'V');
        var b = online.Trim().TrimStart('v', 'V');
        if (Version.TryParse(a, out var installedV) && Version.TryParse(b, out var onlineV))
            return onlineV > installedV;
        return !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task<ReleaseInfo> CheckAsync()
    {
        var endpoint = "https://api.github.com/repos/" + ReleaseRepository + "/releases/latest";
        // A stalled network must not trap the user on the launch screen for 20 minutes.
        using var checkTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var response = await Client.GetAsync(endpoint, checkTimeout.Token);
        response.EnsureSuccessStatusCode();
        using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = release.RootElement;
        var tag = root.GetProperty("tag_name").GetString();
        if (string.IsNullOrWhiteSpace(tag))
            throw new InvalidDataException("У релізі GitHub відсутній номер версії.");

        var assetMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var link = asset.GetProperty("browser_download_url").GetString();
            if (name is not null && link is not null)
                assetMap[name] = link;
        }
        if (!assetMap.TryGetValue(ZipAsset, out var zipUrl) ||
            !assetMap.TryGetValue(ManifestAsset, out var manifestUrl))
            throw new InvalidDataException("У релізі бракує архіву або маніфесту оновлення.");

        ValidateDownloadUrl(zipUrl);
        ValidateDownloadUrl(manifestUrl);
        var manifestText = await Client.GetStringAsync(manifestUrl, checkTimeout.Token);
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(manifestText,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (manifest is null ||
            !string.Equals(manifest.Version?.Trim().TrimStart('v', 'V'),
                tag.TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.Archive, ZipAsset, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.Executable, "RoundUp.exe", StringComparison.OrdinalIgnoreCase) ||
            manifest.Sha256 is null || manifest.Sha256.Length != 64 ||
            !manifest.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Маніфест оновлення некоректний.");
        return new ReleaseInfo(tag, zipUrl, manifest.Sha256.ToLowerInvariant());
    }

    private static void ValidateDownloadUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps ||
            !parsed.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !parsed.AbsolutePath.StartsWith(
                "/" + ReleaseRepository + "/releases/download/",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Посилання на оновлення не належить погодженому GitHub-репозиторію.");
    }

    internal static async Task InstallAsync(ReleaseInfo release, Action<string> setStatus)
    {
        if (Process.GetProcessesByName("RoundUp").Any() ||
            Process.GetProcessesByName("GeoZamir").Any())
            throw new InvalidOperationException("Спочатку закрийте RoundUp / GeoZamir, потім повторіть оновлення.");

        var workDir = Path.Combine(AppPaths.Updates, "staging-" + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(workDir, "app");
        var zipPath = Path.Combine(workDir, ZipAsset);
        Directory.CreateDirectory(staging);

        try
        {
            setStatus("Завантаження оновлення…");
            using (var response = await Client.GetAsync(release.DownloadUrl,
                        HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(zipPath);
                await response.Content.CopyToAsync(file);
            }

            setStatus("Перевірка цілісності пакета…");
            using (var file = File.OpenRead(zipPath))
            {
                using var sha = SHA256.Create();
                var actual = Convert.ToHexString(sha.ComputeHash(file)).ToLowerInvariant();
                if (!string.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA-256 не збігається. Оновлення скасоване.");
            }

            setStatus("Розпакування та підготовка…");
            var absoluteStaging = Path.GetFullPath(staging);
            var prefix = absoluteStaging.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    var full = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                    if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                        !full.Equals(absoluteStaging, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Архів містить небезпечний шлях: " + entry.FullName);
                }
                zip.ExtractToDirectory(staging);
            }

            if (!File.Exists(Path.Combine(staging, "RoundUp.exe")))
                throw new InvalidDataException("У пакеті немає RoundUp.exe. Поточна програма не змінена.");

            File.WriteAllText(Path.Combine(staging, "version.txt"), release.Tag);
            setStatus("Встановлення нової версії…");
            var backup = Path.Combine(AppPaths.AppBackups,
                "app-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") +
                "-" + Guid.NewGuid().ToString("N")[..6]);
            var movedOld = false;

            if (Directory.Exists(AppPaths.AppDirectory))
            {
                if (Directory.EnumerateFileSystemEntries(AppPaths.AppDirectory).Any())
                {
                    Directory.Move(AppPaths.AppDirectory, backup);
                    movedOld = true;
                }
                else Directory.Delete(AppPaths.AppDirectory);
            }
            try
            {
                Directory.Move(staging, AppPaths.AppDirectory);
            }
            catch
            {
                // Roll back the program only. Never touch projects/photos/imports.
                if (movedOld && !Directory.Exists(AppPaths.AppDirectory))
                    Directory.Move(backup, AppPaths.AppDirectory);
                throw;
            }
            setStatus("Встановлено версію " + release.Tag);
        }
        finally
        {
            // Remove downloaded temporary content. Version backups remain untouched.
            if (Directory.Exists(workDir))
                try { Directory.Delete(workDir, recursive: true); } catch { /* retry next time */ }
        }
    }

    private sealed class ReleaseManifest
    {
        public string? Version { get; set; }
        public string? Archive { get; set; }
        public string? Executable { get; set; }
        public string? Sha256 { get; set; }
    }
}
