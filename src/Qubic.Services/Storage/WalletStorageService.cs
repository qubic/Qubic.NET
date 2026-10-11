using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Qubic.Bob; // BobClient for fetching current logId
using Qubic.Services.Networks;

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
    private readonly QubicBackendService _backend;
    private readonly NetworkProfileService _profiles;
    private readonly NetworkChainState _chainState;
    private string? _identity;
    private string? _seed;
    private string? _openProfileId;

    private const string ChainGenerationKey = "chain_generation";

    public bool IsOpen => _db.IsOpen;
    public WalletDatabase Database => _db;
    public WalletSyncService Sync => _sync;
    public string? Identity => _identity;

    public event Action? OnStateChanged;

    /// <summary>Network profile whose database is currently open, or null.</summary>
    public string? OpenProfileId => _openProfileId;

    public WalletStorageService(WalletDatabase db, WalletSyncService sync, QubicSettingsService settings,
        QubicBackendService backend, NetworkProfileService profiles, NetworkChainState chainState)
    {
        _db = db;
        _sync = sync;
        _settings = settings;
        _backend = backend;
        _profiles = profiles;
        _chainState = chainState;
    }

    /// <summary>
    /// Opens the encrypted database for this seed and starts background sync.
    /// When <paramref name="skipInitialSync"/> is true (e.g. freshly generated seeds with no history),
    /// sync is skipped and watermarks are initialized so that future sync starts from the current epoch.
    /// </summary>
    public void SetSeed(string seed, string identity, uint currentEpoch = 0, bool skipInitialSync = false)
    {
        Close();

        _identity = identity;
        _seed = seed;
        var profileId = _profiles.ActiveId;
        _openProfileId = profileId;

        var dbPath = GetDbPath(seed, profileId);
        var isNew = !File.Exists(dbPath);
        var passphrase = GetPassphrase(seed);
        _db.Open(dbPath, passphrase);

        if (profileId == NetworkPresets.MainnetId)
            MigrateLegacyTxdb(seed);

        ApplyChainGeneration(profileId, restartSync: false);

        if (skipInitialSync && isNew && currentEpoch > 0)
        {
            // Seed watermarks so future sync starts from current epoch, not from the beginning
            _db.SetWatermark("bob_log_last_epoch", currentEpoch.ToString());
            _db.SetWatermark("bob_log_last_logid", "0");
            // Fetch the current logId from Bob in the background to refine the watermark
            _ = FetchAndSetCurrentLogIdAsync();
        }

        if (!skipInitialSync || !isNew)
            _sync.Start(identity);
        OnStateChanged?.Invoke();
    }

    /// <summary>
    /// Fetches the current epoch info from Bob and updates the logId watermark.
    /// Best-effort — failures are silently ignored since we already have epoch as fallback.
    /// </summary>
    private async Task FetchAndSetCurrentLogIdAsync()
    {
        try
        {
            if (!_backend.HasMethod(Networks.BackendMethodKind.Bob)) return;
            using var bob = _backend.CreateBobClient();
            var epochInfo = await bob.GetCurrentEpochAsync();
            var logId = epochInfo.GetLastLogId();
            if (logId > 0 && _db.IsOpen)
                _db.SetWatermark("bob_log_last_logid", logId.ToString());
        }
        catch { /* best effort — epoch watermark is already set as fallback */ }
    }

    /// <summary>
    /// Stops sync and closes the database.
    /// </summary>
    public void ClearSeed()
    {
        Close();
        _seed = null;
        OnStateChanged?.Invoke();
    }

    private void Close()
    {
        _sync.Stop();
        _db.Close();
        _identity = null;
        _openProfileId = null;
    }

    /// <summary>
    /// Re-opens the current seed's database for the active network profile
    /// (each network has its own wallet DB). No-op if no seed is open.
    /// </summary>
    public void ReopenForActiveProfile(uint currentEpoch = 0)
    {
        if (_seed == null || _identity == null) return;
        if (_openProfileId == _profiles.ActiveId && IsOpen) return;
        var seed = _seed;
        var identity = _identity;
        SetSeed(seed, identity, currentEpoch);
    }

    /// <summary>
    /// If a chain reset was detected for <paramref name="profileId"/> after this DB last synced,
    /// purge its cached history so stale transactions from the previous chain disappear.
    /// Returns true if data was purged.
    /// </summary>
    public bool ApplyChainGeneration(string profileId, bool restartSync = true)
    {
        if (!IsOpen || _openProfileId != profileId) return false;
        var generation = _chainState.GetGeneration(profileId);
        var dbGeneration = int.TryParse(_db.GetWatermark(ChainGenerationKey), out var g) ? g : 0;
        if (dbGeneration >= generation) return false;

        if (restartSync) _sync.Stop();
        _db.ClearTransactions();
        _db.ClearLogEvents();
        _db.ClearTrackedTransactions();
        foreach (var key in _db.GetAllWatermarks().Keys)
            _db.DeleteWatermark(key);
        _db.SetWatermark(ChainGenerationKey, generation.ToString());
        if (restartSync && _identity != null) _sync.Start(_identity);
        OnStateChanged?.Invoke();
        return true;
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

    /// <summary>Resets both Bob log watermarks (log ID + epoch) and restarts sync.</summary>
    public void ResetBobLogsAndResync()
    {
        if (!IsOpen || _identity == null) return;
        _sync.Stop();
        _db.DeleteWatermark("bob_log_last_logid");
        _db.DeleteWatermark("bob_log_last_epoch");
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
        _sync.Start(_identity);
    }

    /// <summary>Gets all current sync watermarks.</summary>
    public Dictionary<string, string> GetWatermarks()
    {
        if (!IsOpen) return new();
        return _db.GetAllWatermarks();
    }

    // ── Import / Export ──

    /// <summary>
    /// Exports the current database file as a byte array.
    /// Flushes WAL first to ensure the file contains all data.
    /// </summary>
    public byte[] ExportDatabase()
    {
        if (!IsOpen || _db.DatabasePath == null)
            throw new InvalidOperationException("No database is open.");

        _db.Checkpoint();
        return File.ReadAllBytes(_db.DatabasePath);
    }

    /// <summary>
    /// Imports a database file, replacing the current one.
    /// Stops sync, replaces the file, re-opens, and restarts sync.
    /// </summary>
    public void ImportDatabase(byte[] data)
    {
        if (!IsOpen || _identity == null)
            throw new InvalidOperationException("No database is open.");

        var identity = _identity;
        _sync.Stop();
        _db.ReplaceAndReopen(data);
        _sync.Start(identity);
        OnStateChanged?.Invoke();
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

    /// <summary>
    /// Mainnet keeps the pre-profile name (&lt;hash&gt;.walletdb); other networks get
    /// &lt;hash&gt;.&lt;profileId&gt;.walletdb so histories never mix.
    /// </summary>
    private string GetDbPath(string seed, string profileId)
    {
        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(
            Encoding.UTF8.GetBytes("qubic-walletdb-" + seed))).ToLowerInvariant();
        return Path.Combine(_settings.StorageDirectory, hash[..16] + NetworkStorage.Suffix(profileId) + ".walletdb");
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
