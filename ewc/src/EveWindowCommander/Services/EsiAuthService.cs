using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EveWindowCommander.Services;

public sealed class EsiAuthService
{
    private const string ClientId = "a86176aac0314cc7aa3f94dc2535842f";
    private const string AuthUrl = "https://login.eveonline.com/v2/oauth/authorize";
    private const string TokenUrl = "https://login.eveonline.com/v2/oauth/token";
    private const string VerifyUrl = "https://login.eveonline.com/oauth/verify";
    private const string RedirectUri = "http://localhost:4080/callback/";
    private const string Scope = "publicData";

    private static readonly HttpClient _http = new();

    public async Task<(long CharacterId, string CharacterName)> AuthorizeAsync(CancellationToken ct)
    {
        var codeVerifier = GenerateCodeVerifier();
        var codeChallenge = ComputeCodeChallenge(codeVerifier);
        var state = Guid.NewGuid().ToString("N");

        var authUri = $"{AuthUrl}?response_type=code" +
            $"&client_id={Uri.EscapeDataString(ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&scope={Uri.EscapeDataString(Scope)}" +
            $"&state={state}" +
            $"&code_challenge={codeChallenge}" +
            $"&code_challenge_method=S256";

        using var listener = new HttpListener();
        listener.Prefixes.Add(RedirectUri);
        listener.Start();

        Process.Start(new ProcessStartInfo(authUri) { UseShellExecute = true });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(5));

        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync().WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            listener.Stop();
            throw new TimeoutException("ESI login timed out (5 min). Try again.");
        }

        var query = context.Request.QueryString;
        var code = query["code"];
        var returnedState = query["state"];

        var html = code is not null && returnedState == state
            ? "<html><body style='font-family:sans-serif;background:#0a0d14;color:#e2e8f0;padding:40px'><h2>✓ Login successful — you can close this tab.</h2></body></html>"
            : "<html><body style='font-family:sans-serif;background:#0a0d14;color:#ef4444;padding:40px'><h2>Login failed — check EveDeck.</h2></body></html>";
        var htmlBytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = htmlBytes.Length;
        await context.Response.OutputStream.WriteAsync(htmlBytes, ct);
        context.Response.Close();
        listener.Stop();

        if (code is null) throw new InvalidOperationException("EVE SSO returned no auth code.");
        if (returnedState != state) throw new InvalidOperationException("State mismatch — possible CSRF.");

        // Exchange code for token
        var tokenBody = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = codeVerifier,
        });
        var tokenResp = await _http.PostAsync(TokenUrl, tokenBody, ct);
        tokenResp.EnsureSuccessStatusCode();
        var tokenDoc = JsonDocument.Parse(await tokenResp.Content.ReadAsStringAsync(ct));
        var accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("No access_token in EVE SSO response.");

        // Verify → get character identity
        var verifyReq = new HttpRequestMessage(HttpMethod.Get, VerifyUrl);
        verifyReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var verifyResp = await _http.SendAsync(verifyReq, ct);
        verifyResp.EnsureSuccessStatusCode();
        var verifyDoc = JsonDocument.Parse(await verifyResp.Content.ReadAsStringAsync(ct));
        var characterId = verifyDoc.RootElement.GetProperty("CharacterID").GetInt64();
        var characterName = verifyDoc.RootElement.GetProperty("CharacterName").GetString()
            ?? throw new InvalidOperationException("No CharacterName in verify response.");

        return (characterId, characterName);
    }

    private static string GenerateCodeVerifier()
        => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string ComputeCodeChallenge(string verifier)
        => Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
