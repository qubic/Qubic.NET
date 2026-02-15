namespace Qubic.Services.Storage;

/// <summary>
/// Represents a log event stored in the local SQLite database.
/// </summary>
public sealed class StoredLogEvent
{
    public long LogId { get; set; }
    public uint Tick { get; set; }
    public uint Epoch { get; set; }
    public int LogType { get; set; }
    public string? LogTypeName { get; set; }
    public string? TxHash { get; set; }
    /// <summary>JSON-serialized body</summary>
    public string? Body { get; set; }
    /// <summary>Hex-encoded raw binary body</summary>
    public string? BodyRaw { get; set; }
    public string? LogDigest { get; set; }
    public int BodySize { get; set; }
    public string? Timestamp { get; set; }
    public string SyncedFrom { get; set; } = "";
    public string SyncedAtUtc { get; set; } = "";
}
