using System.Text;
using Qubic.Crypto;

namespace Qubic.Core.Auth;

/// <summary>Result of a WalletConnect-style message signature (all base64, as the official Qubic wallet returns them).</summary>
public sealed record PrefixedSignature(byte[] Signature, byte[] SignedData, byte[] Digest)
{
    public string SignatureBase64 => Convert.ToBase64String(Signature);
    public string SignedDataBase64 => Convert.ToBase64String(SignedData);
    public string DigestBase64 => Convert.ToBase64String(Digest);
}

/// <summary>
/// The two message-signing conventions in use:
/// <list type="bullet">
/// <item><b>Raw</b> (wallet sign/verify, web wallet, Qubic Toolkit): SchnorrQ over the message bytes, no prefix.</item>
/// <item><b>WalletConnect</b> (<c>qubic_sign</c>, official mobile wallet): the UTF-8 text is prefixed with
/// <c>"Qubic Signed Message:\n"</c>, K12-hashed and the digest signed; <c>signedData</c> = message ‖ signature and
/// <c>digest</c> = K12(signedData). The prefix stops dApps from tricking users into signing transaction bytes.</item>
/// </list>
/// </summary>
public static class QubicMessageSigning
{
    public const string WalletConnectPrefix = "Qubic Signed Message:\n";

    private static readonly IQubicCrypt Crypt = new QubicCrypt();

    public static byte[] SignRaw(string seed, byte[] message) => Crypt.SignRaw(seed, message);

    public static bool VerifyRaw(string identity, byte[] message, byte[] signature)
    {
        try { return Crypt.VerifyRaw(Crypt.GetPublicKeyFromIdentity(identity), message, signature); }
        catch { return false; }
    }

    /// <summary>Signs <paramref name="text"/> the way the official wallet answers WalletConnect <c>qubic_sign</c>.</summary>
    public static PrefixedSignature SignWalletConnect(string seed, string text)
    {
        var data = Encoding.UTF8.GetBytes(WalletConnectPrefix + text);
        var signature = Crypt.Sign(seed, data); // SchnorrQ over K12(data)
        var signedData = new byte[data.Length + signature.Length];
        data.CopyTo(signedData, 0);
        signature.CopyTo(signedData, data.Length);
        return new PrefixedSignature(signature, signedData, Crypt.KangarooTwelve(signedData));
    }

    /// <summary>Verifies a WalletConnect-style (<c>qubic_sign</c>) signature over <paramref name="text"/>.</summary>
    public static bool VerifyWalletConnect(string identity, string text, byte[] signature)
    {
        try
        {
            var data = Encoding.UTF8.GetBytes(WalletConnectPrefix + text);
            return Crypt.Verify(Crypt.GetPublicKeyFromIdentity(identity), data, signature);
        }
        catch { return false; }
    }
}
