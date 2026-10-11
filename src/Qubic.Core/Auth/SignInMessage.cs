using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Qubic.Crypto;

namespace Qubic.Core.Auth;

/// <summary>
/// "Sign-In with Qubic": a structured, human-readable login message modelled on EIP-4361.
/// The wallet shows each field separately, and the signature is a plain raw-message
/// signature (<see cref="IQubicCrypt.SignRaw"/>) over the UTF-8 text, so any verifier
/// that already checks Qubic message signatures can check it.
/// <code>
/// example.com wants you to sign in with your Qubic account:
/// IDENTITY60CHARS...
///
/// Optional human-readable statement.
///
/// URI: https://example.com/login
/// Version: 1
/// Network: qubic:mainnet
/// Nonce: 8aZ3kQ9x
/// Issued At: 2026-10-11T12:00:00Z
/// Expiration Time: 2026-10-11T12:10:00Z
/// </code>
/// </summary>
public sealed record SignInMessage
{
    public const string CurrentVersion = "1";
    private const string Header = " wants you to sign in with your Qubic account:";
    private static readonly Regex NonceRegex = new("^[A-Za-z0-9]{8,}$", RegexOptions.Compiled);

    /// <summary>RFC 3986 authority of the site requesting the sign-in (e.g. <c>fund.qubic.li</c>).</summary>
    public required string Domain { get; init; }

    /// <summary>60-character identity signing in.</summary>
    public required string Identity { get; init; }

    public string? Statement { get; init; }

    /// <summary>URI of the resource the user signs in to.</summary>
    public required string Uri { get; init; }

    public string Version { get; init; } = CurrentVersion;

    /// <summary>CAIP-2 chain id: <c>qubic:mainnet</c> or <c>qubic:testnet</c>.</summary>
    public required string Network { get; init; }

    /// <summary>Server-chosen random nonce (≥ 8 alphanumeric characters), prevents replay.</summary>
    public required string Nonce { get; init; }

    public required DateTimeOffset IssuedAt { get; init; }
    public DateTimeOffset? ExpirationTime { get; init; }
    public DateTimeOffset? NotBefore { get; init; }
    public string? RequestId { get; init; }
    public IReadOnlyList<string> Resources { get; init; } = [];

    /// <summary>Renders the canonical text that gets signed.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(Domain).Append(Header).Append('\n');
        sb.Append(Identity).Append('\n');
        sb.Append('\n');
        if (!string.IsNullOrEmpty(Statement))
            sb.Append(Statement).Append('\n').Append('\n');
        sb.Append("URI: ").Append(Uri).Append('\n');
        sb.Append("Version: ").Append(Version).Append('\n');
        sb.Append("Network: ").Append(Network).Append('\n');
        sb.Append("Nonce: ").Append(Nonce).Append('\n');
        sb.Append("Issued At: ").Append(FormatTime(IssuedAt));
        if (ExpirationTime is { } exp) sb.Append('\n').Append("Expiration Time: ").Append(FormatTime(exp));
        if (NotBefore is { } nbf) sb.Append('\n').Append("Not Before: ").Append(FormatTime(nbf));
        if (!string.IsNullOrEmpty(RequestId)) sb.Append('\n').Append("Request ID: ").Append(RequestId);
        if (Resources.Count > 0)
        {
            sb.Append('\n').Append("Resources:");
            foreach (var r in Resources) sb.Append('\n').Append("- ").Append(r);
        }
        return sb.ToString();
    }

    public byte[] ToBytes() => Encoding.UTF8.GetBytes(ToString());

    private static string FormatTime(DateTimeOffset t) =>
        t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>True if <paramref name="text"/> looks like a sign-in message (first line header).</summary>
    public static bool IsSignInMessage(string? text) =>
        text != null && text.Split('\n', 2)[0].EndsWith(Header, StringComparison.Ordinal);

    /// <summary>Parses the canonical text. Throws <see cref="FormatException"/> if malformed.</summary>
    public static SignInMessage Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        int i = 0;
        string Next(string what) => i < lines.Length ? lines[i++] : throw new FormatException($"Missing {what}.");

        var first = Next("header");
        if (!first.EndsWith(Header, StringComparison.Ordinal))
            throw new FormatException("Not a Sign-In with Qubic message.");
        var domain = first[..^Header.Length];
        var identity = Next("identity");
        if (Next("blank line").Length != 0) throw new FormatException("Expected a blank line after the identity.");

        string? statement = null;
        if (i < lines.Length && !lines[i].StartsWith("URI: ", StringComparison.Ordinal))
        {
            statement = lines[i++];
            if (Next("blank line").Length != 0) throw new FormatException("Expected a blank line after the statement.");
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var resources = new List<string>();
        while (i < lines.Length)
        {
            var line = lines[i++];
            if (line == "Resources:")
            {
                while (i < lines.Length && lines[i].StartsWith("- ", StringComparison.Ordinal))
                    resources.Add(lines[i++][2..]);
                continue;
            }
            var sep = line.IndexOf(": ", StringComparison.Ordinal);
            if (sep <= 0) throw new FormatException($"Malformed line: '{line}'.");
            fields[line[..sep]] = line[(sep + 2)..];
        }

        string Req(string key) => fields.TryGetValue(key, out var v) ? v : throw new FormatException($"Missing '{key}'.");
        DateTimeOffset? Opt(string key) => fields.TryGetValue(key, out var v) ? ParseTime(v, key) : null;

        return new SignInMessage
        {
            Domain = domain,
            Identity = identity,
            Statement = statement,
            Uri = Req("URI"),
            Version = Req("Version"),
            Network = Req("Network"),
            Nonce = Req("Nonce"),
            IssuedAt = ParseTime(Req("Issued At"), "Issued At"),
            ExpirationTime = Opt("Expiration Time"),
            NotBefore = Opt("Not Before"),
            RequestId = fields.GetValueOrDefault("Request ID"),
            Resources = resources
        };
    }

    private static DateTimeOffset ParseTime(string value, string field) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t
            : throw new FormatException($"Invalid time in '{field}'.");

    /// <summary>
    /// Problems with this message (empty = OK). <paramref name="expectedDomain"/> / <paramref name="expectedNetwork"/>
    /// are checked when given. Wallets call this before showing the sign-in, servers after verifying.
    /// </summary>
    public IReadOnlyList<string> Validate(DateTimeOffset now, string? expectedDomain = null, string? expectedNetwork = null)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Domain) || Domain.Contains(' ')) problems.Add("Domain is missing or invalid.");
        if (Identity.Length != 60 || !((IQubicCrypt)new QubicCrypt()).VerifyIdentityChecksum(Identity)) problems.Add("Identity is not a valid Qubic identity.");
        if (!System.Uri.TryCreate(Uri, UriKind.Absolute, out _)) problems.Add("URI is not absolute.");
        if (Version != CurrentVersion) problems.Add($"Unsupported version '{Version}'.");
        if (Network is not ("qubic:mainnet" or "qubic:testnet")) problems.Add($"Unknown network '{Network}'.");
        if (!NonceRegex.IsMatch(Nonce)) problems.Add("Nonce must be at least 8 alphanumeric characters.");
        if (ExpirationTime is { } exp && now > exp) problems.Add("The sign-in request has expired.");
        if (NotBefore is { } nbf && now < nbf) problems.Add("The sign-in request is not valid yet.");
        if (IssuedAt > now.AddMinutes(5)) problems.Add("Issued-at time is in the future.");
        if (expectedDomain != null && !string.Equals(Domain, expectedDomain, StringComparison.OrdinalIgnoreCase))
            problems.Add($"Domain '{Domain}' does not match the requesting site '{expectedDomain}'.");
        if (expectedNetwork != null && Network != expectedNetwork)
            problems.Add($"Message is for {Network}, but the wallet is on {expectedNetwork}.");
        if (System.Uri.TryCreate(Uri, UriKind.Absolute, out var u) && !string.Equals(u.Authority, Domain, StringComparison.OrdinalIgnoreCase))
            problems.Add($"URI host '{u.Authority}' does not match domain '{Domain}'.");
        return problems;
    }

    /// <summary>Signs with the raw-message convention. Returns the 64-byte signature.</summary>
    public byte[] Sign(string seed, IQubicCrypt? crypt = null) => (crypt ?? new QubicCrypt()).SignRaw(seed, ToBytes());

    /// <summary>
    /// Verifies <paramref name="signature"/> (64 bytes) against <see cref="Identity"/>. Accepts the raw convention
    /// and the WalletConnect <c>qubic_sign</c> convention (prefixed + digest), so a sign-in requested through
    /// WalletConnect verifies too.
    /// </summary>
    public bool Verify(byte[] signature, IQubicCrypt? crypt = null)
    {
        crypt ??= new QubicCrypt();
        try
        {
            var publicKey = crypt.GetPublicKeyFromIdentity(Identity);
            return crypt.VerifyRaw(publicKey, ToBytes(), signature)
                || QubicMessageSigning.VerifyWalletConnect(Identity, ToString(), signature);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>A random alphanumeric nonce.</summary>
    public static string NewNonce(int length = 16)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        return string.Create(length, 0, (span, _) =>
        {
            for (int i = 0; i < span.Length; i++)
                span[i] = alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(alphabet.Length)];
        });
    }
}
