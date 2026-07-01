using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

// Verificador de integração do signaling do NDesk Broker. Opera os DOIS lados (operador
// autenticado + agente anônimo) num único processo contra um broker real e valida o hub
// SignalR de ponta a ponta: relay de SDP/ICE, e — o mais importante — que SendSignal é
// recusado sem consentimento válido e após revogação (gate a cada mensagem, ADR-018).
//
// Config por ambiente (mesmas do broker):
//   NDESK_BROKER_URL   (default http://127.0.0.1:5080)
//   Jwt__SigningKey / Jwt__Issuer / Jwt__Audience
// Código de saída 0 = todos os checks passaram; 1 = falhou.

string baseUrl = (Environment.GetEnvironmentVariable("NDESK_BROKER_URL") ?? "http://127.0.0.1:5080").TrimEnd('/');
string jwtKey = Environment.GetEnvironmentVariable("Jwt__SigningKey")
    ?? throw new InvalidOperationException("Jwt__SigningKey não configurada.");
string issuer = Environment.GetEnvironmentVariable("Jwt__Issuer") ?? "remoteops";
string audience = Environment.GetEnvironmentVariable("Jwt__Audience") ?? "remoteops-ndesk";

string operatorId = "11111111-1111-1111-1111-111111111111";
string workspaceId = "22222222-2222-2222-2222-222222222222";

var checks = new List<(string name, bool ok)>();
void Check(string name, bool ok)
{
    checks.Add((name, ok));
    Console.WriteLine($"  [{(ok ? "PASS" : "FALHA")}] {name}");
}

using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
string jwt = MintJwt(jwtKey, issuer, audience, operatorId);

Console.WriteLine("== NDesk signaling check ==");
Console.WriteLine($"broker: {baseUrl}");

// 1) Operador emite ticket (autenticado)
http.DefaultRequestHeaders.Authorization = new("Bearer", jwt);
var issueResp = await http.PostAsJsonAsync("/ndesk/tickets", new
{
    workspaceId,
    requestedMode = "control",
    permissionsRequested = new[] { "view", "control" },
    agentAllowWindows7Legacy = false,
    agentRequiresInstall = false,
});
Check("emitir ticket -> 2xx", issueResp.IsSuccessStatusCode);
var ticket = await issueResp.Content.ReadFromJsonAsync<JsonElement>();
string linkToken = ticket.GetProperty("linkToken").GetString()!;
http.DefaultRequestHeaders.Authorization = null;

// 2) Agente (anônimo) resgata o link token -> sessionId
var redeemResp = await http.PostAsJsonAsync("/ndesk/tickets/redeem", new { linkToken });
Check("redeem -> 2xx", redeemResp.IsSuccessStatusCode);
var redeem = await redeemResp.Content.ReadFromJsonAsync<JsonElement>();
string sessionId = redeem.GetProperty("sessionId").GetString()!;

// 3) Conexões SignalR: operador (com JWT via query) e agente (anônimo)
await using var operatorConn = new HubConnectionBuilder()
    .WithUrl($"{baseUrl}/hubs/ndesk?access_token={jwt}")
    .Build();
await using var agentConn = new HubConnectionBuilder()
    .WithUrl($"{baseUrl}/hubs/ndesk")
    .Build();

var operatorGotSignal = new TaskCompletionSource<(string type, string payload)>();
var agentGotSignal = new TaskCompletionSource<(string type, string payload)>();
var operatorGotEnd = new TaskCompletionSource<bool>();
operatorConn.On<string, string>("Signal", (t, p) => operatorGotSignal.TrySetResult((t, p)));
agentConn.On<string, string>("Signal", (t, p) => agentGotSignal.TrySetResult((t, p)));
operatorConn.On<string?>("SessionEnded", _ => operatorGotEnd.TrySetResult(true));

await operatorConn.StartAsync();
await agentConn.StartAsync();
await operatorConn.InvokeAsync("JoinSession", sessionId, "operator");
await agentConn.InvokeAsync("JoinSession", sessionId, "agent");
Check("operador + agente entram na sessão", true);

// 4) SendSignal ANTES do consentimento deve ser recusado (gate por mensagem)
Check("SendSignal sem consentimento -> recusado", await Throws(() =>
    agentConn.InvokeAsync("SendSignal", sessionId, "offer", "payload-cru")));

// 5) Agente concede consentimento (subconjunto do pedido)
var consentResp = await http.PostAsJsonAsync($"/ndesk/sessions/{sessionId}/consent", new
{
    grantedByDisplayName = "Usuario Teste",
    grantedByMachineName = "PC-TESTE",
    mode = "control",
    permissions = new[] { "view", "control" },
});
Check("consent válido -> 2xx", consentResp.IsSuccessStatusCode);

// 6) Operador -> agente relay de SDP offer
await operatorConn.InvokeAsync("SendSignal", sessionId, "offer", "sdp-offer-opaco");
var agentSig = await WithTimeout(agentGotSignal.Task);
Check("relay operador->agente (offer)", agentSig is { type: "offer", payload: "sdp-offer-opaco" });

// 7) Agente -> operador relay de SDP answer
await agentConn.InvokeAsync("SendSignal", sessionId, "answer", "sdp-answer-opaco");
var opSig = await WithTimeout(operatorGotSignal.Task);
Check("relay agente->operador (answer)", opSig is { type: "answer", payload: "sdp-answer-opaco" });

// 8) Operador encerra -> ambos recebem SessionEnded
await operatorConn.InvokeAsync("EndSession", sessionId, "teste-concluido");
Check("EndSession -> SessionEnded no outro lado", await WithTimeout(operatorGotEnd.Task, fallback: false)
    || operatorGotEnd.Task.IsCompletedSuccessfully);

// 9) Após encerrar (consentimento revogado), SendSignal deve ser recusado de novo
Check("SendSignal após revogação -> recusado", await Throws(() =>
    agentConn.InvokeAsync("SendSignal", sessionId, "offer", "tarde-demais")));

Console.WriteLine();
int failed = checks.Count(c => !c.ok);
Console.WriteLine($"== {checks.Count - failed}/{checks.Count} checks OK ==");
return failed == 0 ? 0 : 1;

// ── helpers ───────────────────────────────────────────────────────────────────
static async Task<bool> Throws(Func<Task> action)
{
    try { await action(); return false; }
    catch { return true; }
}

static async Task<T?> WithTimeout<T>(Task<T> task, T? fallback = default, int ms = 5000)
{
    var done = await Task.WhenAny(task, Task.Delay(ms));
    return done == task ? await task : fallback;
}

static string MintJwt(string key, string issuer, string audience, string sub)
{
    static string B64(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    string header = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    string payload = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
    {
        ["sub"] = sub,
        ["iss"] = issuer,
        ["aud"] = audience,
        ["iat"] = now,
        ["exp"] = now + 3600,
    }));
    string signingInput = $"{header}.{payload}";
    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
    string sig = B64(hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput)));
    return $"{signingInput}.{sig}";
}
