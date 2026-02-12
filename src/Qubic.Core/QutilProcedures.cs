namespace Qubic.Core;

/// <summary>
/// QUTIL contract procedure IDs.
/// </summary>
[Obsolete("Use Qubic.Core.Contracts.Qutil.QutilContract.Procedures instead.")]
public static class QutilProcedures
{
    /// <summary>
    /// Send QU to up to 25 recipients.
    /// </summary>
    public const ushort SendToManyV1 = 1;

    /// <summary>
    /// Burn QU (remove from circulation).
    /// </summary>
    public const ushort BurnQubic = 2;

    /// <summary>
    /// SendToMany benchmark (testing).
    /// </summary>
    public const ushort SendToManyBenchmark = 3;

    /// <summary>
    /// Create a governance poll.
    /// </summary>
    public const ushort CreatePoll = 4;

    /// <summary>
    /// Vote on a poll.
    /// </summary>
    public const ushort Vote = 5;

    /// <summary>
    /// Cancel a poll.
    /// </summary>
    public const ushort CancelPoll = 6;

    /// <summary>
    /// Distribute QU to asset shareholders.
    /// </summary>
    public const ushort DistributeQuToShareholders = 7;

    /// <summary>
    /// Burn QU for a contract's fee reserve.
    /// </summary>
    public const ushort BurnQubicForContract = 8;
}
