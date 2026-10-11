using System.Buffers.Binary;
using System.Text;
using Qubic.Core.Entities;
using Qubic.Crypto;
using Qubic.Network;
using Qubic.Rpc.Models;

namespace Qubic.Services.Networks;

/// <summary>
/// Maps raw RespondIssued/Owned/PossessedAssets payloads from a node into the
/// RPC models the wallet pages already use, so the Direct method can serve asset listings.
/// Layout: 48-byte AssetRecord(s), then uint32 tick, uint32 universeIndex, siblings.
/// </summary>
internal static class DirectAssetMapper
{
    private const int RecordSize = 48;
    private static readonly QubicCrypt Crypt = new();

    public static async Task<IReadOnlyList<IssuedAssetInfo>> GetIssuedAssetsAsync(this QubicNodeClient node, QubicIdentity identity, CancellationToken ct)
    {
        var raw = await node.GetIssuedAssetsRawAsync(identity, ct);
        return raw.Where(p => p.Length >= RecordSize + 8).Select(p => new IssuedAssetInfo
        {
            Data = ReadIssuance(p.AsSpan(0, RecordSize)),
            Info = ReadMeta(p.AsSpan(RecordSize))
        }).ToList();
    }

    public static async Task<IReadOnlyList<OwnedAssetInfo>> GetOwnedAssetsAsync(this QubicNodeClient node, QubicIdentity identity, CancellationToken ct)
    {
        var raw = await node.GetOwnedAssetsRawAsync(identity, ct);
        return raw.Where(p => p.Length >= 2 * RecordSize + 8).Select(p => new OwnedAssetInfo
        {
            Data = ReadOwnership(p.AsSpan(0, RecordSize), ReadIssuance(p.AsSpan(RecordSize, RecordSize))),
            Info = ReadMeta(p.AsSpan(2 * RecordSize))
        }).ToList();
    }

    public static async Task<IReadOnlyList<PossessedAssetInfo>> GetPossessedAssetsAsync(this QubicNodeClient node, QubicIdentity identity, CancellationToken ct)
    {
        var raw = await node.GetPossessedAssetsRawAsync(identity, ct);
        return raw.Where(p => p.Length >= 3 * RecordSize + 8).Select(p =>
        {
            var issuance = ReadIssuance(p.AsSpan(2 * RecordSize, RecordSize));
            var ownership = ReadOwnership(p.AsSpan(RecordSize, RecordSize), issuance);
            var possession = p.AsSpan(0, RecordSize);
            return new PossessedAssetInfo
            {
                Data = new PossessedAssetData
                {
                    PossessorIdentity = IdentityOf(possession[..32]),
                    Type = possession[32],
                    ManagingContractIndex = BinaryPrimitives.ReadUInt16LittleEndian(possession[34..]),
                    OwnershipIndex = BinaryPrimitives.ReadUInt32LittleEndian(possession[36..]),
                    NumberOfUnits = BinaryPrimitives.ReadInt64LittleEndian(possession[40..]).ToString(),
                    OwnedAsset = ownership
                },
                Info = ReadMeta(p.AsSpan(3 * RecordSize))
            };
        }).ToList();
    }

    internal static IssuedAssetData ReadIssuance(ReadOnlySpan<byte> r) => new()
    {
        IssuerIdentity = IdentityOf(r[..32]),
        Type = r[32],
        Name = ReadName(r.Slice(33, 7)),
        NumberOfDecimalPlaces = (sbyte)r[40],
        UnitOfMeasurement = r.Slice(41, 7).ToArray().Select(b => (int)(sbyte)b).ToArray()
    };

    internal static OwnedAssetData ReadOwnership(ReadOnlySpan<byte> r, IssuedAssetData issuance) => new()
    {
        OwnerIdentity = IdentityOf(r[..32]),
        Type = r[32],
        ManagingContractIndex = BinaryPrimitives.ReadUInt16LittleEndian(r[34..]),
        IssuanceIndex = BinaryPrimitives.ReadUInt32LittleEndian(r[36..]),
        NumberOfUnits = BinaryPrimitives.ReadInt64LittleEndian(r[40..]).ToString(),
        IssuedAsset = issuance
    };

    private static AssetInfoMeta ReadMeta(ReadOnlySpan<byte> tail) => new()
    {
        Tick = BinaryPrimitives.ReadUInt32LittleEndian(tail),
        UniverseIndex = BinaryPrimitives.ReadUInt32LittleEndian(tail[4..])
    };

    private static string IdentityOf(ReadOnlySpan<byte> publicKey) => Crypt.GetIdentityFromPublicKey(publicKey.ToArray());

    private static string ReadName(ReadOnlySpan<byte> name)
    {
        var end = name.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end >= 0 ? name[..end] : name);
    }
}
