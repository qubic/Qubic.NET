namespace Qubic.Core;

/// <summary>
/// Input type constants for core/system transactions sent to the null (burn) address.
/// When a transaction's destination is the zero public key, the inputType field
/// determines which protocol handler processes it.
/// These are distinct from contract-level inputTypes which identify procedures within a smart contract.
/// Based on: https://github.com/qubic/core qubic.cpp processTransaction()
/// </summary>
public static class CoreTransactionInputTypes
{
    #region Protocol Transactions (max priority in mempool)

    /// <summary>
    /// Vote counter data from tick leader.
    /// Input: 848 bytes packed votes (10-bit × 676) + 32 bytes dataLock.
    /// Source: vote_counter.h
    /// </summary>
    public const ushort VoteCounter = 1;

    /// <summary>
    /// Custom mining share counter from computor.
    /// Input: 848 bytes packed scores (10-bit × 676) + 32 bytes dataLock.
    /// Source: mining/mining.h
    /// </summary>
    public const ushort CustomMiningShareCounter = 8;

    /// <summary>
    /// Execution fee report from computor.
    /// Input: 4 bytes phaseNumber + 4 bytes numEntries + contractIndices[] + alignment + executionFees[] + 32 bytes dataLock.
    /// Source: network_messages/execution_fees.h
    /// </summary>
    public const ushort ExecutionFeeReport = 9;

    #endregion

    #region Mining

    /// <summary>
    /// Mining solution submission. Requires <see cref="QubicConstants.SolutionSecurityDeposit"/> as amount.
    /// Input: 32 bytes miningSeed + 32 bytes nonce.
    /// Source: mining/mining.h
    /// </summary>
    public const ushort MiningSolution = 2;

    #endregion

    #region File Storage (stubs — not yet implemented in core)

    /// <summary>
    /// File header for distributed file storage.
    /// Input: 8 bytes fileSize + 8 bytes numberOfFragments + 8 bytes fileFormat.
    /// Source: files/files.h
    /// </summary>
    public const ushort FileHeader = 3;

    /// <summary>
    /// File fragment for distributed file storage.
    /// Input: 8 bytes fragmentIndex + 32 bytes prevFileFragmentTransactionDigest + variable payload.
    /// Source: files/files.h
    /// </summary>
    public const ushort FileFragment = 4;

    /// <summary>
    /// File trailer for distributed file storage.
    /// Input: 8 bytes fileSize + 8 bytes numberOfFragments + 8 bytes fileFormat + 32 bytes lastFileFragmentTransactionDigest.
    /// Source: files/files.h
    /// </summary>
    public const ushort FileTrailer = 5;

    #endregion

    #region Oracle

    /// <summary>
    /// Oracle reply commit from oracle provider.
    /// Input: n × (8 bytes queryId + 32 bytes replyDigest + 32 bytes replyKnowledgeProof).
    /// Source: oracle_core/oracle_transactions.h
    /// </summary>
    public const ushort OracleReplyCommit = 6;

    /// <summary>
    /// Oracle reply reveal from oracle provider.
    /// Input: 8 bytes queryId + variable reply payload.
    /// Source: oracle_core/oracle_transactions.h
    /// </summary>
    public const ushort OracleReplyReveal = 7;

    /// <summary>
    /// Oracle user query.
    /// Input: 4 bytes oracleInterfaceIndex + 4 bytes timeoutMilliseconds + variable query payload.
    /// Source: oracle_core/oracle_transactions.h
    /// </summary>
    public const ushort OracleUserQuery = 10;

    #endregion

    #region Helpers

    /// <summary>
    /// Size in bytes of the packed 10-bit array used by vote counter and mining shares.
    /// 676 computors × 10 bits = 6760 bits → 845 bytes → rounded to 848 for alignment.
    /// </summary>
    public const int PackedComputorDataSize = 848;

    /// <summary>
    /// Total input size for vote counter and custom mining share counter transactions.
    /// </summary>
    public const int PackedComputorInputSize = PackedComputorDataSize + QubicConstants.DigestSize; // 880

    /// <summary>
    /// Returns the minimum input data size (excluding signature) for a given input type.
    /// </summary>
    public static int GetMinInputSize(ushort inputType) => inputType switch
    {
        VoteCounter => PackedComputorInputSize,      // 880
        MiningSolution => 64,                         // 32 + 32
        FileHeader => 24,                             // 8 + 8 + 8
        FileFragment => 40,                           // 8 + 32
        FileTrailer => 56,                            // 8 + 8 + 8 + 32
        OracleReplyCommit => 72,                      // 8 + 32 + 32
        OracleReplyReveal => 8,                       // 8
        CustomMiningShareCounter => PackedComputorInputSize, // 880
        ExecutionFeeReport => 8,                      // 4 + 4
        OracleUserQuery => 8,                         // 4 + 4
        _ => 0
    };

    /// <summary>
    /// Returns a short identifier string for the input type.
    /// </summary>
    public static string GetName(ushort inputType) => inputType switch
    {
        VoteCounter => "VOTE_COUNTER",
        MiningSolution => "MINING_SOLUTION",
        FileHeader => "FILE_HEADER",
        FileFragment => "FILE_FRAGMENT",
        FileTrailer => "FILE_TRAILER",
        OracleReplyCommit => "ORACLE_REPLY_COMMIT",
        OracleReplyReveal => "ORACLE_REPLY_REVEAL",
        CustomMiningShareCounter => "CUSTOM_MINING_SHARE_COUNTER",
        ExecutionFeeReport => "EXECUTION_FEE_REPORT",
        OracleUserQuery => "ORACLE_USER_QUERY",
        _ => $"UNKNOWN_{inputType}"
    };

    /// <summary>
    /// Returns a human-readable display name for the input type.
    /// </summary>
    public static string GetDisplayName(ushort inputType) => inputType switch
    {
        VoteCounter => "Vote Counter",
        MiningSolution => "Mining Solution",
        FileHeader => "File Header",
        FileFragment => "File Fragment",
        FileTrailer => "File Trailer",
        OracleReplyCommit => "Oracle Reply Commit",
        OracleReplyReveal => "Oracle Reply Reveal",
        CustomMiningShareCounter => "Custom Mining Share Counter",
        ExecutionFeeReport => "Execution Fee Report",
        OracleUserQuery => "Oracle User Query",
        _ => $"Unknown ({inputType})"
    };

    /// <summary>
    /// Returns true if the input type is a known core/system transaction type for the burn address.
    /// </summary>
    public static bool IsKnownType(ushort inputType) =>
        inputType >= VoteCounter && inputType <= OracleUserQuery;

    /// <summary>
    /// Returns true if this is a protocol-level transaction (max priority in mempool).
    /// These require the sender to be the tick leader or a computor.
    /// </summary>
    public static bool IsProtocolTransaction(ushort inputType) => inputType switch
    {
        VoteCounter or CustomMiningShareCounter or ExecutionFeeReport => true,
        _ => false
    };

    #endregion
}
