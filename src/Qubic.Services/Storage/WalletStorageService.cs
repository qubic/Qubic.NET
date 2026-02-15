using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Qubic.Services.Storage;

/// <summary>
/// Facade that manages the wallet database lifecycle (open/close on seed changes)
/// and coordinates sync. Call <see cref="SetSeed"/> from MainLayout when the user enters a seed.
/// </summary>
public sealed class WalletStorageService : IDisposable
{
    private readonly WalletDatabase _db;
    private readonly WalletSyncService _sync;
    private readonly QubicSettingsService _settings;
    private string? _identity;

    public bool IsOpen => _db.IsOpen;
    public WalletDatabase Database => _db;
    public WalletSyncService Sync => _sync;
    public string? Identity => _identity;

    public event Action? OnStateChanged;

    public WalletStorageService(WalletDatabase db, WalletSyncService sync, QubicSettingsService settings)
    {
        _db = db;
        _sync = sync;
        _settings = settings;
    }

    /// <summary>
    /// Opens the encrypted database for this seed and starts background sync.
    /// </summary>
    public void SetSeed(string seed, string identity)
    {
        Close();

        _identity = identity;

        var dbPath = GetDbPath(seed);
        var passphrase = GetPassphrase(seed);
        _db.Open(dbPath, passphrase);

        MigrateLegacyTxdb(seed);

        _sync.Start(identity);
        OnStateChanged?.Invoke();
    }

    /// <summary>
    /// Stops sync and closes the database.
    /// </summary>
    public void ClearSeed()
    {
        Close();
        OnStateChanged?.Invoke();
    }

    private void Close()
    {
        _sync.Stop();
        _db.Close();
        _identity = null;
    }

    // ── Convenience query methods ──

    public List<StoredTransaction> GetTransactions(TransactionQuery query)
    {
        if (!IsOpen || _identity == null) return [];
        return _db.GetTransactions(query, _identity);
    }

    public List<StoredLogEvent> GetLogEvents(LogEventQuery query)
    {
        if (!IsOpen) return [];
        return _db.GetLogEvents(query);
    }

    public int TransactionCount => IsOpen ? _db.GetTransactionCount() : 0;
    public int LogEventCount => IsOpen ? _db.GetLogEventCount() : 0;

    // ── Sync management ──

    /// <summary>Restarts sync from current watermarks.</summary>
    public void RestartSync()
    {
        if (!IsOpen || _identity == null) return;
        _sync.Stop();
        _sync.Start(_identity);
    }

    /// <summary>Resets a specific watermark and restarts sync.</summary>
    public void ResetWatermarkAndResync(string watermarkKey)
    {
        if (!IsOpen || _identity == null) return;
        _sync.Stop();
        _db.DeleteWatermark(watermarkKey);
        _sync.Start(_identity);
    }

    /// <summary>Resets all watermarks and restarts sync.</summary>
    public void ResetAllWatermarksAndResync()
    {
        if (!IsOpen || _identity == null) return;
        _sync.Stop();
        foreach (var key in _db.GetAllWatermarks().Keys)
            _db.DeleteWatermark(key);
        _sync.Start(_identity);
    }

    /// <summary>Clears all stored log events, resets log watermark, and restarts sync.</summary>
    public void ClearLogEventsAndResync()
    {
        if (!IsOpen || _identity == null) return;
        _sync.Stop();
        _db.ClearLogEvents();
        _db.DeleteWatermark("bob_log_last_logid");
        _db.DeleteWatermark("bob_log_last_epoch");
        _sync.Start(_identity);
    }

    /// <summary>Clears all stored transactions, resets tx watermarks, and restarts sync.</summary>
    public void ClearTransactionsAndResync()
    {
        if (!IsOpen || _identity == null) return;
        _sync.Stop();
        _db.ClearTransactions();
        _db.DeleteWatermark("rpc_last_offset");
        _db.DeleteWatermark("bob_transfer_last_logid");
        _db.DeleteWatermark("bob_transfer_last_epoch");
        _sync.Start(_identity);
    }

    /// <summary>Gets all current sync watermarks.</summary>
    public Dictionary<string, string> GetWatermarks()
    {
        if (!IsOpen) return new();
        return _db.GetAllWatermarks();
    }

    // ── Legacy .txdb migration ──

    private void MigrateLegacyTxdb(string seed)
    {
        try
        {
            using var sha = SHA256.Create();
            var legacyKey = Convert.ToHexString(sha.ComputeHash(
                Encoding.UTF8.GetBytes("qubic-toolkit-tx-" + seed))).ToLowerInvariant();
            var legacyPath = Path.Combine(_settings.StorageDirectory, legacyKey[..16] + ".txdb");

            if (!File.Exists(legacyPath)) return;

            var encrypted = File.ReadAllText(legacyPath);
            var json = DecryptLegacy(encrypted, legacyKey);
            var tracked = JsonSerializer.Deserialize<List<TrackedTransaction>>(json);

            if (tracked != null && tracked.Count > 0)
            {
                foreach (var tx in tracked)
                    _db.UpsertTrackedTransaction(tx);
            }

            File.Move(legacyPath, legacyPath + ".migrated", overwrite: true);
        }
        catch { /* best effort */ }
    }

    private static string DecryptLegacy(string ciphertext, string key)
    {
        var keyBytes = DeriveKeyBytes(key);
        var allBytes = Convert.FromBase64String(ciphertext);
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = keyBytes;
        var iv = allBytes[..16];
        var cipher = allBytes[16..];
        aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
        return Encoding.UTF8.GetString(plainBytes);
    }

    private static byte[] DeriveKeyBytes(string key)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(Encoding.UTF8.GetBytes(key));
    }

    // ── DB path / passphrase derivation ──

    private string GetDbPath(string seed)
    {
        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(
            Encoding.UTF8.GetBytes("qubic-walletdb-" + seed))).ToLowerInvariant();
        return Path.Combine(_settings.StorageDirectory, hash[..16] + ".walletdb");
    }

    private static string GetPassphrase(string seed)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(
            Encoding.UTF8.GetBytes("qubic-wallet-db-" + seed))).ToLowerInvariant();
    }

    public void Dispose()
    {
        Close();
    }
}
