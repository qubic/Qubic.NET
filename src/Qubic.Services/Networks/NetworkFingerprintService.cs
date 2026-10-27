using System.Buffers.Binary;
using Qubic.Crypto;

namespace Qubic.Services.Networks;

/// <summary>What the wallet proved about the network it is connected to.</summary>
public enum NetworkFingerprintKind
{
    /// <summary>Not checked yet, or the check failed.</summary>
    Unknown,
    /// <summary>The computor list is exactly the 676 public core-lite seeds (AIO / local testnet).</summary>
    CoreLiteDevnet,
    /// <summary>The computor list carries a valid signature of the mainnet arbitrator.</summary>
    ArbitratorSigned,
    /// <summary>Neither of the above (custom testnet, or a list without a valid arbitrator signature).</summary>
    Unrecognized,
}

public sealed record NetworkFingerprint(
    NetworkFingerprintKind Kind,
    uint Epoch,
    string Detail,
    string ProfileId,
    DateTime CheckedUtc);

/// <summary>
/// Proves which network the active profile points at, without trusting any self-reported
/// chain id (Bob's <c>qubic_chainId</c> returns "qubic:mainnet" even on AIO):
/// <list type="bullet">
/// <item>core-lite devnet: the epoch's computor list equals the identities of the public core-lite seeds;</item>
/// <item>arbitrator-signed: the list's signature verifies against the mainnet arbitrator key.</item>
/// </list>
/// </summary>
public sealed class NetworkFingerprintService
{
    /// <summary>Mainnet arbitrator identity (also compiled into core-lite, which cannot sign with it).</summary>
    public const string ArbitratorIdentity = "AFZPUAIYVPNUYGJRQVLUKOPPVLHAZQTGLYAAUUNBXFTVTAMSBKQBLEIEPCVJ";

    private readonly QubicBackendService _backend;
    private readonly NetworkProfileService _profiles;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public NetworkFingerprint? Current { get; private set; }

    private DateTime _lastAttemptUtc = DateTime.MinValue;

    /// <summary>Minimum time between automatic re-checks while the network is unidentified.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Re-checks when the active network has not been identified yet (no result, or the last check
    /// failed), at most every <see cref="RetryInterval"/>. Call it from tick updates so a transient
    /// failure (node busy, connection just switched) does not leave the network unidentified.
    /// </summary>
    public Task RetryIfUnidentifiedAsync()
    {
        var fp = Current;
        var unidentified = fp == null || fp.Kind == NetworkFingerprintKind.Unknown || fp.ProfileId != _profiles.ActiveId;
        if (!unidentified || _lock.CurrentCount == 0 || DateTime.UtcNow - _lastAttemptUtc < RetryInterval)
            return Task.CompletedTask;
        return CheckAsync();
    }

    public event Action? OnChanged;

    public NetworkFingerprintService(QubicBackendService backend, NetworkProfileService profiles)
    {
        _backend = backend;
        _profiles = profiles;
        _profiles.OnActiveProfileChanged += (_, _) => { Current = null; Raise(); };
    }

    /// <summary>
    /// True when the last fingerprint contradicts the active profile's expectation
    /// (e.g. a Mainnet profile that turned out to be a core-lite devnet).
    /// </summary>
    public bool IsMismatch
    {
        get
        {
            var fp = Current;
            var expected = _profiles.Active.ExpectedFingerprint;
            return fp != null && expected != null
                && fp.ProfileId == _profiles.ActiveId
                && fp.Kind != NetworkFingerprintKind.Unknown
                && fp.Kind != expected;
        }
    }

    /// <summary>True if the active network was proven to be a core-lite devnet.</summary>
    public bool IsCoreLiteDevnet => Current is { Kind: NetworkFingerprintKind.CoreLiteDevnet } fp && fp.ProfileId == _profiles.ActiveId;

    public async Task<NetworkFingerprint> CheckAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _lastAttemptUtc = DateTime.UtcNow;
            var profileId = _profiles.ActiveId;
            NetworkFingerprint result;
            try
            {
                var list = await _backend.GetComputorsAsync(null, ct);
                result = Classify(list, profileId);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result = new NetworkFingerprint(NetworkFingerprintKind.Unknown, 0, ex.Message, profileId, DateTime.UtcNow);
            }

            // Discard if the user switched networks while we were checking.
            if (profileId == _profiles.ActiveId)
            {
                Current = result;
                Raise();
            }
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    internal static NetworkFingerprint Classify(ComputorListSnapshot list, string profileId)
    {
        var now = DateTime.UtcNow;
        if (list.Identities.Count == 0)
            return new(NetworkFingerprintKind.Unknown, list.Epoch, "Empty computor list", profileId, now);

        var devIds = CoreLiteDevSeeds.ComputorIdentities;
        if (list.Identities.Count == devIds.Count && list.Identities.All(devIds.Contains))
            return new(NetworkFingerprintKind.CoreLiteDevnet, list.Epoch,
                "Computor list equals the 676 public core-lite seeds", profileId, now);

        if (VerifyArbitratorSignature(list))
            return new(NetworkFingerprintKind.ArbitratorSigned, list.Epoch,
                "Computor list signed by the mainnet arbitrator", profileId, now);

        return new(NetworkFingerprintKind.Unrecognized, list.Epoch,
            "Computor list is neither the public core-lite set nor arbitrator-signed", profileId, now);
    }

    /// <summary>
    /// Verifies the arbitrator signature over the packed Computors struct
    /// (uint16 epoch + 676 public keys), as broadcast by the arbitrator.
    /// </summary>
    internal static bool VerifyArbitratorSignature(ComputorListSnapshot list)
    {
        if (string.IsNullOrEmpty(list.SignatureHex) || list.SignatureHex.Length != 128) return false;
        if (list.Identities.Count != QubicConstantsCount) return false;
        try
        {
            IQubicCrypt crypt = new QubicCrypt();
            var message = new byte[2 + 32 * QubicConstantsCount];
            BinaryPrimitives.WriteUInt16LittleEndian(message, (ushort)list.Epoch);
            for (int i = 0; i < list.Identities.Count; i++)
                crypt.GetPublicKeyFromIdentity(list.Identities[i]).CopyTo(message, 2 + 32 * i);
            var signature = Convert.FromHexString(list.SignatureHex);
            var arbitrator = crypt.GetPublicKeyFromIdentity(ArbitratorIdentity);
            return crypt.Verify(arbitrator, message, signature);
        }
        catch
        {
            return false;
        }
    }

    private const int QubicConstantsCount = 676;

    private void Raise()
    {
        try { OnChanged?.Invoke(); } catch { /* subscriber errors are not ours */ }
    }
}
