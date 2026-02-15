namespace Qubic.Services.Storage;

public enum TransactionDirection { All, Sent, Received }
public enum TransactionSortOrder { TickDesc, TickAsc }

/// <summary>
/// Filter and pagination parameters for querying stored transactions.
/// </summary>
public sealed class TransactionQuery
{
    public TransactionDirection Direction { get; set; } = TransactionDirection.All;
    public uint? MinTick { get; set; }
    public uint? MaxTick { get; set; }
    public uint? InputType { get; set; }
    public string? SearchHash { get; set; }
    public TransactionSortOrder SortOrder { get; set; } = TransactionSortOrder.TickDesc;
    public int Offset { get; set; }
    public int Limit { get; set; } = 50;
}
