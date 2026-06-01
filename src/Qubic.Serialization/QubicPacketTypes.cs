namespace Qubic.Serialization;

/// <summary>
/// Qubic network packet type identifiers.
/// </summary>
public static class QubicPacketTypes
{
    // Request types
    public const byte ExchangePublicPeers = 0;
    public const byte BroadcastMessage = 1;
    public const byte BroadcastComputors = 2;
    public const byte BroadcastTick = 3;
    public const byte BroadcastFutureTickData = 8;
    public const byte RequestComputors = 11;
    public const byte RequestQuorumTick = 14;
    public const byte RequestTickData = 16;
    public const byte BroadcastTransaction = 24;
    public const byte RequestCurrentTickInfo = 27;
    public const byte RequestTickTransactions = 29;
    public const byte RequestEntity = 31;
    public const byte RespondEntity = 32;
    public const byte RequestContractIPO = 33;
    public const byte RespondContractIPO = 34;
    public const byte RequestIssuedAssets = 36;
    public const byte RespondIssuedAssets = 37;
    public const byte RequestOwnedAssets = 38;
    public const byte RespondOwnedAssets = 39;
    public const byte RequestPossessedAssets = 40;
    public const byte RespondPossessedAssets = 41;
    public const byte RequestContractFunction = 42;
    public const byte RespondContractFunction = 43;
    public const byte RequestLog = 44;
    public const byte RespondLog = 45;
    public const byte RequestSystemInfo = 46;
    public const byte RespondSystemInfo = 47;
    public const byte RequestLogIdRangeFromTx = 48;
    public const byte RespondLogIdRangeFromTx = 49;
    public const byte RequestAllLogIdRangesFromTick = 50;
    public const byte RespondAllLogIdRangesFromTick = 51;

    // Oracle
    public const byte RequestOracleData = 66;
    public const byte RespondOracleData = 67;

    // Special
    public const byte EndResponse = 35;
    public const byte SpecialCommand = 255;

    // Response types
    public const byte RespondCurrentTickInfo = 28;
}
