namespace Qubic.Core;

/// <summary>
/// Network message types for the Qubic protocol.
/// Based on: https://github.com/qubic/core network_message_type.h
/// </summary>
public static class NetworkMessageTypes
{
    public const byte ExchangePublicPeers = 0;
    public const byte BroadcastMessage = 1;
    public const byte BroadcastComputors = 2;
    public const byte BroadcastTick = 3;
    public const byte BroadcastFutureTickData = 8;
    public const byte RequestComputors = 11;
    public const byte RequestQuorumTick = 14;
    public const byte RequestTickData = 16;
    public const byte BroadcastTransaction = 24;
    public const byte RequestTransactionInfo = 26;
    public const byte RequestCurrentTickInfo = 27;
    public const byte RespondCurrentTickInfo = 28;
    public const byte RequestTickTransactions = 29;
    public const byte RequestEntity = 31;
    public const byte RespondEntity = 32;
    public const byte RequestContractIpo = 33;
    public const byte RespondContractIpo = 34;
    public const byte EndResponse = 35;
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
    public const byte RequestAllLogIdRangesFromTx = 50;
    public const byte RespondAllLogIdRangesFromTx = 51;
    public const byte RequestAssets = 52;
    public const byte RespondAssets = 53;
    public const byte TryAgain = 54;
    public const byte RequestPruningLog = 56;
    public const byte RespondPruningLog = 57;
    public const byte RequestLogStateDigest = 58;
    public const byte RespondLogStateDigest = 59;
    public const byte RequestCustomMiningData = 60;
    public const byte RespondCustomMiningData = 61;
    public const byte RequestCustomMiningSolutionVerification = 62;
    public const byte RespondCustomMiningSolutionVerification = 63;
    public const byte RequestActiveIpos = 64;
    public const byte RespondActiveIpo = 65;
    public const byte RequestOracleData = 66;
    public const byte RespondOracleData = 67;
    public const byte OracleMachineQuery = 190;
    public const byte OracleMachineReply = 191;
    public const byte RequestTxStatus = 201;
    public const byte RespondTxStatus = 202;
    public const byte SpecialCommand = 255;
}
