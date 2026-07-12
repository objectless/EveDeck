using Xunit;
using EveDeck.Services;
using System.IO;

namespace EveDeck.Tests;

public class EsiTokenStoreTests : IDisposable
{
    private readonly string _tempDir;

    public EsiTokenStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static EsiToken Sample(long id = 90000001) => new()
    {
        CharacterId = id,
        CharacterName = "Test Pilot",
        RefreshToken = "refresh-secret-abc",
        AccessToken = "access-jwt-xyz",
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20),
        Scopes = new() { "publicData", EsiAuthService.ScopePlanets },
    };

    [Fact]
    public void Put_ThenGet_RoundTripsInMemory()
    {
        var store = new EsiTokenStore(_tempDir);
        store.Put(Sample());

        var got = store.Get(90000001);
        Assert.NotNull(got);
        Assert.Equal("refresh-secret-abc", got!.RefreshToken);
        Assert.True(got.HasScope(EsiAuthService.ScopePlanets));
    }

    [Fact]
    public void Tokens_PersistAcrossInstances_Encrypted()
    {
        new EsiTokenStore(_tempDir).Put(Sample());

        // A fresh instance reads the DPAPI-encrypted file from disk.
        var reopened = new EsiTokenStore(_tempDir);
        Assert.True(reopened.Has(90000001));
        Assert.Equal("Test Pilot", reopened.Get(90000001)!.CharacterName);
    }

    [Fact]
    public void OnDiskFile_DoesNotContainPlaintextSecret()
    {
        var store = new EsiTokenStore(_tempDir);
        store.Put(Sample());

        var bytes = File.ReadAllBytes(store.Path);
        var asText = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("refresh-secret-abc", asText);
    }

    [Fact]
    public void Remove_DeletesToken()
    {
        var store = new EsiTokenStore(_tempDir);
        store.Put(Sample());
        store.Remove(90000001);
        Assert.False(store.Has(90000001));
        Assert.Null(new EsiTokenStore(_tempDir).Get(90000001));
    }

    [Fact]
    public void IsExpired_HonoursThirtySecondSkew()
    {
        var almostExpired = Sample();
        almostExpired.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(10); // inside the 30s buffer
        Assert.True(almostExpired.IsExpired);

        var fresh = Sample();
        fresh.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        Assert.False(fresh.IsExpired);
    }
}
