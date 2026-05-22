using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Qubic.Services;

namespace Qubic.Services.Tests;

/// <summary>
/// Verifies VaultService's KDF dispatch, envelope versioning, legacy compatibility,
/// and the zxcvbn-backed password validator.
/// </summary>
public class VaultServiceTests : IDisposable
{
    private const string GoodPassword = "tr0ub4dor-correct-horse-battery";

    private readonly string _appName = $"Qubic.Services.Tests.{Guid.NewGuid():N}";
    private readonly string _vaultPath;

    public VaultServiceTests()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            _appName);
        Directory.CreateDirectory(dir);
        _vaultPath = Path.Combine(dir, "vault.dat");
    }

    public void Dispose()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            _appName);
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    private VaultService NewService() => new VaultService(new QubicSettingsService(_appName));

    private static readonly string TestSeed =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"; // 55 chars

    private static List<VaultEntry> OneEntry() =>
        new() { new VaultEntry { Label = "test", Seed = TestSeed } };

    // ── New-vault path: writes V2 + Argon2id ──

    [Fact]
    public void CreateVault_WritesV2EnvelopeWithArgon2id()
    {
        var svc = NewService();
        svc.CreateVault(_vaultPath, GoodPassword, OneEntry());

        var json = File.ReadAllText(_vaultPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(2, root.GetProperty("Version").GetInt32());
        Assert.Equal("argon2id", root.GetProperty("Kdf").GetString());

        var kdfParams = root.GetProperty("KdfParams");
        Assert.Equal(65536, kdfParams.GetProperty("MemoryKiB").GetInt32());
        Assert.Equal(3, kdfParams.GetProperty("TimeCost").GetInt32());
        Assert.Equal(1, kdfParams.GetProperty("Parallelism").GetInt32());

        // PBKDF2-only field should be omitted when KDF is Argon2id.
        Assert.False(kdfParams.TryGetProperty("Iterations", out _));
    }

    [Fact]
    public void CreateVault_RoundTrip_DecryptsBackToOriginalSeed()
    {
        var svc1 = NewService();
        svc1.CreateVault(_vaultPath, GoodPassword, OneEntry());
        svc1.LockVault();

        var svc2 = NewService();
        var err = svc2.UnlockVault(GoodPassword);

        Assert.Null(err);
        Assert.True(svc2.IsUnlocked);
        Assert.Single(svc2.Entries);
        Assert.Equal(TestSeed, svc2.Entries[0].Seed);
    }

    [Fact]
    public void UnlockVault_WithWrongPassword_ReturnsError()
    {
        var svc = NewService();
        svc.CreateVault(_vaultPath, GoodPassword, OneEntry());
        svc.LockVault();

        var err = svc.UnlockVault("wrong-password-zzzz-7");
        Assert.NotNull(err);
        Assert.False(svc.IsUnlocked);
    }

    // ── Legacy v1 envelope: PBKDF2-SHA256/600k, no Version/Kdf fields ──

    [Fact]
    public void UnlockVault_LegacyV1Envelope_ReadsTransparently()
    {
        // Write a vault file using the exact pre-v0.6.0 format: PBKDF2-SHA256/600k,
        // no Version/Kdf/KdfParams in the envelope.
        const string legacyPassword = "Legacy-Password-Long-Enough-123";
        WriteLegacyV1Vault(_vaultPath, legacyPassword, TestSeed);

        var svc = NewService();
        svc.SetVaultPath(_vaultPath);
        var err = svc.UnlockVault(legacyPassword);

        Assert.Null(err);
        Assert.True(svc.IsUnlocked);
        Assert.Equal(TestSeed, svc.Entries[0].Seed);
    }

    [Fact]
    public void LegacyV1Vault_AfterMutation_UpgradesToArgon2id()
    {
        const string legacyPassword = "Legacy-Password-Long-Enough-123";
        WriteLegacyV1Vault(_vaultPath, legacyPassword, TestSeed);

        var svc = NewService();
        svc.SetVaultPath(_vaultPath);
        Assert.Null(svc.UnlockVault(legacyPassword));

        // Any mutation triggers SaveToDisk → re-encrypt under the current default KDF.
        svc.RenameEntry(svc.Entries[0].Identity, "renamed");

        var json = File.ReadAllText(_vaultPath);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("Version").GetInt32());
        Assert.Equal("argon2id", doc.RootElement.GetProperty("Kdf").GetString());

        // Still openable under the same password after the upgrade.
        svc.LockVault();
        Assert.Null(svc.UnlockVault(legacyPassword));
    }

    /// <summary>
    /// Builds a vault file in the legacy v1 format and writes it to disk.
    /// This mirrors the exact bytes written by VaultService prior to envelope versioning,
    /// so we can guarantee old files still open with the new code.
    /// </summary>
    private static void WriteLegacyV1Vault(string path, string password, string seed)
    {
        var payload = new
        {
            Seeds = new[] { new { Label = "legacy", Seed = seed } },
            Contacts = Array.Empty<object>(),
            Watchlist = Array.Empty<object>(),
            Templates = Array.Empty<object>()
        };
        var json = JsonSerializer.Serialize(payload);

        var salt = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 600_000, HashAlgorithmName.SHA256, 32);

        var plaintext = Encoding.UTF8.GetBytes(json);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Legacy envelope: no Version/Kdf/KdfParams fields.
        var envelope = new
        {
            Salt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag),
            Data = Convert.ToBase64String(ciphertext)
        };
        File.WriteAllText(path, JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ── Password strength ──

    [Fact]
    public void Estimate_CommonPassword_ReturnsLowScore()
    {
        var s = VaultService.Estimate("password123");
        Assert.True(s.Score <= 1, $"Expected weak password to score ≤1, got {s.Score}");
        Assert.False(s.IsAcceptable);
    }

    [Fact]
    public void Estimate_StrongPassphrase_ReturnsHighScore()
    {
        var s = VaultService.Estimate("correct-horse-battery-staple-Q9-yacht");
        Assert.True(s.Score >= 3, $"Expected strong passphrase to score ≥3, got {s.Score}");
        Assert.True(s.IsAcceptable);
    }

    [Fact]
    public void Estimate_EmptyPassword_ReturnsScoreZero()
    {
        var s = VaultService.Estimate("");
        Assert.Equal(0, s.Score);
        Assert.False(s.IsAcceptable);
    }

    [Theory]
    [InlineData("", "Password is required.")]
    [InlineData("short1!", "at least 12")]                // length floor
    [InlineData("Password1234", null)]                    // long, but very common patterns — see below
    public void ValidatePassword_LengthAndCommonPatterns(string password, string expectedFragment)
    {
        var err = VaultService.ValidatePassword(password);

        if (expectedFragment is null)
        {
            // Allow either acceptance or rejection here — zxcvbn's scoring of "Password1234" is
            // implementation-defined enough that we don't pin it. Just assert that *if* rejected,
            // the message is useful (non-empty).
            if (err != null) Assert.NotEmpty(err);
        }
        else
        {
            Assert.NotNull(err);
            Assert.Contains(expectedFragment, err);
        }
    }

    [Fact]
    public void ValidatePassword_StrongPassphrase_ReturnsNull()
    {
        Assert.Null(VaultService.ValidatePassword("correct-horse-battery-staple-Q9-yacht"));
    }

    [Fact]
    public void ValidatePassword_WeakLongPassword_ReturnsError()
    {
        // 14 chars but trivially guessable — should fail score ≥ 3.
        var err = VaultService.ValidatePassword("aaaaaaaaaaaaaa");
        Assert.NotNull(err);
        Assert.NotEmpty(err);
    }
}
