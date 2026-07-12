using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EveDeck.Services;

// Persists ESI OAuth tokens outside settings.json, encrypted with DPAPI (CurrentUser).
// A refresh token is a bearer credential for the character's ESI scopes, so it must never land in
// the plaintext settings.json the user might share when reporting a bug.
public sealed class EsiTokenStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EveDeck.EsiTokenStore.v1");

    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<long, EsiToken> _tokens = new();

    public EsiTokenStore(string appDataFolder)
    {
        _path = System.IO.Path.Combine(appDataFolder, "esi-tokens.dat");
        Load();
    }

    public string Path => _path;

    public EsiToken? Get(long characterId)
    {
        lock (_lock)
            return _tokens.TryGetValue(characterId, out var t) ? t : null;
    }

    public bool Has(long characterId) => Get(characterId) is not null;

    public void Put(EsiToken token)
    {
        lock (_lock)
        {
            _tokens[token.CharacterId] = token;
            Persist();
        }
    }

    public void Remove(long characterId)
    {
        lock (_lock)
        {
            if (_tokens.Remove(characterId)) Persist();
        }
    }

    private void Load()
    {
        lock (_lock)
        {
            _tokens = new Dictionary<long, EsiToken>();
            if (!File.Exists(_path)) return;
            try
            {
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
                var list = JsonSerializer.Deserialize<List<EsiToken>>(Encoding.UTF8.GetString(plain), JsonOptions);
                if (list is null) return;
                foreach (var t in list) _tokens[t.CharacterId] = t;
            }
            catch
            {
                // Unreadable (corrupt, or copied from another Windows user/machine — DPAPI is
                // machine+user bound). Drop it; the user re-links the character.
                try { File.Delete(_path); } catch { }
            }
        }
    }

    private void Persist()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var json = JsonSerializer.Serialize(_tokens.Values.ToList(), JsonOptions);
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.CurrentUser);
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, blob);
        if (File.Exists(_path)) File.Replace(tmp, _path, destinationBackupFileName: null);
        else File.Move(tmp, _path);
    }
}

public sealed class EsiToken
{
    public long CharacterId { get; set; }
    public string CharacterName { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public List<string> Scopes { get; set; } = new();

    // 30s of slack so a token doesn't expire mid-flight between the check and the ESI call.
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt - TimeSpan.FromSeconds(30);

    public bool HasScope(string scope) => Scopes.Contains(scope, StringComparer.OrdinalIgnoreCase);
}
