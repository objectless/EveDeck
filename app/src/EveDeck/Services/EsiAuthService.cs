using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EveDeck.Services;

public sealed class EsiAuthService
{
    private const string ClientId = "a86176aac0314cc7aa3f94dc2535842f";
    private const string AuthUrl = "https://login.eveonline.com/v2/oauth/authorize";
    private const string TokenUrl = "https://login.eveonline.com/v2/oauth/token";
    private const string VerifyUrl = "https://login.eveonline.com/oauth/verify";
    private const string RedirectUri = "http://localhost:4080/callback/";

    public const string ScopePlanets = "esi-planets.manage_planets.v1";
    public const string ScopeAssets = "esi-assets.read_assets.v1";
    public const string ScopeSkills = "esi-skills.read_skills.v1";

    // Requested on every login. The PI scopes are read-only in practice (we only GET), but ESI has no
    // read-only planets scope — manage_planets is the only one that exposes colonies.
    private const string Scope = "publicData " + ScopePlanets + " " + ScopeAssets + " " + ScopeSkills;

    private static readonly HttpClient _http = new();

    public async Task<EsiToken> AuthorizeAsync(CancellationToken ct)
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
        return await ExchangeAsync(tokenBody, ct);
    }

    // Swaps a (possibly rotated) refresh token for a fresh access token. EVE may return a NEW refresh
    // token here — the caller must persist whatever comes back, or the old one eventually stops working.
    public async Task<EsiToken> RefreshAsync(EsiToken token, CancellationToken ct)
    {
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = token.RefreshToken,
            ["client_id"] = ClientId,
        });
        return await ExchangeAsync(body, ct);
    }

    private static async Task<EsiToken> ExchangeAsync(FormUrlEncodedContent body, CancellationToken ct)
    {
        var tokenResp = await _http.PostAsync(TokenUrl, body, ct);
        if (!tokenResp.IsSuccessStatusCode)
        {
            var err = await tokenResp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"EVE SSO token exchange failed ({(int)tokenResp.StatusCode}): {err}");
        }

        var tokenDoc = JsonDocument.Parse(await tokenResp.Content.ReadAsStringAsync(ct));
        var root = tokenDoc.RootElement;
        var accessToken = root.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("No access_token in EVE SSO response.");
        var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
        var expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 1200;

        // Verify → get character identity + the scopes actually granted (which can be fewer than we
        // asked for if the user unticks one on the SSO consent screen).
        var verifyReq = new HttpRequestMessage(HttpMethod.Get, VerifyUrl);
        verifyReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var verifyResp = await _http.SendAsync(verifyReq, ct);
        verifyResp.EnsureSuccessStatusCode();
        var verifyDoc = JsonDocument.Parse(await verifyResp.Content.ReadAsStringAsync(ct));
        var verifyRoot = verifyDoc.RootElement;
        var characterId = verifyRoot.GetProperty("CharacterID").GetInt64();
        var characterName = verifyRoot.GetProperty("CharacterName").GetString()
            ?? throw new InvalidOperationException("No CharacterName in verify response.");
        var scopes = (verifyRoot.TryGetProperty("Scopes", out var sc) ? sc.GetString() ?? "" : "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        return new EsiToken
        {
            CharacterId = characterId,
            CharacterName = characterName,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            Scopes = scopes,
        };
    }

    private static string GenerateCodeVerifier()
        => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string ComputeCodeChallenge(string verifier)
        => Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
