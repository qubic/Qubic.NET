using System.Text;
using Microsoft.Data.Sqlite;

namespace Qubic.Services.Storage;

/// <summary>
/// Low-level encrypted SQLite database for wallet storage.
/// Thread-safe writes via SemaphoreSlim.
/// </summary>
public sealed class WalletDatabase : IDisposable
{
    private const int SchemaVersion = 3;

    private SqliteConnection? _connection;
    private string? _passphrase;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public bool IsOpen => _connection?.State == System.Data.ConnectionState.Open;
    public string? DatabasePath { get; private set; }

    /// <summary>
    /// Opens the database at the specified path with the given passphrase.
    /// Creates schema if not present, runs migrations if needed.
    /// </summary>
    public void Open(string dbPath, string passphrase)
    {
        if (_connection != null)
            Close();

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Password = passphrase,
            Mode = SqliteOpenMode.ReadWriteCreate
        };

        _connection = new SqliteConnection(csb.ToString());
        _connection.Open();
        DatabasePath = dbPath;
        _passphrase = passphrase;

        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA foreign_keys=ON;");

        EnsureSchema();
    }

    public void Close()
    {
        if (_connection != null)
        {
            _connection.Close();
            _connection.Dispose();
            _connection = null;
            DatabasePath = null;
            _passphrase = null;
        }
    }

    /// <summary>Flushes the WAL to the main database file.</summary>
    public void Checkpoint()
    {
        if (_connection == null) return;
        Execute("PRAGMA wal_checkpoint(TRUNCATE);");
    }

    /// <summary>
    /// Replaces the database file with imported data and re-opens it.
    /// </summary>
    public void ReplaceAndReopen(byte[] data)
    {
        if (DatabasePath == null || _passphrase == null)
            throw new InvalidOperationException("No database is open.");

        var path = DatabasePath;
        var pass = _passphrase;
        Close();

        File.Delete(path + "-wal");
        File.Delete(path + "-shm");
        File.WriteAllBytes(path, data);

        Open(path, pass);
    }

    // ── Schema ──

    private void EnsureSchema()
    {
        var version = GetSchemaVersion();
        if (version == 0)
        {
            CreateSchema();
            SetSchemaVersion(SchemaVersion);
        }
        else if (version < SchemaVersion)
        {
            RunMigrations(version);
            SetSchemaVersion(SchemaVersion);
        }
    }

    private int GetSchemaVersion()
    {
        try
        {
            using var cmd = CreateCommand("SELECT value FROM schema_info WHERE key='version'");
            var result = cmd.ExecuteScalar();
            return result != null ? int.Parse((string)result) : 0;
        }
        catch (SqliteException)
        {
            return 0; // table doesn't exist yet
        }
    }

    private void SetSchemaVersion(int version)
    {
        Execute("INSERT OR REPLACE INTO schema_info (key, value) VALUES ('version', @v)",
            ("@v", version.ToString()));
    }

    private void CreateSchema()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS schema_info (key TEXT PRIMARY KEY, value TEXT NOT NULL);

            CREATE TABLE IF NOT EXISTS transactions (
                hash            TEXT PRIMARY KEY,
                source          TEXT NOT NULL,
                destination     TEXT NOT NULL,
                amount          TEXT NOT NULL,
                tick            INTEGER NOT NULL,
                timestamp_ms    INTEGER,
                input_type      INTEGER NOT NULL DEFAULT 0,
                input_size      INTEGER NOT NULL DEFAULT 0,
                input_data      TEXT,
                signature       TEXT,
                money_flew      INTEGER,
                success         INTEGER,
                synced_from     TEXT NOT NULL,
                synced_at_utc   TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_tx_tick ON transactions(tick);
            CREATE INDEX IF NOT EXISTS idx_tx_source ON transactions(source);
            CREATE INDEX IF NOT EXISTS idx_tx_dest ON transactions(destination);

            CREATE TABLE IF NOT EXISTS tracked_transactions (
                hash            TEXT PRIMARY KEY,
                source          TEXT NOT NULL,
                destination     TEXT NOT NULL,
                amount          TEXT NOT NULL,
                target_tick     INTEGER NOT NULL,
                description     TEXT NOT NULL DEFAULT '',
                status          INTEGER NOT NULL DEFAULT 0,
                created_utc     TEXT NOT NULL,
                resolved_utc    TEXT,
                money_flew      INTEGER,
                input_type      INTEGER NOT NULL DEFAULT 0,
                payload_hex     TEXT,
                resend_count    INTEGER NOT NULL DEFAULT 0,
                previous_hash   TEXT,
                raw_data        TEXT
            );

            CREATE TABLE IF NOT EXISTS log_events (
                epoch           INTEGER NOT NULL,
                log_id          INTEGER NOT NULL,
                tick            INTEGER NOT NULL,
                log_type        INTEGER NOT NULL,
                log_type_name   TEXT,
                tx_hash         TEXT,
                body            TEXT,
                body_raw        TEXT,
                log_digest      TEXT,
                body_size       INTEGER NOT NULL DEFAULT 0,
                timestamp       TEXT,
                synced_from     TEXT NOT NULL,
                synced_at_utc   TEXT NOT NULL,
                PRIMARY KEY (epoch, log_id)
            );
            CREATE INDEX IF NOT EXISTS idx_log_tick ON log_events(tick);
            CREATE INDEX IF NOT EXISTS idx_log_tx ON log_events(tx_hash);
            CREATE INDEX IF NOT EXISTS idx_log_type ON log_events(log_type);
            CREATE INDEX IF NOT EXISTS idx_log_epoch ON log_events(epoch);

            CREATE TABLE IF NOT EXISTS sync_watermarks (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """);
    }

    private void RunMigrations(int fromVersion)
    {
        if (fromVersion < 2)
        {
            // Change log_events PK from log_id alone to composite (epoch, log_id)
            // since logIds are epoch-based and reset per epoch.
            Execute("""
                CREATE TABLE IF NOT EXISTS log_events_v2 (
                    epoch           INTEGER NOT NULL,
                    log_id          INTEGER NOT NULL,
                    tick            INTEGER NOT NULL,
                    log_type        INTEGER NOT NULL,
                    log_type_name   TEXT,
                    tx_hash         TEXT,
                    body            TEXT,
                    body_raw        TEXT,
                    log_digest      TEXT,
                    body_size       INTEGER NOT NULL DEFAULT 0,
                    timestamp       TEXT,
                    synced_from     TEXT NOT NULL,
                    synced_at_utc   TEXT NOT NULL,
                    PRIMARY KEY (epoch, log_id)
                );
                INSERT OR IGNORE INTO log_events_v2
                    SELECT epoch, log_id, tick, log_type, log_type_name, tx_hash,
                           body, body_raw, log_digest, body_size, timestamp,
                           synced_from, synced_at_utc
                    FROM log_events;
                DROP TABLE log_events;
                ALTER TABLE log_events_v2 RENAME TO log_events;
                CREATE INDEX IF NOT EXISTS idx_log_tick ON log_events(tick);
                CREATE INDEX IF NOT EXISTS idx_log_tx ON log_events(tx_hash);
                CREATE INDEX IF NOT EXISTS idx_log_type ON log_events(log_type);
                CREATE INDEX IF NOT EXISTS idx_log_epoch ON log_events(epoch);
                """);
        }

        if (fromVersion < 3)
        {
            Execute("ALTER TABLE tracked_transactions ADD COLUMN raw_data TEXT");
        }
    }

    // ── Transactions CRUD ──

    public void UpsertTransaction(StoredTransaction tx)
    {
        _writeLock.Wait();
        try
        {
            Execute("""
                INSERT INTO transactions (hash, source, destination, amount, tick, timestamp_ms,
                    input_type, input_size, input_data, signature, money_flew, success, synced_from, synced_at_utc)
                VALUES (@hash, @src, @dst, @amt, @tick, @ts, @itype, @isize, @idata, @sig, @mf, @suc, @sf, @sat)
                ON CONFLICT(hash) DO UPDATE SET
                    timestamp_ms   = COALESCE(excluded.timestamp_ms, transactions.timestamp_ms),
                    input_data     = COALESCE(excluded.input_data, transactions.input_data),
                    input_size     = CASE WHEN excluded.input_size > 0 THEN excluded.input_size ELSE transactions.input_size END,
                    signature      = COALESCE(excluded.signature, transactions.signature),
                    money_flew     = COALESCE(excluded.money_flew, transactions.money_flew),
                    success        = COALESCE(excluded.success, transactions.success),
                    synced_from    = @sf_merged,
                    synced_at_utc  = excluded.synced_at_utc
                """,
                ("@hash", tx.Hash),
                ("@src", tx.Source),
                ("@dst", tx.Destination),
                ("@amt", tx.Amount),
                ("@tick", (long)tx.Tick),
                ("@ts", tx.TimestampMs.HasValue ? (object)tx.TimestampMs.Value : DBNull.Value),
                ("@itype", (long)tx.InputType),
                ("@isize", (long)tx.InputSize),
                ("@idata", (object?)tx.InputData ?? DBNull.Value),
                ("@sig", (object?)tx.Signature ?? DBNull.Value),
                ("@mf", tx.MoneyFlew.HasValue ? (object)(tx.MoneyFlew.Value ? 1L : 0L) : DBNull.Value),
                ("@suc", tx.Success.HasValue ? (object)(tx.Success.Value ? 1L : 0L) : DBNull.Value),
                ("@sf", tx.SyncedFrom),
                ("@sat", tx.SyncedAtUtc),
                ("@sf_merged", GetMergedSyncedFrom(tx.Hash, tx.SyncedFrom))
            );
        }
        finally { _writeLock.Release(); }
    }

    public void UpsertTransactions(IEnumerable<StoredTransaction> txs)
    {
        _writeLock.Wait();
        try
        {
            Execute("BEGIN TRANSACTION");
            try
            {
                foreach (var tx in txs)
                {
                    Execute("""
                        INSERT INTO transactions (hash, source, destination, amount, tick, timestamp_ms,
                            input_type, input_size, input_data, signature, money_flew, success, synced_from, synced_at_utc)
                        VALUES (@hash, @src, @dst, @amt, @tick, @ts, @itype, @isize, @idata, @sig, @mf, @suc, @sf, @sat)
                        ON CONFLICT(hash) DO UPDATE SET
                            timestamp_ms   = COALESCE(excluded.timestamp_ms, transactions.timestamp_ms),
                            input_data     = COALESCE(excluded.input_data, transactions.input_data),
                            input_size     = CASE WHEN excluded.input_size > 0 THEN excluded.input_size ELSE transactions.input_size END,
                            signature      = COALESCE(excluded.signature, transactions.signature),
                            money_flew     = COALESCE(excluded.money_flew, transactions.money_flew),
                            success        = COALESCE(excluded.success, transactions.success),
                            synced_from    = @sf_merged,
                            synced_at_utc  = excluded.synced_at_utc
                        """,
                        ("@hash", tx.Hash),
                        ("@src", tx.Source),
                        ("@dst", tx.Destination),
                        ("@amt", tx.Amount),
                        ("@tick", (long)tx.Tick),
                        ("@ts", tx.TimestampMs.HasValue ? (object)tx.TimestampMs.Value : DBNull.Value),
                        ("@itype", (long)tx.InputType),
                        ("@isize", (long)tx.InputSize),
                        ("@idata", (object?)tx.InputData ?? DBNull.Value),
                        ("@sig", (object?)tx.Signature ?? DBNull.Value),
                        ("@mf", tx.MoneyFlew.HasValue ? (object)(tx.MoneyFlew.Value ? 1L : 0L) : DBNull.Value),
                        ("@suc", tx.Success.HasValue ? (object)(tx.Success.Value ? 1L : 0L) : DBNull.Value),
                        ("@sf", tx.SyncedFrom),
                        ("@sat", tx.SyncedAtUtc),
                        ("@sf_merged", GetMergedSyncedFrom(tx.Hash, tx.SyncedFrom))
                    );
                }
                Execute("COMMIT");
            }
            catch
            {
                Execute("ROLLBACK");
                throw;
            }
        }
        finally { _writeLock.Release(); }
    }

    public List<StoredTransaction> GetTransactions(TransactionQuery query, string identity)
    {
        if (!IsOpen) return [];
        var sb = new StringBuilder("SELECT * FROM transactions WHERE 1=1");
        var parms = new List<(string, object)>();

        switch (query.Direction)
        {
            case TransactionDirection.Sent:
                sb.Append(" AND source=@id");
                parms.Add(("@id", identity));
                break;
            case TransactionDirection.Received:
                sb.Append(" AND destination=@id");
                parms.Add(("@id", identity));
                break;
            default:
                sb.Append(" AND (source=@id OR destination=@id)");
                parms.Add(("@id", identity));
                break;
        }

        if (query.HashType == TxHashType.User)
            sb.Append(" AND length(hash)=60 AND hash NOT GLOB '*[^a-z]*'");
        else if (query.HashType == TxHashType.System)
            sb.Append(" AND NOT (length(hash)=60 AND hash NOT GLOB '*[^a-z]*')");

        if (query.MinTick.HasValue) { sb.Append(" AND tick>=@mint"); parms.Add(("@mint", (long)query.MinTick.Value)); }
        if (query.MaxTick.HasValue) { sb.Append(" AND tick<=@maxt"); parms.Add(("@maxt", (long)query.MaxTick.Value)); }
        if (query.InputType.HasValue) { sb.Append(" AND input_type=@it"); parms.Add(("@it", (long)query.InputType.Value)); }
        if (!string.IsNullOrEmpty(query.Destination)) { sb.Append(" AND destination=@dest"); parms.Add(("@dest", query.Destination)); }
        if (!string.IsNullOrEmpty(query.SearchHash)) { sb.Append(" AND hash LIKE @sh"); parms.Add(("@sh", "%" + query.SearchHash + "%")); }

        sb.Append(query.SortOrder == TransactionSortOrder.TickAsc ? " ORDER BY tick ASC" : " ORDER BY tick DESC");
        sb.Append(" LIMIT @lim OFFSET @off");
        parms.Add(("@lim", (long)query.Limit));
        parms.Add(("@off", (long)query.Offset));

        return ReadTransactions(sb.ToString(), parms.ToArray());
    }

    public StoredTransaction? GetTransactionByHash(string hash)
    {
        if (!IsOpen) return null;
        var results = ReadTransactions("SELECT * FROM transactions WHERE hash=@h LIMIT 1", ("@h", hash));
        return results.Count > 0 ? results[0] : null;
    }

    public int GetTransactionCount()
    {
        if (!IsOpen) return 0;
        using var cmd = CreateCommand("SELECT COUNT(*) FROM transactions");
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ── Tracked Transactions ──

    public void UpsertTrackedTransaction(TrackedTransaction tx)
    {
        _writeLock.Wait();
        try
        {
            Execute("""
                INSERT OR REPLACE INTO tracked_transactions
                    (hash, source, destination, amount, target_tick, description, status, created_utc,
                     resolved_utc, money_flew, input_type, payload_hex, resend_count, previous_hash, raw_data)
                VALUES (@hash, @src, @dst, @amt, @tick, @desc, @st, @cut, @rut, @mf, @it, @ph, @rc, @prev, @rd)
                """,
                ("@hash", tx.Hash),
                ("@src", tx.Source),
                ("@dst", tx.Destination),
                ("@amt", tx.Amount.ToString()),
                ("@tick", (long)tx.TargetTick),
                ("@desc", tx.Description),
                ("@st", (long)tx.Status),
                ("@cut", tx.CreatedUtc.ToString("O")),
                ("@rut", tx.ResolvedUtc.HasValue ? (object)tx.ResolvedUtc.Value.ToString("O") : DBNull.Value),
                ("@mf", tx.MoneyFlew.HasValue ? (object)(tx.MoneyFlew.Value ? 1L : 0L) : DBNull.Value),
                ("@it", (long)tx.InputType),
                ("@ph", (object?)tx.PayloadHex ?? DBNull.Value),
                ("@rc", (long)tx.ResendCount),
                ("@prev", (object?)tx.PreviousHash ?? DBNull.Value),
                ("@rd", (object?)tx.RawData ?? DBNull.Value)
            );
        }
        finally { _writeLock.Release(); }
    }

    public List<TrackedTransaction> GetTrackedTransactions()
    {
        if (!IsOpen) return [];
        var results = new List<TrackedTransaction>();
        using var cmd = CreateCommand("SELECT * FROM tracked_transactions ORDER BY created_utc DESC");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new TrackedTransaction
            {
                Hash = reader.GetString(reader.GetOrdinal("hash")),
                Source = reader.GetString(reader.GetOrdinal("source")),
                Destination = reader.GetString(reader.GetOrdinal("destination")),
                Amount = long.TryParse(reader.GetString(reader.GetOrdinal("amount")), out var a) ? a : 0,
                TargetTick = (uint)reader.GetInt64(reader.GetOrdinal("target_tick")),
                Description = reader.GetString(reader.GetOrdinal("description")),
                Status = (TrackedTxStatus)reader.GetInt64(reader.GetOrdinal("status")),
                CreatedUtc = DateTime.TryParse(reader.GetString(reader.GetOrdinal("created_utc")), out var c) ? c : DateTime.UtcNow,
                ResolvedUtc = reader.IsDBNull(reader.GetOrdinal("resolved_utc")) ? null : DateTime.TryParse(reader.GetString(reader.GetOrdinal("resolved_utc")), out var r) ? r : null,
                MoneyFlew = reader.IsDBNull(reader.GetOrdinal("money_flew")) ? null : reader.GetInt64(reader.GetOrdinal("money_flew")) == 1,
                InputType = (ushort)reader.GetInt64(reader.GetOrdinal("input_type")),
                PayloadHex = reader.IsDBNull(reader.GetOrdinal("payload_hex")) ? null : reader.GetString(reader.GetOrdinal("payload_hex")),
                ResendCount = (int)reader.GetInt64(reader.GetOrdinal("resend_count")),
                PreviousHash = reader.IsDBNull(reader.GetOrdinal("previous_hash")) ? null : reader.GetString(reader.GetOrdinal("previous_hash")),
                RawData = reader.IsDBNull(reader.GetOrdinal("raw_data")) ? null : reader.GetString(reader.GetOrdinal("raw_data"))
            });
        }
        return results;
    }

    public void DeleteTrackedTransaction(string hash)
    {
        _writeLock.Wait();
        try { Execute("DELETE FROM tracked_transactions WHERE hash=@h", ("@h", hash)); }
        finally { _writeLock.Release(); }
    }

    public void ClearTrackedTransactions()
    {
        _writeLock.Wait();
        try { Execute("DELETE FROM tracked_transactions"); }
        finally { _writeLock.Release(); }
    }

    // ── Log Events ──

    public void InsertLogEvent(StoredLogEvent log)
    {
        _writeLock.Wait();
        try
        {
            Execute("""
                INSERT OR IGNORE INTO log_events
                    (log_id, tick, epoch, log_type, log_type_name, tx_hash, body, body_raw, log_digest, body_size, timestamp, synced_from, synced_at_utc)
                VALUES (@lid, @tick, @epoch, @lt, @ltn, @txh, @body, @braw, @ld, @bs, @ts, @sf, @sat)
                """,
                ("@lid", log.LogId),
                ("@tick", (long)log.Tick),
                ("@epoch", (long)log.Epoch),
                ("@lt", (long)log.LogType),
                ("@ltn", (object?)log.LogTypeName ?? DBNull.Value),
                ("@txh", (object?)log.TxHash ?? DBNull.Value),
                ("@body", (object?)log.Body ?? DBNull.Value),
                ("@braw", (object?)log.BodyRaw ?? DBNull.Value),
                ("@ld", (object?)log.LogDigest ?? DBNull.Value),
                ("@bs", (long)log.BodySize),
                ("@ts", (object?)log.Timestamp ?? DBNull.Value),
                ("@sf", log.SyncedFrom),
                ("@sat", log.SyncedAtUtc)
            );
        }
        finally { _writeLock.Release(); }
    }

    public void InsertLogEvents(IEnumerable<StoredLogEvent> logs)
    {
        _writeLock.Wait();
        try
        {
            Execute("BEGIN TRANSACTION");
            try
            {
                foreach (var log in logs)
                {
                    Execute("""
                        INSERT OR IGNORE INTO log_events
                            (log_id, tick, epoch, log_type, log_type_name, tx_hash, body, body_raw, log_digest, body_size, timestamp, synced_from, synced_at_utc)
                        VALUES (@lid, @tick, @epoch, @lt, @ltn, @txh, @body, @braw, @ld, @bs, @ts, @sf, @sat)
                        """,
                        ("@lid", log.LogId),
                        ("@tick", (long)log.Tick),
                        ("@epoch", (long)log.Epoch),
                        ("@lt", (long)log.LogType),
                        ("@ltn", (object?)log.LogTypeName ?? DBNull.Value),
                        ("@txh", (object?)log.TxHash ?? DBNull.Value),
                        ("@body", (object?)log.Body ?? DBNull.Value),
                        ("@braw", (object?)log.BodyRaw ?? DBNull.Value),
                        ("@ld", (object?)log.LogDigest ?? DBNull.Value),
                        ("@bs", (long)log.BodySize),
                        ("@ts", (object?)log.Timestamp ?? DBNull.Value),
                        ("@sf", log.SyncedFrom),
                        ("@sat", log.SyncedAtUtc)
                    );
                }
                Execute("COMMIT");
            }
            catch
            {
                Execute("ROLLBACK");
                throw;
            }
        }
        finally { _writeLock.Release(); }
    }

    public List<StoredLogEvent> GetLogEvents(LogEventQuery query)
    {
        if (!IsOpen) return [];
        var sb = new StringBuilder("SELECT * FROM log_events WHERE 1=1");
        var parms = new List<(string, object)>();

        if (query.LogType.HasValue) { sb.Append(" AND log_type=@lt"); parms.Add(("@lt", (long)query.LogType.Value)); }
        if (query.ContractIndex.HasValue) { sb.Append(" AND json_extract(body, '$._contractIndex')=@ci"); parms.Add(("@ci", (long)query.ContractIndex.Value)); }
        if (query.MinTick.HasValue) { sb.Append(" AND tick>=@mint"); parms.Add(("@mint", (long)query.MinTick.Value)); }
        if (query.MaxTick.HasValue) { sb.Append(" AND tick<=@maxt"); parms.Add(("@maxt", (long)query.MaxTick.Value)); }
        if (query.Epoch.HasValue) { sb.Append(" AND epoch=@ep"); parms.Add(("@ep", (long)query.Epoch.Value)); }
        if (!string.IsNullOrEmpty(query.TxHash)) { sb.Append(" AND tx_hash=@txh"); parms.Add(("@txh", query.TxHash)); }

        sb.Append(" ORDER BY epoch DESC, log_id DESC LIMIT @lim OFFSET @off");
        parms.Add(("@lim", (long)query.Limit));
        parms.Add(("@off", (long)query.Offset));

        return ReadLogEvents(sb.ToString(), parms.ToArray());
    }

    public int GetLogEventCount()
    {
        if (!IsOpen) return 0;
        using var cmd = CreateCommand("SELECT COUNT(*) FROM log_events");
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Returns tx_hashes referenced by log events that don't exist in the transactions table.
    /// </summary>
    public List<string> GetMissingLogTransactionHashes()
    {
        if (!IsOpen) return [];
        var results = new List<string>();
        using var cmd = CreateCommand("""
            SELECT DISTINCT le.tx_hash FROM log_events le
            WHERE le.tx_hash IS NOT NULL AND le.tx_hash != ''
            AND NOT EXISTS (SELECT 1 FROM transactions t WHERE t.hash = le.tx_hash)
            """);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(reader.GetString(0));
        return results;
    }

    // ── Watermarks ──

    public string? GetWatermark(string key)
    {
        if (!IsOpen) return null;
        using var cmd = CreateCommand("SELECT value FROM sync_watermarks WHERE key=@k", ("@k", key));
        var result = cmd.ExecuteScalar();
        return result as string;
    }

    public void SetWatermark(string key, string value)
    {
        _writeLock.Wait();
        try
        {
            Execute("INSERT OR REPLACE INTO sync_watermarks (key, value) VALUES (@k, @v)",
                ("@k", key), ("@v", value));
        }
        finally { _writeLock.Release(); }
    }

    public Dictionary<string, string> GetAllWatermarks()
    {
        if (!IsOpen) return new();
        var result = new Dictionary<string, string>();
        using var cmd = CreateCommand("SELECT key, value FROM sync_watermarks");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    public void DeleteWatermark(string key)
    {
        _writeLock.Wait();
        try
        {
            Execute("DELETE FROM sync_watermarks WHERE key=@k", ("@k", key));
        }
        finally { _writeLock.Release(); }
    }

    public void ClearLogEvents()
    {
        _writeLock.Wait();
        try
        {
            Execute("DELETE FROM log_events");
        }
        finally { _writeLock.Release(); }
    }

    public void ClearTransactions()
    {
        _writeLock.Wait();
        try
        {
            Execute("DELETE FROM transactions");
        }
        finally { _writeLock.Release(); }
    }

    // ── Helpers ──

    private string GetMergedSyncedFrom(string hash, string newSource)
    {
        try
        {
            using var cmd = CreateCommand("SELECT synced_from FROM transactions WHERE hash=@h", ("@h", hash));
            var existing = cmd.ExecuteScalar() as string;
            if (string.IsNullOrEmpty(existing))
                return newSource;

            var sources = new HashSet<string>(existing.Split(',', StringSplitOptions.RemoveEmptyEntries));
            foreach (var s in newSource.Split(',', StringSplitOptions.RemoveEmptyEntries))
                sources.Add(s);
            return string.Join(",", sources);
        }
        catch
        {
            return newSource;
        }
    }

    private List<StoredTransaction> ReadTransactions(string sql, params (string, object)[] parms)
    {
        var results = new List<StoredTransaction>();
        using var cmd = CreateCommand(sql, parms);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new StoredTransaction
            {
                Hash = reader.GetString(reader.GetOrdinal("hash")),
                Source = reader.GetString(reader.GetOrdinal("source")),
                Destination = reader.GetString(reader.GetOrdinal("destination")),
                Amount = reader.GetString(reader.GetOrdinal("amount")),
                Tick = (uint)reader.GetInt64(reader.GetOrdinal("tick")),
                TimestampMs = reader.IsDBNull(reader.GetOrdinal("timestamp_ms")) ? null : reader.GetInt64(reader.GetOrdinal("timestamp_ms")),
                InputType = (uint)reader.GetInt64(reader.GetOrdinal("input_type")),
                InputSize = (uint)reader.GetInt64(reader.GetOrdinal("input_size")),
                InputData = reader.IsDBNull(reader.GetOrdinal("input_data")) ? null : reader.GetString(reader.GetOrdinal("input_data")),
                Signature = reader.IsDBNull(reader.GetOrdinal("signature")) ? null : reader.GetString(reader.GetOrdinal("signature")),
                MoneyFlew = reader.IsDBNull(reader.GetOrdinal("money_flew")) ? null : reader.GetInt64(reader.GetOrdinal("money_flew")) == 1,
                Success = reader.IsDBNull(reader.GetOrdinal("success")) ? null : reader.GetInt64(reader.GetOrdinal("success")) == 1,
                SyncedFrom = reader.GetString(reader.GetOrdinal("synced_from")),
                SyncedAtUtc = reader.GetString(reader.GetOrdinal("synced_at_utc"))
            });
        }
        return results;
    }

    private List<StoredLogEvent> ReadLogEvents(string sql, params (string, object)[] parms)
    {
        var results = new List<StoredLogEvent>();
        using var cmd = CreateCommand(sql, parms);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new StoredLogEvent
            {
                LogId = reader.GetInt64(reader.GetOrdinal("log_id")),
                Tick = (uint)reader.GetInt64(reader.GetOrdinal("tick")),
                Epoch = (uint)reader.GetInt64(reader.GetOrdinal("epoch")),
                LogType = (int)reader.GetInt64(reader.GetOrdinal("log_type")),
                LogTypeName = reader.IsDBNull(reader.GetOrdinal("log_type_name")) ? null : reader.GetString(reader.GetOrdinal("log_type_name")),
                TxHash = reader.IsDBNull(reader.GetOrdinal("tx_hash")) ? null : reader.GetString(reader.GetOrdinal("tx_hash")),
                Body = reader.IsDBNull(reader.GetOrdinal("body")) ? null : reader.GetString(reader.GetOrdinal("body")),
                BodyRaw = reader.IsDBNull(reader.GetOrdinal("body_raw")) ? null : reader.GetString(reader.GetOrdinal("body_raw")),
                LogDigest = reader.IsDBNull(reader.GetOrdinal("log_digest")) ? null : reader.GetString(reader.GetOrdinal("log_digest")),
                BodySize = (int)reader.GetInt64(reader.GetOrdinal("body_size")),
                Timestamp = reader.IsDBNull(reader.GetOrdinal("timestamp")) ? null : reader.GetString(reader.GetOrdinal("timestamp")),
                SyncedFrom = reader.GetString(reader.GetOrdinal("synced_from")),
                SyncedAtUtc = reader.GetString(reader.GetOrdinal("synced_at_utc"))
            });
        }
        return results;
    }

    private SqliteCommand CreateCommand(string sql, params (string name, object value)[] parms)
    {
        if (_connection == null)
            throw new InvalidOperationException("Database is not open.");
        var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parms)
            cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }

    private void Execute(string sql, params (string name, object value)[] parms)
    {
        using var cmd = CreateCommand(sql, parms);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
        _writeLock.Dispose();
    }
}
