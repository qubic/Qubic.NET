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

    // Single source of truth for contract index → name mapping.
    private static readonly Dictionary<int, string> _contractNames = new()
    {
        [Core] = "Core", [Qx] = "QX", [Quottery] = "Quottery", [Random] = "Random",
        [Qutil] = "QUtil", [Mlm] = "MLM", [Gqmprop] = "GQMProp", [Swatch] = "Swatch",
        [Ccf] = "CCF", [Qearn] = "QEarn", [Qvault] = "QVault", [Msvault] = "MSVault",
        [Qbay] = "QBay", [Qswap] = "QSwap", [Nost] = "Nostromo", [Qdraw] = "QDraw",
        [Rl] = "RL", [Qbond] = "QBond", [Qip] = "QIP", [Qraffle] = "QRaffle",
        [Qrwa] = "QRWA", [Qrp] = "QRP", [Qtf] = "QTF", [Qduel] = "QDuel"
    };

    /// <summary>
    /// Gets the human-readable name for a contract index, or null if unknown.
    /// </summary>
    public static string? GetContractName(int contractIndex) =>
        _contractNames.GetValueOrDefault(contractIndex);

    /// <summary>
    /// Gets formatted contract display string: "NAME (INDEX)" or just "INDEX" if unknown.
    /// </summary>
    public static string FormatContract(int contractIndex)
    {
        var name = GetContractName(contractIndex);
        return name != null ? $"{name} ({contractIndex})" : contractIndex.ToString();
    }

    /// <summary>
    /// Returns all known contract indices and their names (excludes Core).
    /// </summary>
    public static IReadOnlyList<(int Index, string Name)> GetAllContracts() =>
        _contractNames.Where(kv => kv.Key != Core).Select(kv => (kv.Key, kv.Value)).ToList();

    /// <summary>
    /// Contracts that implement TransferShareManagementRights and can thus
    /// transfer management rights away again. Only these should be valid
    /// targets when transferring asset management rights.
    /// </summary>
    private static readonly HashSet<int> _managementRightsContracts = [Qx, Qbay, Qswap, Nost, Qip, Qraffle];

    /// <summary>
    /// Returns true if the contract supports transferring management rights away
    /// (i.e. it has its own TransferShareManagementRights procedure).
    /// </summary>
    public static bool SupportsManagementRightsTransfer(int contractIndex) =>
        _managementRightsContracts.Contains(contractIndex);

    /// <summary>
    /// Returns contracts that are valid targets for TransferShareManagementRights.
    /// </summary>
    public static IReadOnlyList<(int Index, string Name)> GetManagementRightsTargets() =>
        GetAllContracts().Where(c => SupportsManagementRightsTransfer(c.Index)).ToList();

    /// <summary>
    /// Creates the correct TransferShareManagementRightsPayload for the given managing contract.
    /// Each contract has its own payload class with the correct InputType.
    /// </summary>
    public static Payloads.ITransactionPayload CreateManagementRightsPayload(
        int managingContractIndex, Contracts.QubicAsset asset, long shares, uint newContractIndex)
    {
        return managingContractIndex switch
        {
            Qx => new Contracts.Qx.TransferShareManagementRightsPayload
                { Asset = asset, NumberOfShares = shares, NewManagingContractIndex = newContractIndex },
            Qbay => new Contracts.Qbay.TransferShareManagementRightsPayload
                { Asset = asset, NumberOfShares = shares, NewManagingContractIndex = newContractIndex },
            Qswap => new Contracts.Qswap.TransferShareManagementRightsPayload
                { Asset = asset, NumberOfShares = shares, NewManagingContractIndex = newContractIndex },
            Nost => new Contracts.Nost.TransferShareManagementRightsPayload
                { Asset = asset, NumberOfShares = shares, NewManagingContractIndex = newContractIndex },
            Qip => new Contracts.Qip.TransferShareManagementRightsPayload
                { Asset = asset, NumberOfShares = shares, NewManagingContractIndex = newContractIndex },
            Qraffle => new Contracts.Qraffle.TransferShareManagementRightsPayload
                { TokenIssuer = asset.Issuer, TokenName = asset.AssetName, NumberOfShares = shares, NewManagingContractIndex = newContractIndex },
            _ => throw new ArgumentException($"Contract {managingContractIndex} does not support TransferShareManagementRights")
        };
    }

    /// <summary>
    /// Gets the contract public key for a given contract index.
    /// Contract addresses are little-endian encoded: contractIndex occupies bytes 0-1, rest zeros.
    /// Supports indices 0–1023 (MAX_NUMBER_OF_CONTRACTS = 1024 in core).
    /// </summary>
    public static byte[] GetContractPublicKey(int contractIndex)
    {
        var pubKey = new byte[32];
        pubKey[0] = (byte)(contractIndex & 0xFF);
        pubKey[1] = (byte)(contractIndex >> 8);
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
