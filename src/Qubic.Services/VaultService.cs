using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Qubic.Core.Entities;

namespace Qubic.Services;

/// <summary>
/// A single entry in the seed vault.
/// </summary>
public sealed class VaultEntry
{
    public string Label { get; set; } = "";
    public string Seed { get; set; } = "";

    /// <summary>Computed from Seed on load, not serialized to disk.</summary>
    [JsonIgnore]
    public string Identity { get; set; } = "";
}

/// <summary>
/// An address book entry in the vault (label + 60-char identity, no seed).
/// </summary>
public sealed class ContactEntry
{
    public string Label { get; set; } = "";
    public string Address { get; set; } = "";
}

/// <summary>
/// Manages an encrypted vault file containing multiple seed entries.
/// Uses PBKDF2 (600k iterations, SHA-256) for key derivation and AES-256-GCM for authenticated encryption.
/// </summary>
public sealed class VaultService
{
    private const int SaltSize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 600_000;
    private const string VaultPathKey = "vault_path";

    private readonly QubicSettingsService _settings;
    private List<VaultEntry>? _entries;
    private List<ContactEntry>? _contacts;
    private string? _password;

    public VaultService(QubicSettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>Raised whenever vault state changes.</summary>
    public event Action? OnVaultChanged;

    /// <summary>The configured vault file path (from settings), or null.</summary>
    public string? VaultPath => _settings.GetCustom<string>(VaultPathKey);

    /// <summary>True if a vault path is configured and the file exists on disk.</summary>
    public bool VaultExists => VaultPath is { } p && File.Exists(p);

    /// <summary>True if a vault path is configured (file may or may not exist).</summary>
    public bool VaultConfigured => !string.IsNullOrEmpty(VaultPath);

    /// <summary>True if the vault is loaded and decrypted in memory.</summary>
    public bool IsUnlocked => _entries != null;

    /// <summary>Decrypted seed entries. Empty if locked.</summary>
    public IReadOnlyList<VaultEntry> Entries =>
        _entries?.AsReadOnly() ?? (IReadOnlyList<VaultEntry>)Array.Empty<VaultEntry>();

    /// <summary>Address book contacts. Empty if locked.</summary>
    public IReadOnlyList<ContactEntry> Contacts =>
        _contacts?.AsReadOnly() ?? (IReadOnlyList<ContactEntry>)Array.Empty<ContactEntry>();

    // ── Password validation ──

    /// <summary>
    /// Validates a password against requirements: min 12 chars, uppercase, lowercase, digit, special.
    /// Returns an error message or null if valid.
    /// </summary>
    public static string? ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
            return "Password is required.";
        if (password.Length < 12)
            return "Password must be at least 12 characters.";
        if (!password.Any(char.IsUpper))
            return "Password must contain at least one uppercase letter.";
        if (!password.Any(char.IsLower))
            return "Password must contain at least one lowercase letter.";
        if (!password.Any(char.IsDigit))
            return "Password must contain at least one digit.";
        if (password.All(c => char.IsLetterOrDigit(c)))
            return "Password must contain at least one special character.";
        return null;
    }

    // ── Lifecycle ──

    /// <summary>
    /// Creates a new vault at the given file path with the given password and initial entries.
    /// Persists the file path to settings.
    /// </summary>
    public void CreateVault(string filePath, string password, List<VaultEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path is required.", nameof(filePath));
        var pwError = ValidatePassword(password);
        if (pwError != null)
            throw new ArgumentException(pwError, nameof(password));
        if (entries.Count == 0)
            throw new ArgumentException("At least one entry is required.", nameof(entries));

        foreach (var entry in entries)
        {
            if (entry.Seed.Length != 55)
                throw new ArgumentException($"Seed for '{entry.Label}' must be 55 characters.");
            entry.Identity = QubicIdentity.FromSeed(entry.Seed).ToString();
        }

        _entries = entries;
        _contacts = [];
        _password = password;
        _settings.SetCustom(VaultPathKey, filePath);
        SaveToDisk();

        // Verify round-trip: read back and decrypt to ensure the vault is valid
        VerifyVaultRoundTrip(password);

        OnVaultChanged?.Invoke();
    }

    /// <summary>
    /// Points the vault at an existing file without unlocking it.
    /// </summary>
    public void SetVaultPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path is required.", nameof(filePath));
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Vault file not found.", filePath);

        _settings.SetCustom(VaultPathKey, filePath);
        OnVaultChanged?.Invoke();
    }

    /// <summary>
    /// Unlocks the vault with the given password.
    /// Returns null on success, or an error message on failure.
    /// </summary>
    public string? UnlockVault(string password)
    {
        var path = VaultPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return "Vault file not found.";

        try
        {
            var json = File.ReadAllText(path);
            var envelope = JsonSerializer.Deserialize<VaultFileEnvelope>(json);
            if (envelope == null) return "Invalid vault file format.";

            var decryptedJson = Decrypt(envelope, password);

            // V2 format: { "Seeds": [...], "Contacts": [...] }
            // V1 format: [{ "Label": "...", "Seed": "..." }, ...]
            List<VaultEntry>? entries;
            List<ContactEntry>? contacts;

            if (decryptedJson.TrimStart().StartsWith('['))
            {
                // V1: flat array of seeds
                entries = JsonSerializer.Deserialize<List<VaultEntry>>(decryptedJson);
                contacts = [];
            }
            else
            {
                // V2: object with Seeds + Contacts
                var payload = JsonSerializer.Deserialize<VaultPayload>(decryptedJson);
                entries = payload?.Seeds;
                contacts = payload?.Contacts ?? [];
            }

            if (entries == null) return "Decrypted data is invalid.";

            foreach (var entry in entries)
                entry.Identity = QubicIdentity.FromSeed(entry.Seed).ToString();

            _entries = entries;
            _contacts = contacts;
            _password = password;
            OnVaultChanged?.Invoke();
            return null;
        }
        catch (CryptographicException) { return "Wrong password or corrupted vault."; }
        catch (JsonException ex) { return $"Vault file error: {ex.Message}"; }
        catch (FormatException ex) { return $"Vault data error: {ex.Message}"; }
    }

    /// <summary>Locks the vault, clearing all decrypted data from memory.</summary>
    public void LockVault()
    {
        _entries = null;
        _contacts = null;
        _password = null;
        OnVaultChanged?.Invoke();
    }

    /// <summary>Deletes the vault file, clears settings, and clears memory.</summary>
    public void DeleteVault()
    {
        var path = VaultPath;
        _entries = null;
        _contacts = null;
        _password = null;
        _settings.RemoveCustom(VaultPathKey);
        try { if (path != null && File.Exists(path)) File.Delete(path); } catch { }
        OnVaultChanged?.Invoke();
    }

    // ── Entry management ──

    /// <summary>Adds a new entry to the unlocked vault and saves.</summary>
    public void AddEntry(string label, string seed)
    {
        EnsureUnlocked();
        if (seed.Length != 55)
            throw new ArgumentException("Seed must be 55 characters.");
        if (_entries!.Any(e => e.Seed == seed))
            throw new InvalidOperationException("This seed is already in the vault.");

        var identity = QubicIdentity.FromSeed(seed).ToString();
        _entries!.Add(new VaultEntry { Label = label, Seed = seed, Identity = identity });
        SaveToDisk();
        OnVaultChanged?.Invoke();
    }

    /// <summary>Removes an entry by its identity string.</summary>
    public void RemoveEntry(string identity)
    {
        EnsureUnlocked();
        var removed = _entries!.RemoveAll(e => e.Identity == identity);
        if (removed == 0) return;

        if (_entries.Count == 0)
        {
            DeleteVault();
            return;
        }

        SaveToDisk();
        OnVaultChanged?.Invoke();
    }

    /// <summary>Renames the label for a given identity.</summary>
    public void RenameEntry(string identity, string newLabel)
    {
        EnsureUnlocked();
        var entry = _entries!.FirstOrDefault(e => e.Identity == identity)
            ?? throw new InvalidOperationException("Entry not found.");
        entry.Label = newLabel;
        SaveToDisk();
        OnVaultChanged?.Invoke();
    }

    /// <summary>Gets an entry by identity, or null.</summary>
    public VaultEntry? GetEntry(string identity) =>
        _entries?.FirstOrDefault(e => e.Identity == identity);

    // ── Contact (address book) management ──

    /// <summary>Adds a contact to the address book.</summary>
    public void AddContact(string label, string address)
    {
        EnsureUnlocked();
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("Label is required.", nameof(label));
        if (string.IsNullOrEmpty(address) || address.Length != 60)
            throw new ArgumentException("Address must be 60 characters.", nameof(address));
        if (_contacts!.Any(c => c.Address.Equals(address, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("This address is already in the address book.");

        _contacts!.Add(new ContactEntry { Label = label, Address = address.ToUpperInvariant() });
        SaveToDisk();
        OnVaultChanged?.Invoke();
    }

    /// <summary>Removes a contact by address.</summary>
    public void RemoveContact(string address)
    {
        EnsureUnlocked();
        var removed = _contacts!.RemoveAll(c => c.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return;
        SaveToDisk();
        OnVaultChanged?.Invoke();
    }

    /// <summary>Renames a contact's label.</summary>
    public void RenameContact(string address, string newLabel)
    {
        EnsureUnlocked();
        var contact = _contacts!.FirstOrDefault(c => c.Address.Equals(address, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Contact not found.");
        contact.Label = newLabel;
        SaveToDisk();
        OnVaultChanged?.Invoke();
    }

    /// <summary>Gets a contact by address, or null.</summary>
    public ContactEntry? GetContact(string address) =>
        _contacts?.FirstOrDefault(c => c.Address.Equals(address, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns true if the address is already in the address book or matches a vault identity.</summary>
    public bool IsKnownAddress(string? address)
    {
        if (string.IsNullOrEmpty(address) || !IsUnlocked) return false;
        return _contacts!.Any(c => c.Address.Equals(address, StringComparison.OrdinalIgnoreCase))
            || _entries!.Any(e => e.Identity.Equals(address, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Changes the vault password. Re-encrypts all entries with the new password.</summary>
    public void ChangePassword(string currentPassword, string newPassword)
    {
        EnsureUnlocked();
        if (currentPassword != _password)
            throw new InvalidOperationException("Current password is incorrect.");
        var pwError = ValidatePassword(newPassword);
        if (pwError != null)
            throw new ArgumentException(pwError, nameof(newPassword));

        _password = newPassword;
        SaveToDisk();
        OnVaultChanged?.Invoke();
    }

    // ── Encryption ──

    private static byte[] DeriveKey(string password, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(
            password, salt, Iterations, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(KeySize);
    }

    private static VaultFileEnvelope Encrypt(string json, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = DeriveKey(password, salt);

        var plaintextBytes = Encoding.UTF8.GetBytes(json);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        return new VaultFileEnvelope
        {
            Salt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag),
            Data = Convert.ToBase64String(ciphertext)
        };
    }

    private static string Decrypt(VaultFileEnvelope envelope, string password)
    {
        var salt = Convert.FromBase64String(envelope.Salt);
        var nonce = Convert.FromBase64String(envelope.Nonce);
        var tag = Convert.FromBase64String(envelope.Tag);
        var ciphertext = Convert.FromBase64String(envelope.Data);
        var key = DeriveKey(password, salt);

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }

    // ── Helpers ──

    private void EnsureUnlocked()
    {
        if (_entries == null || _password == null)
            throw new InvalidOperationException("Vault is not unlocked.");
    }

    private void SaveToDisk()
    {
        if (_entries == null || _password == null) return;
        var path = VaultPath;
        if (string.IsNullOrEmpty(path)) return;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var payload = new VaultPayload { Seeds = _entries, Contacts = _contacts ?? [] };
        var json = JsonSerializer.Serialize(payload);
        var envelope = Encrypt(json, _password);
        var fileJson = JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, fileJson);
    }

    /// <summary>Reads the vault file back and decrypts it to verify the round-trip works.</summary>
    private void VerifyVaultRoundTrip(string password)
    {
        var path = VaultPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            throw new InvalidOperationException("Vault file was not written to disk.");

        var json = File.ReadAllText(path);
        var envelope = JsonSerializer.Deserialize<VaultFileEnvelope>(json)
            ?? throw new InvalidOperationException("Vault file could not be deserialized.");

        // This will throw CryptographicException if the round-trip fails
        var decryptedJson = Decrypt(envelope, password);
        var payload = JsonSerializer.Deserialize<VaultPayload>(decryptedJson)
            ?? throw new InvalidOperationException("Decrypted vault data is invalid.");

        if (payload.Seeds.Count != _entries!.Count)
            throw new InvalidOperationException(
                $"Round-trip mismatch: wrote {_entries.Count} entries, read back {payload.Seeds.Count}.");
    }

    private sealed class VaultFileEnvelope
    {
        public string Salt { get; set; } = "";
        public string Nonce { get; set; } = "";
        public string Tag { get; set; } = "";
        public string Data { get; set; } = "";
    }

    /// <summary>V2 vault payload: seeds + address book contacts.</summary>
    private sealed class VaultPayload
    {
        public List<VaultEntry> Seeds { get; set; } = [];
        public List<ContactEntry> Contacts { get; set; } = [];
    }
}
