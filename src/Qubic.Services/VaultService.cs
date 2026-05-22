using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Konscious.Security.Cryptography;
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
/// A watchlist entry: an address to monitor balance for (no seed, no sending).
/// </summary>
public sealed class WatchlistEntry
{
    public string Label { get; set; } = "";
    public string Address { get; set; } = "";
}

/// <summary>A saved SendToMany template (name + list of recipients).</summary>
public sealed class SendManyTemplate
{
    public string Name { get; set; } = "";
    public List<SendManyRecipient> Recipients { get; set; } = [];
}

/// <summary>A single recipient in a SendToMany template.</summary>
public sealed class SendManyRecipient
{
    public string Address { get; set; } = "";
    public long Amount { get; set; }
}

/// <summary>
/// Manages an encrypted vault file containing seeds, contacts, watchlist and templates.
/// Current default: Argon2id (m=64 MiB, t=3, p=1) for key derivation + AES-256-GCM for authenticated encryption.
/// Legacy vaults using PBKDF2-HMAC-SHA256 (600k iterations) are read transparently and migrate to
/// Argon2id on the next save.
/// </summary>
public sealed class VaultService
{
    private const int SaltSize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const string VaultPathKey = "vault_path";

    // KDF identifiers used in the envelope.
    private const string KdfPbkdf2 = "pbkdf2-sha256";
    private const string KdfArgon2id = "argon2id";

    // Legacy PBKDF2 parameters (still read; no longer written for new vaults).
    private const int LegacyPbkdf2Iterations = 600_000;

    // Current Argon2id parameters. These are written into every new envelope so changing the
    // defaults later only affects new saves; existing files keep opening with their stored params.
    private const int Argon2MemoryKiB = 64 * 1024; // 64 MiB
    private const int Argon2TimeCost = 3;
    private const int Argon2Parallelism = 1;

    // Current envelope format version. v1 = legacy (no Version/Kdf fields, implicit PBKDF2).
    private const int CurrentEnvelopeVersion = 2;

    private static readonly JsonSerializerOptions EnvelopeJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly QubicSettingsService _settings;
    private List<VaultEntry>? _entries;
    private List<ContactEntry>? _contacts;
    private List<WatchlistEntry>? _watchlist;
    private List<SendManyTemplate>? _templates;
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

    /// <summary>Watchlist addresses for balance monitoring. Empty if locked.</summary>
    public IReadOnlyList<WatchlistEntry> Watchlist =>
        _watchlist?.AsReadOnly() ?? (IReadOnlyList<WatchlistEntry>)Array.Empty<WatchlistEntry>();

    /// <summary>SendToMany templates. Empty if locked.</summary>
    public IReadOnlyList<SendManyTemplate> Templates =>
        _templates?.AsReadOnly() ?? (IReadOnlyList<SendManyTemplate>)Array.Empty<SendManyTemplate>();

    // ── Password validation ──

    /// <summary>Minimum password length enforced by <see cref="ValidatePassword(string?)"/>.</summary>
    public const int MinPasswordLength = 12;

    /// <summary>Minimum zxcvbn score (0..4) required by <see cref="ValidatePassword(string?)"/>.</summary>
    public const int MinPasswordScore = 3;

    /// <summary>
    /// Estimated strength of a password using the zxcvbn algorithm.
    /// </summary>
    /// <param name="Score">0 (very weak) to 4 (very strong).</param>
    /// <param name="Guesses">Estimated number of guesses required to crack.</param>
    /// <param name="CrackTimeDisplay">Human-readable crack time (offline slow hashing, 1e4/sec).</param>
    /// <param name="Warning">Optional zxcvbn warning, e.g. "This is a very common password".</param>
    /// <param name="Suggestions">Zxcvbn improvement suggestions.</param>
    public sealed record PasswordStrength(
        int Score,
        double Guesses,
        string? CrackTimeDisplay,
        string? Warning,
        IReadOnlyList<string> Suggestions)
    {
        /// <summary>True if the password clears the minimum length + zxcvbn score floor.</summary>
        public bool IsAcceptable => Score >= MinPasswordScore;
    }

    /// <summary>
    /// Estimates a password's strength via zxcvbn. Returns Score 0 and a hint for empty input.
    /// </summary>
    public static PasswordStrength Estimate(string? password)
    {
        if (string.IsNullOrEmpty(password))
            return new PasswordStrength(0, 0, null, "Password is required.", Array.Empty<string>());

        var result = Zxcvbn.Core.EvaluatePassword(password);
        var suggestions = result.Feedback?.Suggestions is { } s
            ? (IReadOnlyList<string>)s.ToList()
            : Array.Empty<string>();
        return new PasswordStrength(
            Score: result.Score,
            Guesses: result.Guesses,
            CrackTimeDisplay: result.CrackTimeDisplay?.OfflineSlowHashing1e4PerSecond,
            Warning: string.IsNullOrEmpty(result.Feedback?.Warning) ? null : result.Feedback.Warning,
            Suggestions: suggestions);
    }

    /// <summary>
    /// Validates a password: must be at least <see cref="MinPasswordLength"/> characters and
    /// achieve zxcvbn score ≥ <see cref="MinPasswordScore"/>. Returns null on success, or an
    /// error message suitable for direct display to the user.
    /// </summary>
    public static string? ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
            return "Password is required.";
        if (password.Length < MinPasswordLength)
            return $"Password must be at least {MinPasswordLength} characters.";

        var strength = Estimate(password);
        if (strength.IsAcceptable)
            return null;

        var msg = strength.Warning ?? "Password is too easy to guess.";
        if (strength.Suggestions.Count > 0)
            msg += " " + string.Join(" ", strength.Suggestions);
        return msg;
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
        _watchlist = [];
        _templates = [];
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
    /// Moves (or copies) the vault file to a new location and updates the stored path.
    /// </summary>
    public void MoveVault(string newPath)
    {
        var currentPath = VaultPath;
        if (string.IsNullOrWhiteSpace(currentPath) || !File.Exists(currentPath))
            throw new InvalidOperationException("No vault file to move.");
        if (string.IsNullOrWhiteSpace(newPath))
            throw new ArgumentException("New file path is required.", nameof(newPath));

        var fullNew = Path.GetFullPath(newPath);
        var fullCurrent = Path.GetFullPath(currentPath);
        if (string.Equals(fullNew, fullCurrent, StringComparison.OrdinalIgnoreCase))
            return;

        File.Copy(currentPath, newPath, overwrite: true);
        _settings.SetCustom(VaultPathKey, newPath);

        // Delete the old file only after the path is updated
        try { File.Delete(currentPath); } catch { }

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
            List<WatchlistEntry>? watchlist;
            List<SendManyTemplate>? templates;

            if (decryptedJson.TrimStart().StartsWith('['))
            {
                // V1: flat array of seeds
                entries = JsonSerializer.Deserialize<List<VaultEntry>>(decryptedJson);
                contacts = [];
                watchlist = [];
                templates = [];
            }
            else
            {
                // V2+: object with Seeds + Contacts + Watchlist + Templates
                var payload = JsonSerializer.Deserialize<VaultPayload>(decryptedJson);
                entries = payload?.Seeds;
                contacts = payload?.Contacts ?? [];
                watchlist = payload?.Watchlist ?? [];
                templates = payload?.Templates ?? [];
            }

            if (entries == null) return "Decrypted data is invalid.";

            foreach (var entry in entries)
                entry.Identity = QubicIdentity.FromSeed(entry.Seed).ToString();

            _entries = entries;
            _contacts = contacts;
            _watchlist = watchlist;
            _templates = templates;
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
        _watchlist = null;
        _templates = null;
        _password = null;
        OnVaultChanged?.Invoke();
    }

    /// <summary>Deletes the vault file, clears settings, and clears memory.</summary>
    public void DeleteVault()
    {
        var path = VaultPath;
        _entries = null;
        _contacts = null;
        _watchlist = null;
        _templates = null;
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

    // ── Watchlist management ──

    /// <summary>Adds an address to the watchlist.</summary>
    public void AddWatchlistEntry(string label, string address)
    {
        EnsureUnlocked();
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("Label is required.", nameof(label));
        if (string.IsNullOrEmpty(address) || address.Length != 60)
            throw new ArgumentException("Address must be 60 characters.", nameof(address));
        if (_watchlist!.Any(w => w.Address.Equals(address, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("This address is already in the watchlist.");

        _watchlist!.Add(new WatchlistEntry { Label = label, Address = address.ToUpperInvariant() });
        SaveToDisk();
        OnVaultChanged?.Invoke();
    }

    /// <summary>Removes a watchlist entry by address.</summary>
    public void RemoveWatchlistEntry(string address)
    {
        EnsureUnlocked();
        var removed = _watchlist!.RemoveAll(w => w.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return;
        SaveToDisk();
        OnVaultChanged?.Invoke();
    }

    /// <summary>Renames a watchlist entry's label.</summary>
    public void RenameWatchlistEntry(string address, string newLabel)
    {
        EnsureUnlocked();
        var entry = _watchlist!.FirstOrDefault(w => w.Address.Equals(address, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Watchlist entry not found.");
        entry.Label = newLabel;
        SaveToDisk();
        OnVaultChanged?.Invoke();
    }

    /// <summary>Gets a watchlist entry by address, or null.</summary>
    public WatchlistEntry? GetWatchlistEntry(string address) =>
        _watchlist?.FirstOrDefault(w => w.Address.Equals(address, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns true if the address is already in the address book or matches a vault identity.</summary>
    public bool IsKnownAddress(string? address)
    {
        if (string.IsNullOrEmpty(address) || !IsUnlocked) return false;
        return _contacts!.Any(c => c.Address.Equals(address, StringComparison.OrdinalIgnoreCase))
            || _watchlist!.Any(w => w.Address.Equals(address, StringComparison.OrdinalIgnoreCase))
            || _entries!.Any(e => e.Identity.Equals(address, StringComparison.OrdinalIgnoreCase));
    }

    // ── Template management ──

    /// <summary>Adds a new SendToMany template.</summary>
    public void AddTemplate(string name, List<SendManyRecipient> recipients)
    {
        EnsureUnlocked();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Template name is required.", nameof(name));
        if (recipients.Count == 0 || recipients.All(r => string.IsNullOrWhiteSpace(r.Address)))
            throw new ArgumentException("At least one recipient with an address is required.", nameof(recipients));
        if (_templates!.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A template with this name already exists.");

        _templates!.Add(new SendManyTemplate { Name = name.Trim(), Recipients = recipients });
        SaveToDisk();
        OnVaultChanged?.Invoke();
    }

    /// <summary>Updates an existing template's recipients.</summary>
    public void UpdateTemplate(string name, List<SendManyRecipient> recipients)
    {
        EnsureUnlocked();
        var template = _templates!.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Template not found.");
        if (recipients.Count == 0 || recipients.All(r => string.IsNullOrWhiteSpace(r.Address)))
            throw new ArgumentException("At least one recipient with an address is required.", nameof(recipients));
        template.Recipients = recipients;
        SaveToDisk();
        OnVaultChanged?.Invoke();
    }

    /// <summary>Removes a template by name.</summary>
    public void RemoveTemplate(string name)
    {
        EnsureUnlocked();
        var removed = _templates!.RemoveAll(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return;
        SaveToDisk();
        OnVaultChanged?.Invoke();
    }

    /// <summary>Renames a template.</summary>
    public void RenameTemplate(string name, string newName)
    {
        EnsureUnlocked();
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("New name is required.", nameof(newName));
        var template = _templates!.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Template not found.");
        if (_templates!.Any(t => t.Name.Equals(newName, StringComparison.OrdinalIgnoreCase) && t != template))
            throw new InvalidOperationException("A template with this name already exists.");
        template.Name = newName.Trim();
        SaveToDisk();
        OnVaultChanged?.Invoke();
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

    private static byte[] DeriveKey(string password, byte[] salt, string kdf, KdfParams? kdfParams)
    {
        switch (kdf)
        {
            case KdfPbkdf2:
                // Legacy read path. New vaults are never written with this KDF.
                var iterations = kdfParams?.Iterations ?? LegacyPbkdf2Iterations;
                return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeySize);

            case KdfArgon2id:
                using (var argon = new Argon2id(Encoding.UTF8.GetBytes(password)))
                {
                    argon.Salt = salt;
                    argon.DegreeOfParallelism = kdfParams?.Parallelism ?? Argon2Parallelism;
                    argon.MemorySize = kdfParams?.MemoryKiB ?? Argon2MemoryKiB;
                    argon.Iterations = kdfParams?.TimeCost ?? Argon2TimeCost;
                    return argon.GetBytes(KeySize);
                }

            default:
                throw new CryptographicException($"Unsupported KDF: '{kdf}'.");
        }
    }

    /// <summary>
    /// Encrypts the JSON payload with the current default KDF (Argon2id) and AES-256-GCM.
    /// </summary>
    private static VaultFileEnvelope Encrypt(string json, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var kdfParams = new KdfParams
        {
            MemoryKiB = Argon2MemoryKiB,
            TimeCost = Argon2TimeCost,
            Parallelism = Argon2Parallelism
        };
        var key = DeriveKey(password, salt, KdfArgon2id, kdfParams);

        var plaintextBytes = Encoding.UTF8.GetBytes(json);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        return new VaultFileEnvelope
        {
            Version = CurrentEnvelopeVersion,
            Kdf = KdfArgon2id,
            KdfParams = kdfParams,
            Salt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag),
            Data = Convert.ToBase64String(ciphertext)
        };
    }

    /// <summary>
    /// Decrypts an envelope. Handles both v2 envelopes (with explicit Kdf+KdfParams) and
    /// legacy v1 envelopes that omit those fields and implicitly used PBKDF2-SHA256 / 600k.
    /// </summary>
    private static string Decrypt(VaultFileEnvelope envelope, string password)
    {
        var salt = Convert.FromBase64String(envelope.Salt);
        var nonce = Convert.FromBase64String(envelope.Nonce);
        var tag = Convert.FromBase64String(envelope.Tag);
        var ciphertext = Convert.FromBase64String(envelope.Data);

        // Legacy v1 envelopes have no Kdf field — fall back to PBKDF2 with the historical iteration count.
        var kdf = string.IsNullOrEmpty(envelope.Kdf) ? KdfPbkdf2 : envelope.Kdf;
        var kdfParams = envelope.KdfParams;
        if (kdf == KdfPbkdf2 && kdfParams?.Iterations == null)
            kdfParams = new KdfParams { Iterations = LegacyPbkdf2Iterations };

        var key = DeriveKey(password, salt, kdf, kdfParams);

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

        var payload = new VaultPayload { Seeds = _entries, Contacts = _contacts ?? [], Watchlist = _watchlist ?? [], Templates = _templates ?? [] };
        var json = JsonSerializer.Serialize(payload);
        var envelope = Encrypt(json, _password);
        var fileJson = JsonSerializer.Serialize(envelope, EnvelopeJsonOptions);
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

    /// <summary>
    /// Outer JSON wrapper persisted to disk.
    /// V1 (legacy): only Salt/Nonce/Tag/Data — implicit PBKDF2-SHA256/600k.
    /// V2: adds Version/Kdf/KdfParams so the format can evolve without breaking existing files.
    /// </summary>
    private sealed class VaultFileEnvelope
    {
        // Optional in v1 files; defaults to 1 when absent.
        public int Version { get; set; } = 1;

        // Optional in v1 files; "" means legacy PBKDF2-SHA256.
        public string Kdf { get; set; } = "";

        // Optional in v1 files.
        public KdfParams? KdfParams { get; set; }

        public string Salt { get; set; } = "";
        public string Nonce { get; set; } = "";
        public string Tag { get; set; } = "";
        public string Data { get; set; } = "";
    }

    /// <summary>
    /// KDF parameters carried in the envelope. Only the fields relevant to the chosen KDF are set;
    /// e.g. PBKDF2 uses Iterations only, Argon2id uses MemoryKiB/TimeCost/Parallelism.
    /// </summary>
    private sealed class KdfParams
    {
        // PBKDF2.
        public int? Iterations { get; set; }

        // Argon2id.
        public int? MemoryKiB { get; set; }
        public int? TimeCost { get; set; }
        public int? Parallelism { get; set; }
    }

    /// <summary>Vault payload: seeds + contacts + watchlist + SendToMany templates.</summary>
    private sealed class VaultPayload
    {
        public List<VaultEntry> Seeds { get; set; } = [];
        public List<ContactEntry> Contacts { get; set; } = [];
        public List<WatchlistEntry> Watchlist { get; set; } = [];
        public List<SendManyTemplate> Templates { get; set; } = [];
    }
}
