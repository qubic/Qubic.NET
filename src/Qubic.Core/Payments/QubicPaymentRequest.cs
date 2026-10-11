using System.Globalization;
using System.Text;
using Qubic.Crypto;

namespace Qubic.Core.Payments;

/// <summary>
/// A payment request encoded as a URI, for QR codes and links:
/// <code>qubic:&lt;IDENTITY&gt;?amount=1000&amp;asset=NAME.ISSUER&amp;tick=123&amp;inputType=5&amp;input=0a0b&amp;network=testnet&amp;message=Invoice%2042</code>
/// All parameters are optional. A bare 60-character identity also parses (no parameters).
/// <list type="bullet">
/// <item><c>amount</c>: QU (or asset shares when <c>asset</c> is set), integer.</item>
/// <item><c>asset</c>: asset to transfer, <c>NAME.ISSUER</c>.</item>
/// <item><c>tick</c>: requested target tick.</item>
/// <item><c>inputType</c>/<c>input</c>: contract call input type and hex payload (destination is a contract).</item>
/// <item><c>network</c>: <c>mainnet</c> (default) or <c>testnet</c>; wallets warn on mismatch.</item>
/// <item><c>label</c>/<c>message</c>: free text shown to the payer.</item>
/// </list>
/// </summary>
public sealed record QubicPaymentRequest
{
    public const string Scheme = "qubic";

    public required string Destination { get; init; }
    public long? Amount { get; init; }
    public string? AssetName { get; init; }
    public string? AssetIssuer { get; init; }
    public uint? Tick { get; init; }
    public ushort? InputType { get; init; }
    public byte[]? Input { get; init; }

    /// <summary>"mainnet" or "testnet"; null means mainnet.</summary>
    public string? Network { get; init; }
    public string? Label { get; init; }
    public string? Message { get; init; }

    public bool IsAssetTransfer => AssetName != null;
    public bool IsContractCall => InputType is > 0;

    /// <summary>CAIP-2 chain id this request is meant for.</summary>
    public string ChainId => Network == "testnet" ? "qubic:testnet" : "qubic:mainnet";

    public string ToUri()
    {
        var q = new List<string>();
        if (Amount is { } a) q.Add("amount=" + a.ToString(CultureInfo.InvariantCulture));
        if (AssetName != null) q.Add("asset=" + Escape(AssetName + "." + AssetIssuer));
        if (Tick is { } t) q.Add("tick=" + t.ToString(CultureInfo.InvariantCulture));
        if (InputType is { } it) q.Add("inputType=" + it.ToString(CultureInfo.InvariantCulture));
        if (Input is { Length: > 0 }) q.Add("input=" + Convert.ToHexString(Input).ToLowerInvariant());
        if (!string.IsNullOrEmpty(Network) && Network != "mainnet") q.Add("network=" + Escape(Network));
        if (!string.IsNullOrEmpty(Label)) q.Add("label=" + Escape(Label));
        if (!string.IsNullOrEmpty(Message)) q.Add("message=" + Escape(Message));
        var sb = new StringBuilder(Scheme).Append(':').Append(Destination);
        if (q.Count > 0) sb.Append('?').Append(string.Join('&', q));
        return sb.ToString();
    }

    public override string ToString() => ToUri();

    public static bool TryParse(string? text, out QubicPaymentRequest? request, out string? error)
    {
        request = null;
        error = null;
        try
        {
            request = Parse(text ?? "");
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Parses a <c>qubic:</c> URI or a bare identity. Throws <see cref="FormatException"/>.</summary>
    public static QubicPaymentRequest Parse(string text)
    {
        text = text.Trim();
        if (text.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase))
            text = text[(Scheme.Length + 1)..].TrimStart('/');

        var qIndex = text.IndexOf('?');
        var dest = (qIndex >= 0 ? text[..qIndex] : text).Trim().ToUpperInvariant();
        if (dest.Length != 60 || !((IQubicCrypt)new QubicCrypt()).VerifyIdentityChecksum(dest))
            throw new FormatException("Destination is not a valid Qubic identity.");

        var p = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (qIndex >= 0)
        {
            foreach (var part in text[(qIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                var key = Uri.UnescapeDataString(eq >= 0 ? part[..eq] : part);
                var value = eq >= 0 ? Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' ')) : "";
                p[key] = value;
            }
        }

        string? assetName = null, assetIssuer = null;
        if (p.TryGetValue("asset", out var asset) && asset.Length > 0)
        {
            var dot = asset.IndexOf('.');
            if (dot <= 0) throw new FormatException("asset must be NAME.ISSUER.");
            assetName = asset[..dot].ToUpperInvariant();
            assetIssuer = asset[(dot + 1)..].ToUpperInvariant();
            if (assetName.Length > 7) throw new FormatException("Asset name is at most 7 characters.");
            if (assetIssuer.Length != 60 || !((IQubicCrypt)new QubicCrypt()).VerifyIdentityChecksum(assetIssuer))
                throw new FormatException("Asset issuer is not a valid Qubic identity.");
        }

        var network = p.GetValueOrDefault("network")?.ToLowerInvariant();
        if (network is not (null or "" or "mainnet" or "testnet"))
            throw new FormatException($"Unknown network '{network}'.");

        return new QubicPaymentRequest
        {
            Destination = dest,
            Amount = ParseOptional<long>(p, "amount", long.TryParse, mustBePositive: true),
            AssetName = assetName,
            AssetIssuer = assetIssuer,
            Tick = ParseOptional<uint>(p, "tick", uint.TryParse),
            InputType = ParseOptional<ushort>(p, "inputType", ushort.TryParse),
            Input = p.TryGetValue("input", out var hex) && hex.Length > 0 ? ParseHex(hex) : null,
            Network = string.IsNullOrEmpty(network) ? null : network,
            Label = p.GetValueOrDefault("label"),
            Message = p.GetValueOrDefault("message"),
        };
    }

    private delegate bool TryParser<T>(string s, NumberStyles style, IFormatProvider provider, out T value);

    private static T? ParseOptional<T>(Dictionary<string, string> p, string key, TryParser<T> parser, bool mustBePositive = false)
        where T : struct, IComparable<T>
    {
        if (!p.TryGetValue(key, out var raw) || raw.Length == 0) return null;
        if (!parser(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var v))
            throw new FormatException($"{key} must be a non-negative integer.");
        if (mustBePositive && v.CompareTo(default) <= 0)
            throw new FormatException($"{key} must be greater than zero.");
        return v;
    }

    private static byte[] ParseHex(string hex)
    {
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        try { return Convert.FromHexString(hex); }
        catch (FormatException) { throw new FormatException("input must be hex."); }
    }

    private static string Escape(string s) => Uri.EscapeDataString(s);
}
