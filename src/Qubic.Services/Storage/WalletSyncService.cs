using System.Text.Json;
using Qubic.Bob;
using Qubic.Bob.Models;
using Qubic.Rpc;
using Qubic.Rpc.Models;

namespace Qubic.Services.Storage;

public enum SyncState { Idle, Syncing, Error }

public enum StreamStatus { Idle, Connecting, CatchingUp, Live, Error, Done }

/// <summary>
/// Background sync orchestrator with concurrent streams:
/// 1. RPC paginated transaction sync (long-term archive)
/// 2. Bob WebSocket log subscription (real-time + catch-up, covers all event types including transfers)
/// 3. Missing transaction fetch (fetches full tx data for tx_hashes referenced in logs)
/// </summary>
public sealed class WalletSyncService : IDisposable
{
    private readonly QubicBackendService _backend;
    private readonly WalletDatabase _db;

    private CancellationTokenSource? _cts;
    private Task? _rpcTask;
    private Task? _bobLogTask;
    private Task? _missingTxTask;
    private volatile bool _rpcInitialDone;
    private volatile bool _bobLogInitialDone;

    public SyncState State { get; private set; } = SyncState.Idle;
    public int TransactionsSynced { get; private set; }
    public int LogEventsSynced { get; private set; }
    public int RpcTotal { get; private set; }
    public double SyncProgress => RpcTotal > 0 ? Math.Min(100.0, TransactionsSynced * 100.0 / RpcTotal) : 0;
    public string? LastError { get; private set; }
    public int MissingTxFetched { get; private set; }
    /// <summary>True once both RPC and Bob log streams have completed their initial catch-up.</summary>
    public bool InitialSyncComplete => _rpcInitialDone && _bobLogInitialDone;

    // Per-stream status for UI visibility
    public StreamStatus RpcStatus { get; private set; } = StreamStatus.Idle;
    public string? RpcStatusMessage { get; private set; }
    public StreamStatus BobLogStatus { get; private set; } = StreamStatus.Idle;
    public string? BobLogStatusMessage { get; private set; }
    public double? BobLogCatchUpPercent { get; private set; }
    public uint? BobLogCatchUpEpoch { get; private set; }

    // Sync log for diagnostics
    private readonly List<string> _syncLog = new();
    private const int MaxLogEntries = 200;
    public IReadOnlyList<string> SyncLog => _syncLog;

    public event Action? OnSyncStateChanged;

    public string? CurrentIdentity { get; private set; }
    public string CurrentBobUrl => _backend.BobUrl;
    public string CurrentRpcUrl => _backend.RpcUrl;

    public WalletSyncService(QubicBackendService backend, WalletDatabase db)
    {
        _backend = backend;
        _db = db;
    }

    private void Log(string message)
    {
        var entry = $"[{DateTime.UtcNow:HH:mm:ss}] {message}";
        lock (_syncLog)
        {
            _syncLog.Add(entry);
            if (_syncLog.Count > MaxLogEntries)
                _syncLog.RemoveAt(0);
        }
    }

    public void ClearLog()
    {
        lock (_syncLog) { _syncLog.Clear(); }
    }

    public void Start(string identity)
    {
        Stop();

        CurrentIdentity = identity;
        _cts = new CancellationTokenSource();
        State = SyncState.Syncing;
        TransactionsSynced = 0;
        LogEventsSynced = 0;
        RpcTotal = 0;
        MissingTxFetched = 0;
        LastError = null;
        Log($"Starting sync for {identity[..8]}... RPC={_backend.RpcUrl} Bob={_backend.BobUrl}");
        _rpcInitialDone = false;
        _bobLogInitialDone = false;
        RpcStatus = StreamStatus.Idle;
        RpcStatusMessage = null;
        BobLogStatus = StreamStatus.Idle;
        BobLogStatusMessage = null;
        BobLogCatchUpPercent = null;
        BobLogCatchUpEpoch = null;

        var ct = _cts.Token;
        _rpcTask = Task.Run(() => RpcSyncLoop(identity, ct), ct);
        _bobLogTask = Task.Run(() => BobLogSyncLoop(identity, ct), ct);
        _missingTxTask = Task.Run(() => MissingTxSyncLoop(identity, ct), ct);

        RaiseChanged();
    }

    public void Stop()
    {
        if (_cts != null)
            Log("Stopping sync...");

        _cts?.Cancel();

        try { _rpcTask?.Wait(3000); } catch { }
        try { _bobLogTask?.Wait(3000); } catch { }
        try { _missingTxTask?.Wait(3000); } catch { }

        _cts?.Dispose();
        _cts = null;
        _rpcTask = null;
        _bobLogTask = null;
        _missingTxTask = null;

        State = SyncState.Idle;
        RpcStatus = StreamStatus.Idle;
        BobLogStatus = StreamStatus.Idle;
        BobLogCatchUpPercent = null;
        BobLogCatchUpEpoch = null;
        RaiseChanged();
    }

    // ── Stream 1: RPC Paginated Transaction Sync ──

    private async Task RpcSyncLoop(string identity, CancellationToken ct)
    {
        const int pageSize = 100;
        const string watermarkKey = "rpc_last_offset";

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    RpcStatus = StreamStatus.Connecting;
                    RpcStatusMessage = "Connecting to RPC...";
                    Log("RPC: Connecting...");
                    RaiseChanged();

                    using var rpc = new QubicRpcClient(_backend.RpcUrl);

                    var offsetStr = _db.GetWatermark(watermarkKey);
                    var offset = uint.TryParse(offsetStr, out var o) ? o : 0u;

                    RpcStatus = StreamStatus.CatchingUp;
                    RpcStatusMessage = $"Fetching from offset {offset}...";
                    Log($"RPC: Starting from offset {offset}");
                    RaiseChanged();

                    var hasMore = true;
                    while (hasMore && !ct.IsCancellationRequested)
                    {
                        var result = await rpc.GetTransactionsForIdentityAsync(
                            new TransactionsForIdentityRequest
                            {
                                Identity = identity,
                                Pagination = new PaginationOptions { Offset = offset, Size = pageSize }
                            }, ct);

                        RpcTotal = (int)result.Hits.Total;

                        Log($"RPC: Fetched {result.Transactions.Count} tx (offset {offset}, total {result.Hits.Total})");

                        if (result.Transactions.Count > 0)
                        {
                            var now = DateTime.UtcNow.ToString("O");
                            var stored = result.Transactions.Select(tx => new StoredTransaction
                            {
                                Hash = tx.Hash,
                                Source = tx.Source,
                                Destination = tx.Destination,
                                Amount = tx.Amount,
                                Tick = tx.TickNumber,
                                TimestampMs = ParseTimestampMs(tx.Timestamp),
                                InputType = tx.InputType,
                                InputSize = tx.InputSize,
                                InputData = string.IsNullOrEmpty(tx.InputData) ? null : tx.InputData,
                                Signature = string.IsNullOrEmpty(tx.Signature) ? null : tx.Signature,
                                MoneyFlew = tx.MoneyFlew,
                                SyncedFrom = "rpc",
                                SyncedAtUtc = now
                            }).ToList();

                            _db.UpsertTransactions(stored);
                            TransactionsSynced = (int)offset + stored.Count;
                            RpcStatusMessage = $"{TransactionsSynced}/{RpcTotal} tx fetched";
                            RaiseChanged();
                        }

                        offset += (uint)result.Transactions.Count;
                        _db.SetWatermark(watermarkKey, offset.ToString());

                        hasMore = result.Transactions.Count == pageSize && offset < result.Hits.Total;
                    }

                    // Initial sync pass complete
                    if (!_rpcInitialDone)
                    {
                        _rpcInitialDone = true;
                        RpcStatus = StreamStatus.Live;
                        RpcStatusMessage = $"Done — {TransactionsSynced} tx synced, polling every 60s";
                        Log($"RPC: Initial sync complete — {TransactionsSynced} tx synced");
                        CheckInitialSyncComplete();
                        RaiseChanged();
                    }
                    else
                    {
                        RpcStatus = StreamStatus.Live;
                        RpcStatusMessage = $"Idle — polling in 60s";
                        RaiseChanged();
                    }

                    // Poll incrementally
                    await Task.Delay(TimeSpan.FromSeconds(60), ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    LastError = $"RPC sync: {ex.Message}";
                    RpcStatus = StreamStatus.Error;
                    RpcStatusMessage = $"Error: {ex.Message}";
                    Log($"RPC: Error — {ex.Message}");
                    if (!_rpcInitialDone)
                    {
                        _rpcInitialDone = true;
                        CheckInitialSyncComplete();
                    }
                    RaiseChanged();
                    Log("RPC: Retrying in 30s...");
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── Stream 2: Bob WebSocket Log Subscription ──

    private async Task BobLogSyncLoop(string identity, CancellationToken ct)
    {
        const string watermarkKey = "bob_log_last_logid";
        const string epochWatermarkKey = "bob_log_last_epoch";
        const string prevEpochDoneKey = "bob_log_prev_epoch_done";

        try
        {
            while (!ct.IsCancellationRequested)
            {
                BobWebSocketClient? wsClient = null;
                try
                {
                    BobLogStatus = StreamStatus.Connecting;
                    BobLogStatusMessage = "Connecting to Bob WS...";
                    Log("Bob Logs: Connecting...");
                    RaiseChanged();

                    var wsOptions = new BobWebSocketOptions
                    {
                        Nodes = [_backend.BobUrl],
                        OnConnectionEvent = evt => Log($"Bob WS: [{evt.Type}] {evt.Message}")
                    };
                    wsClient = new BobWebSocketClient(wsOptions);
                    await wsClient.ConnectAsync(ct);
                    Log("Bob Logs: Connected");

                    // ── Phase 1: Previous epochs indexed fetch (up to epoch-2) ──
                    var currentEpochInfo = await wsClient.GetCurrentEpochAsync(ct);
                    var currentEpoch = currentEpochInfo.Epoch;
                    Log($"Bob Logs: Current epoch = {currentEpoch}");

                    var prevEpochDoneStr = _db.GetWatermark(prevEpochDoneKey);
                    var prevEpochDone = uint.TryParse(prevEpochDoneStr, out var ped) ? ped : 0u;

                    var oldestEpoch = currentEpoch >= 2 ? currentEpoch - 2 : 1u;
                    for (var catchUpEpoch = oldestEpoch; catchUpEpoch < currentEpoch; catchUpEpoch++)
                    {
                        if (prevEpochDone >= catchUpEpoch)
                        {
                            Log($"Bob Logs: Phase 1 — epoch {catchUpEpoch} already caught up, skipping");
                            continue;
                        }

                        BobLogStatus = StreamStatus.CatchingUp;
                        BobLogCatchUpEpoch = catchUpEpoch;
                        Log($"Bob Logs: Phase 1 — fetching epoch {catchUpEpoch} logs via indexed lookup");
                        RaiseChanged();

                        var epochInfo = await wsClient.GetEpochInfoAsync((int)catchUpEpoch, ct);
                        var count = await FetchEpochLogsIndexed(wsClient, catchUpEpoch, epochInfo, identity, ct);

                        Log($"Bob Logs: Phase 1 — epoch {catchUpEpoch} done, {count} matching logs");
                        _db.SetWatermark(prevEpochDoneKey, catchUpEpoch.ToString());
                        BobLogCatchUpPercent = null;
                        BobLogCatchUpEpoch = null;
                    }

                    // ── Phase 2: Current epoch indexed fetch ──
                    BobLogStatus = StreamStatus.CatchingUp;
                    BobLogCatchUpEpoch = currentEpoch;
                    Log($"Bob Logs: Phase 2 — fetching current epoch {currentEpoch} logs via indexed lookup");
                    RaiseChanged();

                    // Snapshot the current latestLogId BEFORE the indexed fetch.
                    // After the fetch, we know there are no matching logs between
                    // the last match and this point, so the subscription can start here.
                    currentEpochInfo = await wsClient.GetCurrentEpochAsync(ct);
                    var snapshotLogId = currentEpochInfo.GetLastLogId();
                    Log($"Bob Logs: Phase 2 — snapshotLogId={snapshotLogId}");

                    var phase2Count = await FetchEpochLogsIndexed(wsClient, currentEpoch, currentEpochInfo, identity, ct);
                    Log($"Bob Logs: Phase 2 complete — {phase2Count} matching logs from epoch {currentEpoch}");

                    // Set watermark to the snapshot so subscription skips the gap
                    _db.SetWatermark(watermarkKey, snapshotLogId.ToString());
                    _db.SetWatermark(epochWatermarkKey, currentEpoch.ToString());

                    BobLogCatchUpPercent = null;
                    BobLogCatchUpEpoch = null;

                    // ── Phase 3: Subscribe for live logs ──
                    var options = new LogSubscriptionOptions
                    {
                        Identities = [identity],
                        StartLogId = snapshotLogId,
                        StartEpoch = currentEpoch
                    };

                    BobLogStatus = StreamStatus.CatchingUp;
                    BobLogStatusMessage = $"Subscribing for live logs...";
                    Log($"Bob Logs: Phase 3 — subscribing from logId {snapshotLogId} epoch {currentEpoch}");
                    RaiseChanged();

                    var sub = await wsClient.SubscribeLogsAsync(options, ct);
                    Log($"Bob Logs: Subscribed (subId={sub.ServerSubscriptionId ?? "null"}), awaiting data...");
                    var catchUpCount = 0;

                    await foreach (var notification in sub.WithCancellation(ct))
                    {
                        // catchUpProgress: update progress bar, continue
                        if (notification.CatchUpProgress)
                        {
                            BobLogCatchUpPercent = notification.Percent;
                            BobLogCatchUpEpoch = notification.Epoch > 0 ? notification.Epoch : currentEpoch;
                            BobLogStatusMessage = $"Catching up: {notification.Percent:F0}%";
                            RaiseChanged();
                            continue;
                        }

                        // catchUpComplete: transition to live
                        if (notification.CatchUpComplete)
                        {
                            if (notification.LastPosition.HasValue)
                            {
                                _db.SetWatermark(watermarkKey, notification.LastPosition.Value.ToString());
                                _db.SetWatermark(epochWatermarkKey,
                                    (notification.Epoch > 0 ? notification.Epoch : currentEpoch).ToString());
                            }
                            _bobLogInitialDone = true;
                            BobLogStatus = StreamStatus.Live;
                            BobLogCatchUpPercent = null;
                            BobLogCatchUpEpoch = null;
                            BobLogStatusMessage = $"Live — {catchUpCount} logs caught up";
                            Log($"Bob Logs: Catch-up complete — {catchUpCount} logs caught up, now live");
                            CheckInitialSyncComplete();
                            RaiseChanged();
                            continue;
                        }

                        StoreLogNotification(notification);
                        _db.SetWatermark(watermarkKey, notification.LogId.ToString());
                        _db.SetWatermark(epochWatermarkKey, notification.Epoch.ToString());

                        if (!_bobLogInitialDone)
                        {
                            catchUpCount++;
                            if (catchUpCount % 100 == 0)
                                Log($"Bob Logs: Subscription catch-up: {catchUpCount} logs (logId {notification.LogId})");
                        }
                        else
                        {
                            Log($"Bob Logs: Live log tick={notification.Tick} type={notification.LogType} ({notification.LogTypeName})");
                        }
                        RaiseChanged();
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    LastError = $"Bob log sync: {ex.Message}";
                    BobLogStatus = StreamStatus.Error;
                    BobLogStatusMessage = $"Error: {ex.Message}";
                    BobLogCatchUpPercent = null;
                    BobLogCatchUpEpoch = null;
                    Log($"Bob Logs: Error — {ex.Message}");
                    if (!_bobLogInitialDone)
                    {
                        _bobLogInitialDone = true;
                        CheckInitialSyncComplete();
                    }
                    RaiseChanged();
                    Log("Bob Logs: Retrying in 10s...");
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                }
                finally
                {
                    if (wsClient is IAsyncDisposable ad)
                        await ad.DisposeAsync();
                    else
                        wsClient?.Dispose();
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// 3-step indexed log fetch: findLogIds → getTickLogRanges → getLogsByIdRange.
    /// Returns the number of matching logs stored.
    /// </summary>
    private async Task<int> FetchEpochLogsIndexed(
        BobWebSocketClient wsClient, uint epoch, EpochInfoResponse epochInfo,
        string identity, CancellationToken ct)
    {
        var initialTick = (uint)epochInfo.GetInitialTick();
        var endTick = (uint)epochInfo.GetEndTick();
        if (endTick == 0) endTick = (uint)epochInfo.GetFinalTick();
        if (endTick == 0) endTick = (uint)epochInfo.GetCurrentTick();
        if (endTick == 0) endTick = (uint)epochInfo.GetLastIndexedTick();
        Log($"Bob Logs: Epoch {epoch} ticks {initialTick}–{endTick}");

        if (initialTick == 0 || endTick == 0 || endTick < initialTick)
        {
            Log($"Bob Logs: Epoch {epoch} — invalid tick range, skipping");
            return 0;
        }

        // Step 1: Find ticks with matching logs
        BobLogStatusMessage = $"Epoch {epoch}: finding matching ticks...";
        RaiseChanged();

        var filter = new FindLogIdsFilter
        {
            ScIndex = 0,
            LogType = 0,
            Topic1 = identity,
            FromTick = initialTick,
            ToTick = endTick
        };
        var matchingTicks = await wsClient.FindLogIdsAsync(filter, ct);
        Log($"Bob Logs: Epoch {epoch} — findLogIds returned {matchingTicks.Count} ticks");

        if (matchingTicks.Count == 0)
            return 0;

        // Step 2: Get logId ranges (batch by 1000)
        BobLogStatusMessage = $"Epoch {epoch}: getting log ranges for {matchingTicks.Count} ticks...";
        RaiseChanged();

        var allRanges = new List<TickLogRange>();
        for (int i = 0; i < matchingTicks.Count; i += 1000)
        {
            var tickBatch = matchingTicks.Skip(i).Take(1000).ToArray();
            var ranges = await wsClient.GetTickLogRangesAsync(tickBatch, ct);
            allRanges.AddRange(ranges);
        }

        // Merge adjacent/overlapping ranges
        var merged = MergeLogRanges(allRanges);
        var totalLogIds = merged.Sum(r => r.ToId - r.FromId + 1);
        Log($"Bob Logs: Epoch {epoch} — {allRanges.Count} tick ranges → {merged.Count} merged ranges ({totalLogIds} logIds)");

        // Step 3: Fetch logs by merged ranges
        long processedLogIds = 0;
        int matchCount = 0;

        foreach (var range in merged)
        {
            for (long fromId = range.FromId; fromId <= range.ToId; fromId += 100_000)
            {
                ct.ThrowIfCancellationRequested();
                var toId = Math.Min(fromId + 99_999, range.ToId);
                var logs = await wsClient.GetLogsByIdRangeAsync(epoch, fromId, toId, ct);

                foreach (var entry in logs)
                {
                    if (!entry.Ok) continue;

                    // Client-side identity filter
                    if (entry.Body.HasValue)
                    {
                        var bodyStr = entry.Body.Value.ToString();
                        if (!bodyStr.Contains(identity, StringComparison.Ordinal))
                            continue;
                    }
                    else
                    {
                        continue;
                    }

                    StoreLogNotification(entry);
                    matchCount++;
                }

                processedLogIds += toId - fromId + 1;
                BobLogCatchUpPercent = totalLogIds > 0
                    ? Math.Min(100.0, processedLogIds * 100.0 / totalLogIds)
                    : 100.0;
                BobLogStatusMessage = $"Epoch {epoch}: fetching logs — {BobLogCatchUpPercent:F0}%";
                RaiseChanged();
            }
        }

        return matchCount;
    }

    /// <summary>Merges adjacent/overlapping tick log ranges into minimal fetch ranges.</summary>
    private static List<(long FromId, long ToId)> MergeLogRanges(List<TickLogRange> ranges)
    {
        var valid = ranges
            .Where(r => r.FromLogId.HasValue && r.Length.HasValue && r.Length.Value > 0)
            .OrderBy(r => r.FromLogId!.Value)
            .ToList();

        var merged = new List<(long FromId, long ToId)>();
        foreach (var r in valid)
        {
            long fromId = r.FromLogId!.Value;
            long toId = fromId + r.Length!.Value - 1;

            if (merged.Count > 0 && fromId <= merged[^1].ToId + 1)
            {
                var last = merged[^1];
                merged[^1] = (last.FromId, Math.Max(last.ToId, toId));
            }
            else
            {
                merged.Add((fromId, toId));
            }
        }
        return merged;
    }

    /// <summary>Converts a LogNotification to a StoredLogEvent and inserts it into the database.</summary>
    private void StoreLogNotification(LogNotification notification)
    {
        var now = DateTime.UtcNow.ToString("O");

        string? bodyJson = null;
        string? bodyRaw = null;
        if (notification.Body.HasValue)
        {
            bodyJson = notification.Body.Value.ToString();
            var rawBytes = System.Text.Encoding.UTF8.GetBytes(bodyJson);
            bodyRaw = Convert.ToHexString(rawBytes);
        }

        var logEvent = new StoredLogEvent
        {
            LogId = notification.LogId,
            Tick = notification.Tick,
            Epoch = notification.Epoch,
            LogType = notification.LogType,
            LogTypeName = string.IsNullOrEmpty(notification.LogTypeName) ? null : notification.LogTypeName,
            TxHash = notification.TxHash,
            Body = bodyJson,
            BodyRaw = bodyRaw,
            LogDigest = notification.LogDigest,
            BodySize = notification.BodySize,
            Timestamp = notification.GetTimestamp(),
            SyncedFrom = "bob_ws",
            SyncedAtUtc = now
        };

        _db.InsertLogEvent(logEvent);
        LogEventsSynced++;
    }

    // ── Stream 3: Fetch Missing Transactions Referenced by Logs ──

    private async Task MissingTxSyncLoop(string identity, CancellationToken ct)
    {
        try
        {
            // Wait a bit for log sync to populate data first
            await Task.Delay(TimeSpan.FromSeconds(10), ct);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var missingHashes = _db.GetMissingLogTransactionHashes();
                    if (missingHashes.Count > 0)
                    {
                        Log($"Missing TX: Found {missingHashes.Count} transactions referenced by logs but not stored");

                        // Split into standard hashes (60 lowercase chars) fetchable from RPC,
                        // and non-standard hashes (e.g. SC_END_TICK_TX_*) that need Bob.
                        var standardHashes = missingHashes.Where(IsStandardTxHash).ToList();
                        var nonStandardHashes = missingHashes.Where(h => !IsStandardTxHash(h)).ToList();

                        var fetched = 0;

                        // Fetch standard hashes via RPC (richer data: MoneyFlew, Signature, etc.)
                        if (standardHashes.Count > 0)
                        {
                            using var rpc = new QubicRpcClient(_backend.RpcUrl);
                            foreach (var hash in standardHashes)
                            {
                                if (ct.IsCancellationRequested) break;
                                try
                                {
                                    var txInfo = await rpc.GetTransactionByHashAsync(hash, ct);
                                    if (txInfo != null)
                                    {
                                        var now = DateTime.UtcNow.ToString("O");
                                        _db.UpsertTransaction(new StoredTransaction
                                        {
                                            Hash = txInfo.Hash,
                                            Source = txInfo.Source,
                                            Destination = txInfo.Destination,
                                            Amount = txInfo.Amount,
                                            Tick = txInfo.TickNumber,
                                            TimestampMs = ParseTimestampMs(txInfo.Timestamp),
                                            InputType = txInfo.InputType,
                                            InputSize = txInfo.InputSize,
                                            InputData = string.IsNullOrEmpty(txInfo.InputData) ? null : txInfo.InputData,
                                            Signature = string.IsNullOrEmpty(txInfo.Signature) ? null : txInfo.Signature,
                                            MoneyFlew = txInfo.MoneyFlew,
                                            SyncedFrom = "log_ref",
                                            SyncedAtUtc = now
                                        });
                                        fetched++;
                                        MissingTxFetched++;
                                    }
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception ex)
                                {
                                    Log($"Missing TX: RPC failed for {hash[..Math.Min(16, hash.Length)]}... — {ex.Message}");
                                }
                            }
                        }

                        // Fetch non-standard hashes via Bob (e.g. SC_END_TICK_TX_*)
                        if (nonStandardHashes.Count > 0 && !ct.IsCancellationRequested)
                        {
                            using var bob = new BobClient(_backend.BobUrl);
                            foreach (var hash in nonStandardHashes)
                            {
                                if (ct.IsCancellationRequested) break;
                                try
                                {
                                    var bobTx = await bob.GetTransactionByHashAsync(hash, ct);
                                    if (bobTx != null)
                                    {
                                        var now = DateTime.UtcNow.ToString("O");
                                        _db.UpsertTransaction(new StoredTransaction
                                        {
                                            Hash = hash,
                                            Source = bobTx.Source.ToString(),
                                            Destination = bobTx.Destination.ToString(),
                                            Amount = bobTx.Amount.ToString(),
                                            Tick = bobTx.Tick,
                                            SyncedFrom = "log_ref_bob",
                                            SyncedAtUtc = now
                                        });
                                        fetched++;
                                        MissingTxFetched++;
                                    }
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception ex)
                                {
                                    Log($"Missing TX: Bob failed for {hash[..Math.Min(16, hash.Length)]}... — {ex.Message}");
                                }
                            }
                        }

                        if (fetched > 0)
                        {
                            Log($"Missing TX: Fetched {fetched}/{missingHashes.Count} transactions ({standardHashes.Count} RPC, {nonStandardHashes.Count} Bob)");
                            RaiseChanged();
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log($"Missing TX: Error — {ex.Message}");
                }

                await Task.Delay(TimeSpan.FromSeconds(60), ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Standard Qubic tx hashes are 60 lowercase alphabetic characters.
    /// Non-standard hashes (e.g. SC_END_TICK_TX_44150530) must be fetched from Bob.
    /// </summary>
    private static bool IsStandardTxHash(string hash) =>
        hash.Length == 60 && hash.All(c => c >= 'a' && c <= 'z');

    // ── Helpers ──

    private void CheckInitialSyncComplete()
    {
        if (State == SyncState.Syncing && _rpcInitialDone && _bobLogInitialDone)
        {
            State = SyncState.Idle;
        }
    }

    private static long? ParseTimestampMs(string? timestamp)
    {
        if (string.IsNullOrEmpty(timestamp)) return null;
        if (long.TryParse(timestamp, out var ms))
            return ms;
        if (DateTimeOffset.TryParse(timestamp, out var dto))
            return dto.ToUnixTimeMilliseconds();
        return null;
    }

    private void RaiseChanged()
    {
        var handler = OnSyncStateChanged;
        if (handler == null) return;
        foreach (var d in handler.GetInvocationList())
        {
            try { ((Action)d)(); }
            catch { }
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
