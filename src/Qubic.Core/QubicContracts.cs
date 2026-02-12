using Qubic.Core.Entities;

namespace Qubic.Core;

/// <summary>
/// Well-known Qubic contract indices.
/// Based on: https://github.com/qubic/core contract_def.h
/// </summary>
public static class QubicContracts
{
    /// <summary>Contract 0 - Core/system contract.</summary>
    public const int Core = 0;

    /// <summary>QX - Asset exchange contract. (epoch 66)</summary>
    public const int Qx = 1;

    /// <summary>QUOTTERY - Lottery contract. (epoch 72)</summary>
    public const int Quottery = 2;

    /// <summary>RANDOM - Random number generation contract. (epoch 88)</summary>
    public const int Random = 3;

    /// <summary>QUTIL - Utility functions (SendToMany, etc). (epoch 99)</summary>
    public const int Qutil = 4;

    /// <summary>MLM - Machine Learning Mining contract. (epoch 112)</summary>
    public const int Mlm = 5;

    /// <summary>GQMPROP - GQM proposal contract. (epoch 123)</summary>
    public const int Gqmprop = 6;

    /// <summary>SWATCH - Swatch contract. (epoch 123)</summary>
    public const int Swatch = 7;

    /// <summary>CCF - Community Contribution Fund contract. (epoch 127)</summary>
    public const int Ccf = 8;

    /// <summary>QEARN - Earning/staking contract. (epoch 137)</summary>
    public const int Qearn = 9;

    /// <summary>QVAULT - Vault contract. (epoch 138)</summary>
    public const int Qvault = 10;

    /// <summary>MSVAULT - Multi-sig vault contract. (epoch 149)</summary>
    public const int Msvault = 11;

    /// <summary>QBAY - Marketplace contract. (epoch 154)</summary>
    public const int Qbay = 12;

    /// <summary>QSWAP - Swap contract. (epoch 171)</summary>
    public const int Qswap = 13;

    /// <summary>NOST - Nostr contract. (epoch 172)</summary>
    public const int Nost = 14;

    /// <summary>QDRAW - Draw contract. (epoch 179)</summary>
    public const int Qdraw = 15;

    /// <summary>RL - RL contract. (epoch 182)</summary>
    public const int Rl = 16;

    /// <summary>QBOND - Bond contract. (epoch 182)</summary>
    public const int Qbond = 17;

    /// <summary>QIP - Qubic Improvement Proposal contract. (epoch 189)</summary>
    public const int Qip = 18;

    /// <summary>QRAFFLE - Raffle contract. (epoch 192)</summary>
    public const int Qraffle = 19;

    /// <summary>QRWA - Real World Assets contract. (epoch 197)</summary>
    public const int Qrwa = 20;

    /// <summary>QRP - QRP contract. (epoch 199)</summary>
    public const int Qrp = 21;

    /// <summary>QTF - QTF contract. (epoch 199)</summary>
    public const int Qtf = 22;

    /// <summary>QDUEL - Duel contract. (epoch 199)</summary>
    public const int Qduel = 23;

    /// <summary>
    /// Gets the contract public key for a given contract index.
    /// Contract addresses are encoded as: contractIndex in first byte, rest zeros.
    /// </summary>
    public static byte[] GetContractPublicKey(int contractIndex)
    {
        var pubKey = new byte[32];
        pubKey[0] = (byte)contractIndex;
        return pubKey;
    }

    /// <summary>
    /// Gets the contract identity for a given contract index.
    /// </summary>
    public static QubicIdentity GetContractIdentity(int contractIndex)
    {
        return QubicIdentity.FromPublicKey(GetContractPublicKey(contractIndex));
    }
}
