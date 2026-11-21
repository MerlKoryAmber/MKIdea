using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LinuxOps.Web.Services.Auth;

/// <summary>
/// Клиент API MK 2FA (второй фактор для входа пользователей AD): создать запрос → опрос → код (если просят) → consume.
/// Секрет — Bearer, Idempotency-Key — на каждую операцию. Пускать только по 200 от consume с тем же username и context.
/// Описание API — у MK 2FA: «MFA_API_INTEGRATION_RU.md».
/// </summary>
public sealed class MkMfa(WebStore store)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Ответ API: HTTP-код, статус запроса, код ошибки и поля challenge.</summary>
    public sealed record Reply(int Code, string Status = "", string Error = "", string Message = "",
        string ChallengeId = "", int PollMs = 2000, string Username = "", string Context = "", DateTimeOffset? Expires = null)
    {
        public bool Ok => Code is >= 200 and < 300;
    }

    public bool Enabled(out AuthSettings s)
    {
        s = store.LoadAuth();
        return s.MfaEnabled && s.MfaUrl.Trim().Length > 0 && s.MfaSecretEnc.Length > 0;
    }

    public Task<Reply> Create(string username, string context, string ip) =>
        Send(HttpMethod.Post, "challenges", new JsonObject
        {
            ["username"] = username, ["context"] = context,
            ["action"] = Settings.MfaAction.Trim().Length > 0 ? Settings.MfaAction.Trim() : "Вход в LinuxOps Web",
            ["client_ip"] = ip
        }, idem: true);

    public Task<Reply> Get(string id) => Send(HttpMethod.Get, "challenges/" + Uri.EscapeDataString(id), null, idem: false);

    public Task<Reply> Verify(string id, string code) =>
        Send(HttpMethod.Post, $"challenges/{Uri.EscapeDataString(id)}/verify", new JsonObject { ["code"] = code }, idem: true);

    public Task<Reply> Consume(string id, string context) =>
        Send(HttpMethod.Post, $"challenges/{Uri.EscapeDataString(id)}/consume", new JsonObject { ["context"] = context }, idem: true);

    public Task<Reply> Cancel(string id) => Send(HttpMethod.Post, $"challenges/{Uri.EscapeDataString(id)}/cancel", null, idem: false);

    /// <summary>
    /// Проверка связи из настроек без сообщения человеку: опрос несуществующего запроса.
    /// 404 — адрес и секрет верны; 401 — секрет неверный или адрес сервера не разрешён в MK 2FA.
    /// </summary>
    public async Task<string> Probe(AuthSettings s, string secret)
    {
        var r = await Send(HttpMethod.Get, "challenges/lo-probe-" + Guid.NewGuid().ToString("N")[..8], null, false, s, secret);
        return r.Code switch
        {
            404 => "OK: MK 2FA отвечает, секрет принят.",
            401 => "MK 2FA отказал (401): секрет неверный или отозван, либо адрес этого сервера не разрешён в MK 2FA.",
            0 => "Нет связи: " + r.Message,
            _ => $"Неожиданный ответ {r.Code}: {r.Error} {r.Message}".Trim()
        };
    }

    private AuthSettings Settings => store.LoadAuth();

    private async Task<Reply> Send(HttpMethod method, string path, JsonObject? body, bool idem, AuthSettings? s = null, string? secret = null)
    {
        s ??= Settings;
        secret ??= store.Unprotect(s.MfaSecretEnc);
        var url = s.MfaUrl.Trim().TrimEnd('/') + "/" + path;
        try
        {
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            if (idem) req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            if (body is not null || method == HttpMethod.Post)
                req.Content = new StringContent((body ?? new JsonObject()).ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            return Parse((int)resp.StatusCode, text);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            return new Reply(0, Message: e is TaskCanceledException ? "нет ответа за 15 с" : e.Message);
        }
    }

    private static Reply Parse(int code, string text)
    {
        JsonNode? j = null;
        try { j = JsonNode.Parse(text); } catch (JsonException) { }
        JsonNode? err = null;
        string S(string k) { try { return j?[k]?.ToString() ?? ""; } catch (InvalidOperationException) { return ""; } }
        try { err = j?["error"]; } catch (InvalidOperationException) { }
        DateTimeOffset? exp = DateTimeOffset.TryParse(S("expires_at"), out var e) ? e : null;
        // при ошибке кода статус может прийти и рядом с error, и внутри него
        var status = S("status");
        if (status.Length == 0) status = err?["status"]?.ToString() ?? "";
        return new Reply(code, status,
            err?["code"]?.ToString() ?? "", err?["message"]?.ToString() ?? "",
            S("challenge_id"), int.TryParse(S("poll_after_ms"), out var p) ? Math.Clamp(p, 1000, 10000) : 2000,
            S("username"), S("context"), exp);
    }
}
