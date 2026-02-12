namespace Qubic.Core;

/// <summary>
/// QX contract procedure IDs for asset operations.
/// </summary>
public static class QxProcedures
{
    /// <summary>
    /// Issue a new asset.
    /// </summary>
    public const ushort IssueAsset = 1;

    /// <summary>
    /// Transfer asset ownership and possession.
    /// </summary>
    public const ushort TransferShareOwnershipAndPossession = 2;

    /// <summary>
    /// Add an ask (sell) order.
    /// </summary>
    public const ushort AddToAskOrder = 5;

    /// <summary>
    /// Add a bid (buy) order.
    /// </summary>
    public const ushort AddToBidOrder = 6;

    /// <summary>
    /// Remove an ask order.
    /// </summary>
    public const ushort RemoveFromAskOrder = 7;

    /// <summary>
    /// Remove a bid order.
    /// </summary>
    public const ushort RemoveFromBidOrder = 8;

    /// <summary>
    /// Transfer asset management rights to another contract.
    /// </summary>
    public const ushort TransferShareManagementRights = 9;
}
