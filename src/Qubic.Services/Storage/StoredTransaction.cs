namespace Qubic.Services.Storage;

/// <summary>
/// Represents a transaction stored in the local SQLite database.
/// </summary>
public sealed class StoredTransaction
{
    public string Hash { get; set; } = "";
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Amount { get; set; } = "0";
    public uint Tick { get; set; }
    public long? TimestampMs { get; set; }
    public uint InputType { get; set; }
    public uint InputSize { get; set; }
    public string? InputData { get; set; }
    public string? Signature { get; set; }
    public bool? MoneyFlew { get; set; }
    public bool? Success { get; set; }
    /// <summary>Comma-separated sources, e.g. "rpc,bob,local"</summary>
    public string SyncedFrom { get; set; } = "";
    public string SyncedAtUtc { get; set; } = "";

    public long GetAmountAsLong() =>
        long.TryParse(Amount, out var v) ? v : 0;
}
