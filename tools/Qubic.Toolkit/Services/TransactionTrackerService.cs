using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Qubic.Core.Entities;

namespace Qubic.Toolkit;

public enum TrackedTxStatus
{
    Pending,    // Broadcast, waiting for target tick
    Confirmed,  // MoneyFlew = true or receipt Status = true
    Failed,     // MoneyFlew = false or receipt Status = false
    Unknown     // Tick passed but couldn't determine status
}

public sealed class TrackedTransaction
{
    public string Hash { get; set; } = "";
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public long Amount { get; set; }
    public uint TargetTick { get; set; }
    public string Description { get; set; } = "";
    public TrackedTxStatus Status { get; set; } = TrackedTxStatus.Pending;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedUtc { get; set; }
    public bool? MoneyFlew { get; set; }

    // Auto-resend support
    public ushort InputType { get; set; }
    public string? PayloadHex { get; set; }
    public int ResendCount { get; set; }
    public string? PreviousHash { get; set; }
}

public sealed class TransactionTrackerService : IDisposable
{
    private readonly ToolkitBackendService _backend;
    private readonly TickMonitorService _tickMonitor;
    private readonly ToolkitSettingsService _settings;
    private readonly SeedSessionService _seed;
    private readonly List<TrackedTransaction> _transactions = [];
    private readonly object _lock = new();
    private string? _storageKey;
    private string? _storageDir;
    private CancellationTokenSource? _cts;
    private Task? _monitorTask;

    public IReadOnlyList<TrackedTransaction> Transactions
    {
        get { lock (_lock) return _transactions.ToList(); }
    }

    public event Action? OnChanged;

    private void RaiseChanged()
    {
        var handler = OnChanged;
        if (handler == null) return;
        foreach (var d in handler.GetInvocationList())
        {
            try { ((Action)d)(); }
            catch { /* subscriber may be disposed */ }
        }
    }

    public TransactionTrackerService(ToolkitBackendService backend, TickMonitorService tickMonitor,
        ToolkitSettingsService settings, SeedSessionService seed)
    {
        _backend = backend;
        _tickMonitor = tickMonitor;
        _settings = settings;
        _seed = seed;
        _storageDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QubicToolkit");

        _cts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorLoop(_cts.Token));
    }

    public void SetEncryptionKey(string seed)
    {
        using var sha = SHA256.Create();
        _storageKey = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes("qubic-toolkit-tx-" + seed))).ToLowerInvariant();
        LoadFromDisk();
    }

    public void ClearEncryptionKey()
    {
        _storageKey = null;
        lock (_lock) _transactions.Clear();
        RaiseChanged();
    }

    public void Track(TrackedTransaction tx)
    {
        lock (_lock)
        {
            // Avoid duplicates
            if (_transactions.Any(t => t.Hash == tx.Hash)) return;
            _transactions.Insert(0, tx);
        }
        SaveToDisk();
        RaiseChanged();
    }

    public void Remove(string hash)
    {
        lock (_lock) _transactions.RemoveAll(t => t.Hash == hash);
        SaveToDisk();
        RaiseChanged();
    }

    public void Clear()
    {
        lock (_lock) _transactions.Clear();
        SaveToDisk();
        RaiseChanged();
    }

    /// <summary>
    /// Repeats a transaction: re-creates it with a fresh tick, signs, broadcasts, and tracks it.
    /// Requires an active seed.
    /// </summary>
    public async Task<string> RepeatAsync(TrackedTransaction original)
    {
        if (!_seed.HasSeed)
            throw new InvalidOperationException("No seed is set. Enter your seed first.");
        if (!_tickMonitor.IsConnected)
            throw new InvalidOperationException("Not connected. Cannot determine current tick.");

        var dest = QubicIdentity.FromIdentity(original.Destination);
        var newTick = _tickMonitor.Tick + (uint)_settings.TickOffset;

        QubicTransaction tx;
        if (original.InputType == 0 && string.IsNullOrEmpty(original.PayloadHex))
        {
            tx = _seed.CreateAndSignTransfer(dest, original.Amount, newTick);
        }
        else
        {
            var data = string.IsNullOrEmpty(original.PayloadHex) ? [] : Convert.FromHexString(original.PayloadHex);
            var payload = new Qubic.Toolkit.Models.GenericContractPayload(original.InputType, data);
            tx = _seed.CreateAndSignTransaction(dest, original.Amount, newTick, payload);
        }

        var result = await _backend.BroadcastTransactionAsync(tx);

        var description = original.Description;
        if (description.StartsWith("[Resend ") || description.StartsWith("[Repeat]"))
        {
            var idx = description.IndexOf("] ");
            if (idx >= 0) description = description[(idx + 2)..];
        }

        var repeat = new TrackedTransaction
        {
            Hash = result.TransactionId,
            Source = _seed.Identity?.ToString() ?? original.Source,
            Destination = original.Destination,
            Amount = original.Amount,
            TargetTick = newTick,
            InputType = original.InputType,
            PayloadHex = original.PayloadHex,
            PreviousHash = original.Hash,
            Description = $"[Repeat] {description}"
        };

        Track(repeat);
        return result.TransactionId;
    }

    /// <summary>Returns the path to the encrypted storage file, or null if no seed is set.</summary>
    public string? CurrentStoragePath => _storageKey != null ? GetFilePath() : null;

    /// <summary>Exports all tracked transactions as unencrypted JSON (for backup/transfer).</summary>
    public string ExportJson()
    {
        List<TrackedTransaction> snapshot;
        lock (_lock) snapshot = _transactions.ToList();
        return JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Imports transactions from JSON, merging with existing (skips duplicates by hash).</summary>
    public int ImportJson(string json)
    {
        var imported = JsonSerializer.Deserialize<List<TrackedTransaction>>(json);
        if (imported == null || imported.Count == 0) return 0;

        int added = 0;
        lock (_lock)
        {
            foreach (var tx in imported)
            {
                if (_transactions.Any(t => t.Hash == tx.Hash)) continue;
                _transactions.Add(tx);
                added++;
            }
        }

        if (added > 0)
        {
            SaveToDisk();
            RaiseChanged();
        }
        return added;
    }

    private async Task MonitorLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(3000, ct);
                await CheckPendingTransactions(ct);
            }
            catch (OperationCanceledException) { break; }
            catch { /* retry next cycle */ }
        }
    }

    private async Task CheckPendingTransactions(CancellationToken ct)
    {
        List<TrackedTransaction> pending;
        lock (_lock)
        {
            pending = _transactions.Where(t => t.Status == TrackedTxStatus.Pending).ToList();
        }

        if (pending.Count == 0 || !_tickMonitor.IsConnected) return;

        var currentTick = _tickMonitor.Tick;
        bool changed = false;

        foreach (var tx in pending)
        {
            if (ct.IsCancellationRequested) break;
            if (tx.TargetTick > currentTick) continue; // Not yet reached

            // Tick has passed, check status
            try
            {
                bool confirmed = false;

                // Try RPC first (has MoneyFlew)
                if (_backend.ActiveBackend == QueryBackend.Rpc)
                {
                    var info = await _backend.GetTransactionByHashAsync(tx.Hash, ct);
                    if (info != null)
                    {
                        tx.MoneyFlew = info.MoneyFlew;
                        if (info.MoneyFlew == true)
                        {
                            tx.Status = TrackedTxStatus.Confirmed;
                            tx.ResolvedUtc = DateTime.UtcNow;
                            changed = true;
                            confirmed = true;
                        }
                        else if (info.MoneyFlew == false)
                        {
                            tx.Status = TrackedTxStatus.Failed;
                            tx.ResolvedUtc = DateTime.UtcNow;
                            changed = true;
                        }
                    }
                }

                // Try Bob receipt
                if (!confirmed && _backend.ActiveBackend == QueryBackend.Bob)
                {
                    var receipt = await _backend.GetTransactionReceiptAsync(tx.Hash, ct);
                    if (receipt != null)
                    {
                        tx.MoneyFlew = receipt.Status;
                        tx.Status = receipt.Status ? TrackedTxStatus.Confirmed : TrackedTxStatus.Failed;
                        tx.ResolvedUtc = DateTime.UtcNow;
                        changed = true;
                        confirmed = receipt.Status;
                    }
                }

                if (confirmed) continue;

                // If tick passed significantly and still no info, mark unknown
                if (currentTick > tx.TargetTick + 20)
                {
                    tx.Status = TrackedTxStatus.Unknown;
                    tx.ResolvedUtc = DateTime.UtcNow;
                    changed = true;
                }

                // Auto-resend: if tx failed/unknown and resend is enabled
                if (tx.Status is TrackedTxStatus.Failed or TrackedTxStatus.Unknown
                    && _settings.AutoResend
                    && _seed.HasSeed
                    && tx.ResendCount < _settings.AutoResendMaxRetries)
                {
                    var resent = await TryResend(tx, ct);
                    if (resent) changed = true;
                }
            }
            catch { /* will retry next cycle */ }
        }

        if (changed)
        {
            SaveToDisk();
            RaiseChanged();
        }
    }

    private async Task<bool> TryResend(TrackedTransaction original, CancellationToken ct)
    {
        try
        {
            var dest = QubicIdentity.FromIdentity(original.Destination);
            var newTick = _tickMonitor.Tick + (uint)_settings.TickOffset;

            QubicTransaction tx;
            if (original.InputType == 0 && string.IsNullOrEmpty(original.PayloadHex))
            {
                // Simple transfer
                tx = _seed.CreateAndSignTransfer(dest, original.Amount, newTick);
            }
            else
            {
                // Contract call with payload
                var data = string.IsNullOrEmpty(original.PayloadHex) ? [] : Convert.FromHexString(original.PayloadHex);
                var payload = new Qubic.Toolkit.Models.GenericContractPayload(original.InputType, data);
                tx = _seed.CreateAndSignTransaction(dest, original.Amount, newTick, payload);
            }

            var result = await _backend.BroadcastTransactionAsync(tx);

            // Track the new transaction
            var resend = new TrackedTransaction
            {
                Hash = result.TransactionId,
                Source = original.Source,
                Destination = original.Destination,
                Amount = original.Amount,
                TargetTick = newTick,
                InputType = original.InputType,
                PayloadHex = original.PayloadHex,
                ResendCount = original.ResendCount + 1,
                PreviousHash = original.Hash,
                Description = $"[Resend #{original.ResendCount + 1}] {original.Description}"
            };

            lock (_lock) _transactions.Insert(0, resend);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SaveToDisk()
    {
        if (_storageKey == null || _storageDir == null) return;
        try
        {
            Directory.CreateDirectory(_storageDir);
            List<TrackedTransaction> snapshot;
            lock (_lock) snapshot = _transactions.ToList();
            var json = JsonSerializer.Serialize(snapshot);
            var encrypted = Encrypt(json, _storageKey);
            File.WriteAllText(GetFilePath(), encrypted);
        }
        catch { /* best effort */ }
    }

    private void LoadFromDisk()
    {
        if (_storageKey == null) return;
        try
        {
            var path = GetFilePath();
            if (!File.Exists(path)) return;
            var encrypted = File.ReadAllText(path);
            var json = Decrypt(encrypted, _storageKey);
            var loaded = JsonSerializer.Deserialize<List<TrackedTransaction>>(json);
            if (loaded != null)
            {
                lock (_lock)
                {
                    _transactions.Clear();
                    _transactions.AddRange(loaded);
                }
                RaiseChanged();
            }
        }
        catch { /* corrupted or wrong key */ }
    }

    private string GetFilePath()
        => Path.Combine(_storageDir!, _storageKey![..16] + ".txdb");

    private static string Encrypt(string plaintext, string key)
    {
        var keyBytes = DeriveKeyBytes(key);
        using var aes = Aes.Create();
        aes.Key = keyBytes;
        aes.GenerateIV();
        using var encryptor = aes.CreateEncryptor();
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = encryptor.TransformFinalBlock(plaintextBytes, 0, plaintextBytes.Length);
        var result = new byte[aes.IV.Length + cipherBytes.Length];
        aes.IV.CopyTo(result, 0);
        cipherBytes.CopyTo(result, aes.IV.Length);
        return Convert.ToBase64String(result);
    }

    private static string Decrypt(string ciphertext, string key)
    {
        var keyBytes = DeriveKeyBytes(key);
        var allBytes = Convert.FromBase64String(ciphertext);
        using var aes = Aes.Create();
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

    public void Dispose()
    {
        _cts?.Cancel();
        try { _monitorTask?.Wait(2000); } catch { }
        _cts?.Dispose();
    }
}
