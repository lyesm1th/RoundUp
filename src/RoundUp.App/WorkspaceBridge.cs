using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace RoundUp.App;

/// <summary>
/// Native bridge for the optional Cloudflare workspace. No request to the
/// original GeoZamir backend is intercepted or forwarded through a proxy.
/// Cloud API tokens stay in the Windows user DPAPI store, never in WebView JS.
/// </summary>
internal sealed class WorkspaceBridge
{
    private static readonly Uri ApiBase = new("https://roundup-workspace-api.moriarty-soa.workers.dev");
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(18) };
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RoundUp.Workspace.UserToken.v1");
    private readonly CoreWebView2 _web;
    private readonly Action<string> _log;
    private readonly string _tokenFile;
    private string? _token;

    internal WorkspaceBridge(CoreWebView2 web, string dataDirectory, Action<string> log)
    {
        _web = web;
        _log = log;
        string settings = Path.Combine(dataDirectory, "settings");
        Directory.CreateDirectory(settings);
        _tokenFile = Path.Combine(settings, "workspace-token.dat");
        _token = ReadToken();
    }

    private string? ReadToken()
    {
        try
        {
            if (!File.Exists(_tokenFile)) return null;
            var ciphertext = File.ReadAllBytes(_tokenFile);
            var plain = ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
            var token = Encoding.UTF8.GetString(plain);
            return IsToken(token) ? token : null;
        }
        catch (Exception e)
        {
            _log("RoundUp Workspace: credential cannot be decrypted for this Windows user: " + e.GetType().Name);
            return null;
        }
    }

    private static bool IsToken(string? token) =>
        token is { Length: 67 } && token.StartsWith("ru_", StringComparison.Ordinal) &&
        token.Skip(3).All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private void SaveToken(string token)
    {
        if (!IsToken(token)) throw new WorkspaceError(400, "Некоректний ключ доступу.");
        var ciphertext = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser);
        var tmp = _tokenFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(tmp, ciphertext);
        File.Move(tmp, _tokenFile, overwrite: true);
        _token = token;
    }

    private static string GetString(JsonElement root, string key)
    {
        return root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";
    }

    internal async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string id = "";
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || GetString(root, "channel") != "roundup-workspace") return;
            id = GetString(root, "requestId");
            if (id.Length > 80) return;
            var result = await DispatchAsync(root);
            _web.PostWebMessageAsJson(JsonSerializer.Serialize(new { channel = "roundup-workspace-result", requestId = id, ok = true, data = result }));
        }
        catch (Exception error)
        {
            int status = error is WorkspaceError webError ? webError.Status : 500;
            string message = error is WorkspaceError ? e.Message : "Не вдалося виконати запит спільної бази";
            _log("RoundUp Workspace request error: " + status + " " + error.GetType().Name);
            try
            {
                _web.PostWebMessageAsJson(JsonSerializer.Serialize(new
                {
                    channel = "roundup-workspace-result", requestId = id, ok = false,
                    error = message, status
                }));
            }
            catch (InvalidOperationException) { /* the window closed during the request */ }
        }
    }

    private async Task<object> DispatchAsync(JsonElement root)
    {
        var action = GetString(root, "action");
        switch (action)
        {
            case "status":
                if (_token is null) return new { signedIn = false, url = ApiBase.ToString() };
                try
                {
                    var me = await ApiAsync("GET", "/v1/me", null, _token, null);
                    return new { signedIn = true, user = me.GetProperty("user"), url = ApiBase.ToString() };
                }
                catch (WorkspaceError authError) when (authError.Status is 401 or 403)
                {
                    return new { signedIn = false, expired = true, url = ApiBase.ToString() };
                }

            case "login":
                {
                    var candidate = GetString(root, "token");
                    if (!IsToken(candidate)) throw new WorkspaceError(400, "Вставте персональний ключ RoundUp.");
                    var me = await ApiAsync("GET", "/v1/me", null, candidate, null);
                    SaveToken(candidate);
                    return new { signedIn = true, user = me.GetProperty("user") };
                }

            case "setup":
                {
                    var secret = GetString(root, "setupSecret");
                    var displayName = GetString(root, "displayName");
                    if (secret.Length < 32) throw new WorkspaceError(400, "Потрібен секрет початкового налаштування.");
                    var body = JsonSerializer.SerializeToElement(new { displayName });
                    var result = await ApiAsync("POST", "/v1/setup", body, null, secret);
                    var token = result.GetProperty("apiToken").GetString();
                    if (token is null) throw new WorkspaceError(503, "Сервер не повернув ключ керівника.");
                    SaveToken(token);
                    // Do not post raw admin token to WebView or write it to logs.
                    return new { signedIn = true, user = new { id = result.GetProperty("userId").GetString(), display_name = displayName, role = "admin" } };
                }

            case "copyToken":
                if (_token is null) throw new WorkspaceError(401, "Спочатку увійдіть.");
                System.Windows.Forms.Clipboard.SetText(_token);
                return new { copied = true };

            case "logout":
                try { if (File.Exists(_tokenFile)) File.Delete(_tokenFile); }
                catch (IOException) { throw new WorkspaceError(500, "Не вдалося видалити локальний ключ."); }
                catch (UnauthorizedAccessException) { throw new WorkspaceError(500, "Немає доступу до локального ключа."); }
                _token = null;
                return new { signedIn = false };

            case "api":
                if (_token is null) throw new WorkspaceError(401, "Спочатку увійдіть у спільне середовище.");
                var method = GetString(root, "method").ToUpperInvariant();
                var path = GetString(root, "path");
                if (method is not ("GET" or "POST" or "PUT" or "PATCH"))
                    throw new WorkspaceError(400, "Недозволений метод.");
                if (!path.StartsWith("/v1/", StringComparison.Ordinal) ||
                    path.Contains("://", StringComparison.Ordinal) || path.Contains('\\') ||
                    path.StartsWith("//", StringComparison.Ordinal) ||
                    path.StartsWith("/v1/setup", StringComparison.Ordinal))
                    throw new WorkspaceError(400, "Недозволений шлях API.");
                JsonElement? payload = root.TryGetProperty("body", out var bodyElement) ? bodyElement : null;
                return await ApiAsync(method, path, payload, _token, null);

            default:
                throw new WorkspaceError(400, "Невідома команда спільного середовища.");
        }
    }

    private static async Task<JsonElement> ApiAsync(
        string method, string path, JsonElement? body, string? token, string? setupSecret)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(ApiBase, path));
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (setupSecret is not null) request.Headers.TryAddWithoutValidation("X-RoundUp-Setup-Secret", setupSecret);
        if (body.HasValue)
            request.Content = new StringContent(body.Value.GetRawText(), Encoding.UTF8, "application/json");
        try
        {
            using var response = await Client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            JsonElement data;
            try
            {
                using var document = JsonDocument.Parse(text);
                data = document.RootElement.Clone();
            }
            catch (JsonException) { throw new WorkspaceError((int)response.StatusCode, "Сервер повернув некоректну відповідь."); }
            if (!response.IsSuccessStatusCode)
            {
                var error = data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty("error", out var detail) && detail.ValueKind == JsonValueKind.String
                    ? detail.GetString() : null;
                throw new WorkspaceError((int)response.StatusCode, error ?? "Помилка сервера RoundUp.");
            }
            return data;
        }
        catch (HttpRequestException)
        {
            throw new WorkspaceError(0, "Немає з'єднання зі спільною базою. Локальний RoundUp продовжує працювати.");
        }
        catch (TaskCanceledException)
        {
            throw new WorkspaceError(0, "Сервер RoundUp не відповідає. Повторіть спробу пізніше.");
        }
    }

    private sealed class WorkspaceError(int status, string message) : Exception(message)
    {
        internal int Status { get; } = status;
    }
}
