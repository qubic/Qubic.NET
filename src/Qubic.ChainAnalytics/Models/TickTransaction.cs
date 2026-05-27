namespace Qubic.ChainAnalytics.Models;

/// <summary>
/// A transaction parsed from a tick's raw wire bytes, with the digest/hash
/// computed locally for chain verification.
/// </summary>
/// <param name="SourceIdentity">Human-readable 60-char source identity.</param>
/// <param name="DestinationIdentity">Human-readable 60-char destination identity.</param>
/// <param name="SourcePublicKey">Raw 32-byte source public key.</param>
/// <param name="DestinationPublicKey">Raw 32-byte destination public key.</param>
/// <param name="Amount">Transfer amount in QU.</param>
/// <param name="Tick">Target tick the tx was scheduled for.</param>
/// <param name="InputType">Input type discriminator (0 = transfer/system, ≠0 = contract procedure).</param>
/// <param name="InputSize">Payload length in bytes.</param>
/// <param name="Payload">Raw payload bytes (empty for plain transfers).</param>
/// <param name="Signature">64-byte Schnorrq signature.</param>
/// <param name="Digest">32-byte K12 digest of the full raw transaction bytes.</param>
/// <param name="Hash">Human-readable form of <paramref name="Digest"/> — the canonical tx hash.</param>
/// <param name="RawBytes">Original wire bytes (kept for re-broadcast or external verification).</param>
public sealed record TickTransaction(
    string SourceIdentity,
    string DestinationIdentity,
    byte[] SourcePublicKey,
    byte[] DestinationPublicKey,
    long Amount,
    uint Tick,
    ushort InputType,
    ushort InputSize,
    byte[] Payload,
    byte[] Signature,
    byte[] Digest,
    string Hash,
    byte[] RawBytes)
{
    /// <summary>
    /// True when the destination is the zero address (all 32 bytes zero). By Qubic
    /// convention these are system messages — protocol-level signalling rather than
    /// user transfers or contract calls. Note: this is a subset of
    /// <see cref="IsContractDestination"/> (zero is contract-index-0, which is reserved).
    /// </summary>
    public bool IsSystemMessage => IsAllZero(DestinationPublicKey);

    /// <summary>
    /// True when the destination encodes a contract address: bytes 2..31 are zero,
    /// matching <c>isPublicKeyOfContract</c> in qubic-core. This includes the zero
    /// address (contract 0). To filter out system messages, combine with
    /// <c>!IsSystemMessage</c>.
    /// </summary>
    public bool IsContractDestination =>
        DestinationPublicKey.AsSpan(2).IndexOfAnyExcept((byte)0) < 0;

    /// <summary>
    /// Contract index encoded in the destination address (bytes 0–1, little-endian),
    /// or <c>null</c> when the destination isn't a contract address. Index 0 is the
    /// reserved system address — see <see cref="IsSystemMessage"/>.
    /// </summary>
    public ushort? DestinationContractIndex =>
        IsContractDestination ? (ushort)(DestinationPublicKey[0] | (DestinationPublicKey[1] << 8)) : null;

    /// <summary>
    /// Categorises the transaction by destination shape — useful for analytics filters.
    /// </summary>
    public TickTransactionKind Kind => this switch
    {
        { IsSystemMessage: true } => TickTransactionKind.SystemMessage,
        { IsContractDestination: true } => TickTransactionKind.ContractCall,
        _ => TickTransactionKind.UserTransfer,
    };

    private static bool IsAllZero(byte[] bytes)
    {
        foreach (var b in bytes)
            if (b != 0) return false;
        return true;
    }
}

public enum TickTransactionKind
{
    /// <summary>Plain QU transfer between user identities.</summary>
    UserTransfer,
    /// <summary>Call to a deployed smart contract (destination is the contract address).</summary>
    ContractCall,
    /// <summary>System message — destination is the zero address.</summary>
    SystemMessage,
}
