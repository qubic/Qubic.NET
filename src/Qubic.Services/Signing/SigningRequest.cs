using Qubic.Core.Auth;
using Qubic.Core.Contracts.Metadata;
using Qubic.Services.Networks;

namespace Qubic.Services.Signing;

public enum SigningKind { Transaction, Message, SignIn }

/// <summary>Who asked for the signature.</summary>
public enum SigningOrigin
{
    /// <summary>A page of this wallet.</summary>
    InApp,
    /// <summary>"Repeat" of a tracked transaction.</summary>
    Repeat,
    /// <summary>Developer tools (faucet, contract console) on a devnet.</summary>
    DevTools,
    /// <summary>A dApp connected via WalletConnect.</summary>
    WalletConnect,
    /// <summary>A local program via the local signing endpoint.</summary>
    LocalEndpoint,
}

public enum WarningLevel { Info, Caution, Danger }

public sealed record SigningWarning(WarningLevel Level, string Title, string Detail, string? Code = null);

/// <summary>Context supplied by the caller: origin, a human description, and for external requests who asked.</summary>
public sealed record SigningContext(
    SigningOrigin Origin = SigningOrigin.InApp,
    string? Description = null,
    string? RequesterName = null,
    string? RequesterUrl = null)
{
    public static SigningContext InApp(string? description = null) => new(SigningOrigin.InApp, description);

    public bool IsExternal => Origin is SigningOrigin.WalletConnect or SigningOrigin.LocalEndpoint;
}

/// <summary>
/// Everything the review sheet shows before a signature is made. Built by
/// <see cref="SigningReviewService"/>; presenters only read it.
/// </summary>
public sealed class SigningRequest
{
    public Guid Id { get; } = Guid.NewGuid();
    public DateTime CreatedUtc { get; } = DateTime.UtcNow;

    public required SigningKind Kind { get; init; }
    public required SigningContext Context { get; init; }
    public required NetworkProfile Network { get; init; }
    public NetworkFingerprint? Fingerprint { get; init; }

    public required string SourceIdentity { get; init; }
    public string? SourceLabel { get; init; }
    public bool SourceIsPublicDevKey { get; init; }

    // ── Transaction ──
    public string? Destination { get; init; }
    public string? DestinationLabel { get; init; }
    public long Amount { get; init; }
    public uint TargetTick { get; init; }
    public uint? CurrentTick { get; init; }
    public double? EtaSeconds { get; init; }
    public ushort InputType { get; init; }
    public byte[]? Payload { get; init; }
    public DecodedCall? Decoded { get; init; }

    // ── Message / sign-in ──
    public byte[]? MessageBytes { get; init; }
    public string? MessageText { get; init; }
    public SignInMessage? SignIn { get; init; }

    public List<SigningWarning> Warnings { get; } = [];

    /// <summary>True when a Danger warning requires an explicit "I understand" before approving.</summary>
    public bool RequiresAcknowledgement => Warnings.Any(w => w.Level == WarningLevel.Danger);

    /// <summary>Set when a policy approved the request without showing it (local endpoint + dev keys).</summary>
    public bool AutoApproved { get; internal set; }

    public string Title => Kind switch
    {
        SigningKind.SignIn => $"Sign in to {SignIn?.Domain}",
        SigningKind.Message => "Sign message",
        _ when Decoded is { } d => $"{d.ContractName}: {d.EntryName ?? $"procedure #{d.InputType}"}",
        _ when InputType != 0 => $"Contract call (input type {InputType})",
        _ => "Send QU"
    };
}

/// <summary>Thrown when the user (or policy) declined a signature request.</summary>
public sealed class SigningRejectedException : OperationCanceledException
{
    public SigningRejectedException(string message = "Signing was cancelled.") : base(message) { }
}

/// <summary>Implemented by the app UI: shows the review sheet and returns the user's decision.</summary>
public interface ISigningPresenter
{
    Task<bool> PresentAsync(SigningRequest request, CancellationToken ct);
}
