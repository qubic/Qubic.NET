using System.Collections.Concurrent;

namespace Qubic.Services.Networks;

public enum EgressGuardMode
{
    /// <summary>No checks.</summary>
    Off,
    /// <summary>Record violations but let the connection through.</summary>
    Log,
    /// <summary>Record violations and refuse the connection (default).</summary>
    Block,
}

public sealed record EgressViolation(DateTime TimestampUtc, string Host, int? Port, string Purpose, string ProfileName);

/// <summary>Thrown when a connection to a host outside the active network profile was refused.</summary>
public sealed class EgressBlockedException : InvalidOperationException
{
    public string Host { get; }

    public EgressBlockedException(string host, string purpose, string profileName)
        : base($"Blocked connection to {host} ({purpose}): not part of network \"{profileName}\".")
    {
        Host = host;
    }
}

/// <summary>
/// Allow-list for every outbound connection the wallet services make.
/// Allowed: the hosts of the active profile's enabled methods, plus hosts that
/// enabled features registered via <see cref="AllowFeatureHost"/> (e.g. the
/// static.qubic.org bundle when remote labels are on). Direct peer discovery,
/// when enabled in the profile, allows any host on the Direct port.
/// </summary>
public sealed class EgressGuard
{
    private readonly NetworkProfileService _profiles;
    private readonly ConcurrentDictionary<string, string> _featureHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<EgressViolation> _violations = [];
    private const int MaxViolations = 200;

    public EgressGuardMode Mode { get; set; } = EgressGuardMode.Block;

    /// <summary>Raised for every refused or (in Log mode) recorded connection.</summary>
    public event Action<EgressViolation>? OnViolation;

    public EgressGuard(NetworkProfileService profiles)
    {
        _profiles = profiles;
    }

    public IReadOnlyList<EgressViolation> Violations
    {
        get { lock (_violations) return _violations.ToList(); }
    }

    public void ClearViolations()
    {
        lock (_violations) _violations.Clear();
    }

    /// <summary>Allows <paramref name="host"/> on behalf of a feature (replaces the feature's previous host).</summary>
    public void AllowFeatureHost(string featureKey, string host) => _featureHosts[featureKey] = host;

    public void RevokeFeatureHost(string featureKey) => _featureHosts.TryRemove(featureKey, out _);

    /// <summary>Hosts currently allowed for the active profile (for display).</summary>
    public IReadOnlyCollection<string> AllowedHosts
    {
        get
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in _profiles.Active.EnabledMethods)
                if (m.HostName is { } h) set.Add(h);
            foreach (var h in _featureHosts.Values) set.Add(h);
            return set;
        }
    }

    public bool IsAllowed(string host, int? port = null)
    {
        if (Mode == EgressGuardMode.Off) return true;
        var profile = _profiles.Active;
        foreach (var m in profile.EnabledMethods)
        {
            if (m.HostName is { } h && string.Equals(h, host, StringComparison.OrdinalIgnoreCase))
                return true;
            if (m.Kind == BackendMethodKind.Direct && m.PeerDiscovery && port == m.Port)
                return true;
        }
        return _featureHosts.Values.Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Checks an outbound connection. Throws <see cref="EgressBlockedException"/> in Block mode.</summary>
    public void Check(string host, int? port, string purpose)
    {
        if (IsAllowed(host, port)) return;
        var profileName = _profiles.Active.Name;
        var v = new EgressViolation(DateTime.UtcNow, host, port, purpose, profileName);
        lock (_violations)
        {
            _violations.Add(v);
            if (_violations.Count > MaxViolations) _violations.RemoveAt(0);
        }
        try { OnViolation?.Invoke(v); } catch { /* observer errors must not change the outcome */ }
        if (Mode == EgressGuardMode.Block)
            throw new EgressBlockedException(host, purpose, profileName);
    }

    public void Check(Uri uri, string purpose) => Check(uri.Host, uri.IsDefaultPort ? null : uri.Port, purpose);

    /// <summary>An <see cref="HttpClient"/> whose every request is checked by this guard.</summary>
    public HttpClient CreateHttpClient(string baseUrl, string purpose, TimeSpan? timeout = null)
    {
        var client = new HttpClient(new GuardHandler(this, purpose, new SocketsHttpHandler()), disposeHandler: true)
        {
            BaseAddress = new Uri(baseUrl)
        };
        if (timeout.HasValue) client.Timeout = timeout.Value;
        return client;
    }

    /// <summary>An <see cref="HttpClient"/> without base address whose every request is checked by this guard.</summary>
    public HttpClient CreateHttpClient(string purpose, TimeSpan? timeout = null)
    {
        var client = new HttpClient(new GuardHandler(this, purpose, new SocketsHttpHandler()), disposeHandler: true);
        if (timeout.HasValue) client.Timeout = timeout.Value;
        return client;
    }

    private sealed class GuardHandler(EgressGuard guard, string purpose, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is { } uri)
                guard.Check(uri, purpose);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
