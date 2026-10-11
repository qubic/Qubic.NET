namespace Qubic.Services.Features;

/// <summary>Optional wallet features the user must opt into.</summary>
public enum WalletFeature
{
    /// <summary>Fetch address labels, token names and contract fees from static.qubic.org.</summary>
    RemoteLabels,
    /// <summary>Dev accounts, faucet, contract console (core-lite devnets only).</summary>
    DeveloperTools,
    /// <summary>Accept "Sign-In with Qubic" login requests.</summary>
    SignInWithQubic,
    /// <summary>Connect to dApps via WalletConnect v2.</summary>
    WalletConnect,
    /// <summary>Local JSON-RPC signing endpoint for scripts and tests (desktop).</summary>
    LocalSigningEndpoint,
    /// <summary>Let the local endpoint sign with public dev accounts on devnets without asking.</summary>
    LocalEndpointAutoApproveDev,
}

/// <summary>Description of an optional feature, shown on the Features screen.</summary>
public sealed record FeatureInfo(
    WalletFeature Feature,
    string Title,
    string Description,
    string ConnectsTo,
    string Risk,
    bool DefaultEnabled,
    int IntroducedInFeatureSet,
    WalletFeature? Requires = null,
    bool DesktopOnly = false);

/// <summary>
/// Opt-in switches for optional features. On first launch — and after an update that adds
/// new opt-in features — the app shows the Features screen (<see cref="NeedsReview"/>).
/// "Off" means the feature does not run at all: no sockets, listeners or background fetches.
/// Stored in app settings (not in the vault) so it applies to whichever vault is loaded.
/// </summary>
public sealed class FeatureService
{
    /// <summary>Bump when a release adds features that need the user's decision.</summary>
    public const int CurrentFeatureSet = 1;

    private const string ReviewedKey = "features.reviewedSet";
    private static string Key(WalletFeature f) => "feature." + f;

    private readonly QubicSettingsService _settings;

    public event Action<WalletFeature>? OnChanged;

    public FeatureService(QubicSettingsService settings)
    {
        _settings = settings;
    }

    public static IReadOnlyList<FeatureInfo> All { get; } =
    [
        new(WalletFeature.RemoteLabels,
            "Remote labels",
            "Shows names for exchanges, tokens and smart contracts, and fills in contract procedure fees, using the public Qubic data bundle. Only used on Mainnet.",
            "static.qubic.org",
            "Reveals to static.qubic.org that you use a Qubic wallet (no addresses are sent).",
            false, 1),
        new(WalletFeature.DeveloperTools,
            "Developer tools",
            "On a local AIO / core-lite testnet: public dev accounts, a built-in faucet, an asset helper and a console to call any contract — including your own, imported from its C++ header.",
            "Only the active devnet",
            "Dev accounts use publicly known seeds. Never use them, or send funds to them, on Mainnet.",
            false, 1),
        new(WalletFeature.SignInWithQubic,
            "Sign-In with Qubic",
            "Lets websites ask you to prove you own an identity by signing a structured login message (domain, nonce, expiry). Nothing is sent on-chain.",
            "Nothing by itself",
            "A signed login proves ownership to that site. Always check the domain shown in the request.",
            false, 1),
        new(WalletFeature.WalletConnect,
            "WalletConnect",
            "Connect to dApps (e.g. built with @qubic/react) by scanning a WalletConnect QR code. Every request is shown for approval before signing.",
            "WalletConnect relay (relay.walletconnect.org) and the dApps you pair with",
            "Paired dApps can request signatures; they can never sign without your approval.",
            false, 1),
        new(WalletFeature.LocalSigningEndpoint,
            "Local signing endpoint",
            "Opens a JSON-RPC endpoint on 127.0.0.1 (random port, secret token) so scripts and tests can ask the wallet to sign. Every request needs your approval.",
            "Nothing external — listens on 127.0.0.1 only",
            "Any local program with the token can request signatures.",
            false, 1, DesktopOnly: true),
        new(WalletFeature.LocalEndpointAutoApproveDev,
            "Auto-approve dev accounts",
            "Lets the local endpoint sign with public dev accounts on a core-lite devnet without asking — for automated contract tests.",
            "Only the active devnet",
            "Only applies to public dev keys on a proven core-lite devnet; never to your own seeds.",
            false, 1, Requires: WalletFeature.LocalSigningEndpoint, DesktopOnly: true),
    ];

    public static FeatureInfo Info(WalletFeature f) => All.First(i => i.Feature == f);

    public bool IsEnabled(WalletFeature feature)
    {
        var info = Info(feature);
        if (info.Requires is { } parent && !IsEnabled(parent)) return false;
        if (feature == WalletFeature.RemoteLabels) return _settings.RemoteLabelsEnabled;
        return _settings.GetCustom<bool?>(Key(feature)) ?? info.DefaultEnabled;
    }

    public void SetEnabled(WalletFeature feature, bool enabled)
    {
        if (IsStoredEnabled(feature) == enabled) return;
        if (feature == WalletFeature.RemoteLabels)
            _settings.RemoteLabelsEnabled = enabled;
        else
            _settings.SetCustom(Key(feature), enabled);
        OnChanged?.Invoke(feature);
        // Turning a parent off turns its dependents off too.
        if (!enabled)
            foreach (var child in All.Where(i => i.Requires == feature))
                SetEnabled(child.Feature, false);
    }

    private bool IsStoredEnabled(WalletFeature feature) =>
        feature == WalletFeature.RemoteLabels
            ? _settings.RemoteLabelsEnabled
            : _settings.GetCustom<bool?>(Key(feature)) ?? Info(feature).DefaultEnabled;

    /// <summary>The Features screen must be shown (first launch, or new opt-in features since last review).</summary>
    public bool NeedsReview => ReviewedSet < CurrentFeatureSet;

    private int ReviewedSet => _settings.GetCustom<int?>(ReviewedKey) ?? 0;

    /// <summary>Features introduced since the user last reviewed (all of them on first launch).</summary>
    public IEnumerable<FeatureInfo> NewSinceReview(bool desktop) =>
        All.Where(i => i.IntroducedInFeatureSet > ReviewedSet && (desktop || !i.DesktopOnly));

    /// <summary>Stores the user's choices and marks the current feature set as reviewed.</summary>
    public void CompleteReview(IReadOnlyDictionary<WalletFeature, bool> choices)
    {
        foreach (var (feature, enabled) in choices)
            SetEnabled(feature, enabled);
        _settings.SetCustom(ReviewedKey, CurrentFeatureSet);
        // The old one-question "extended services" prompt is covered by this screen.
        _settings.ExtendedServicesConfigured ??= true;
    }
}
