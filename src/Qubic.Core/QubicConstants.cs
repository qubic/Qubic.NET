namespace Qubic.Core;

/// <summary>
/// Qubic protocol constants derived from qubic/core.
/// Based on: https://github.com/qubic/core v1.278.0
/// </summary>
public static class QubicConstants
{
    /// <summary>
    /// The Qubic core release version these models are based on.
    /// </summary>
    public const string QubicCoreVersion = "1.278.0";


    #region Network Configuration

    /// <summary>
    /// Number of computors in the network.
    /// </summary>
    public const int NumberOfComputors = 676;

    /// <summary>
    /// Quorum required for consensus (2/3 + 1).
    /// </summary>
    public const int Quorum = NumberOfComputors * 2 / 3 + 1; // 451

    /// <summary>
    /// Number of peers exchanged in peer discovery.
    /// </summary>
    public const int NumberOfExchangedPeers = 4;

    /// <summary>
    /// Default node port.
    /// </summary>
    public const int DefaultNodePort = 21841;

    /// <summary>
    /// Target tick duration in milliseconds.
    /// </summary>
    public const int TargetTickDurationMs = 1000;

    #endregion

    #region Transaction Limits

    /// <summary>
    /// Maximum transactions per tick.
    /// </summary>
    public const int MaxTransactionsPerTick = 1024;

    /// <summary>
    /// Maximum number of contracts.
    /// </summary>
    public const int MaxNumberOfContracts = 1024;

    /// <summary>
    /// Maximum input (payload) size in bytes.
    /// </summary>
    public const int MaxInputSize = 1024;

    /// <summary>
    /// Maximum number of mining solutions.
    /// </summary>
    public const int MaxNumberOfSolutions = 65536;

    #endregion

    #region Cryptographic Sizes

    /// <summary>
    /// Signature size in bytes.
    /// </summary>
    public const int SignatureSize = 64;

    /// <summary>
    /// Public key size in bytes.
    /// </summary>
    public const int PublicKeySize = 32;

    /// <summary>
    /// Digest (hash) size in bytes.
    /// </summary>
    public const int DigestSize = 32;

    #endregion

    #region Structure Sizes

    /// <summary>
    /// Transaction header size (without payload and signature).
    /// </summary>
    public const int TransactionHeaderSize = 80;

    /// <summary>
    /// Entity record size in bytes.
    /// </summary>
    public const int EntityRecordSize = 64;

    /// <summary>
    /// Asset record size in bytes.
    /// </summary>
    public const int AssetRecordSize = 48;

    /// <summary>
    /// Tick structure size in bytes.
    /// </summary>
    public const int TickSize = 344;

    /// <summary>
    /// Packet header size in bytes.
    /// </summary>
    public const int PacketHeaderSize = 8;

    #endregion

    #region Identity Format

    /// <summary>
    /// Identity string length (56 chars + 4 checksum).
    /// </summary>
    public const int IdentityLength = 60;

    /// <summary>
    /// Identity string length without checksum.
    /// </summary>
    public const int IdentityLengthWithoutChecksum = 56;

    /// <summary>
    /// Seed length (55 lowercase characters a-z).
    /// </summary>
    public const int SeedLength = 55;

    #endregion

    #region Economic Constants

    /// <summary>
    /// Base issuance rate (1 trillion QU).
    /// </summary>
    public const long IssuanceRate = 1_000_000_000_000L;

    /// <summary>
    /// Maximum amount per transaction (1 quadrillion QU).
    /// </summary>
    public const long MaxAmount = IssuanceRate * 1000L;

    /// <summary>
    /// Maximum total supply (200 trillion QU).
    /// </summary>
    public const long MaxSupply = IssuanceRate * 200L;

    /// <summary>
    /// Solution security deposit required for mining.
    /// </summary>
    public const long SolutionSecurityDeposit = 1_000_000L;

    #endregion

    #region Spectrum & Universe

    /// <summary>
    /// Spectrum (accounts) Merkle tree depth.
    /// </summary>
    public const int SpectrumDepth = 24;

    /// <summary>
    /// Spectrum capacity (2^24 = 16,777,216 entities).
    /// </summary>
    public const int SpectrumCapacity = 1 << SpectrumDepth;

    /// <summary>
    /// Assets (universe) Merkle tree depth.
    /// </summary>
    public const int AssetsDepth = 24;

    /// <summary>
    /// Assets capacity (2^24 = 16,777,216 asset records).
    /// </summary>
    public const int AssetsCapacity = 1 << AssetsDepth;

    /// <summary>
    /// Indicates no asset index (0xFFFFFFFF).
    /// </summary>
    public const uint NoAssetIndex = 0xFFFFFFFF;

    #endregion

    #region Well-Known Identities

    /// <summary>
    /// Arbitrator identity (handles disputes).
    /// </summary>
    public const string ArbitratorIdentity = "AFZPUAIYVPNUYGJRQVLUKOPPVLHAZQTGLYAAUUNBXFTVTAMSBKQBLEIEPCVJ";

    /// <summary>
    /// Dispatcher identity (special operations).
    /// </summary>
    public const string DispatcherIdentity = "XPXYKFLGSWRHRGAUKWFWVXCDVEYAPCPCNUTMUDWFGDYQCWZNJMWFZEEGCFFO";

    #endregion
}
