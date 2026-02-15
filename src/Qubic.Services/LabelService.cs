using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Qubic.Services;

/// <summary>
/// Provides human-readable labels for Qubic addresses/identities.
/// Supports manual labels and remote loading from static.qubic.org.
/// </summary>
public sealed class LabelService
{
    private const string BundleUrl = "https://static.qubic.org/v1/general/data/bundle.min.json";

    private readonly Dictionary<string, string> _labels = new(_staticLabels, StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _remoteLabels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _userLabels = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> _staticLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAFXIB"] = "Null",
    };

    /// <summary>Raised when labels are added, removed, or changed.</summary>
    public event Action? OnLabelsChanged;

    /// <summary>Whether a remote fetch is currently in progress.</summary>
    public bool IsLoading { get; private set; }

    /// <summary>Error message from the last fetch attempt, if any.</summary>
    public string? LastError { get; private set; }

    /// <summary>Timestamp of the last successful remote fetch.</summary>
    public DateTime? LastFetched { get; private set; }

    /// <summary>Number of labels loaded from the remote registry.</summary>
    public int RemoteLabelCount => _remoteLabels.Count;

    /// <summary>Gets the label for an address, or null if not found. User labels take priority, then remote, then static.</summary>
    public string? GetLabel(string address)
    {
        if (_userLabels.TryGetValue(address, out var userLabel))
            return userLabel;
        return _labels.TryGetValue(address, out var label) ? label : null;
    }

    /// <summary>Gets the static built-in labels.</summary>
    public static IReadOnlyDictionary<string, string> StaticLabels => _staticLabels;

    /// <summary>Sets a single user-defined label.</summary>
    public void SetLabel(string address, string label)
    {
        _userLabels[address] = label;
        RebuildMerged();
    }

    /// <summary>Sets multiple user-defined labels at once.</summary>
    public void SetLabels(IEnumerable<KeyValuePair<string, string>> labels)
    {
        foreach (var kv in labels)
            _userLabels[kv.Key] = kv.Value;
        RebuildMerged();
    }

    /// <summary>Removes a user-defined label.</summary>
    public void RemoveLabel(string address)
    {
        if (_userLabels.Remove(address))
            RebuildMerged();
    }

    /// <summary>Gets all effective labels (remote + user overrides).</summary>
    public IReadOnlyDictionary<string, string> GetAll() => _labels;

    /// <summary>
    /// Fetches labels from the remote bundle at static.qubic.org.
    /// Parses exchanges, address_labels, tokens, and smart_contracts.
    /// </summary>
    public async Task<bool> LoadRemoteLabelsAsync(CancellationToken ct = default)
    {
        if (IsLoading) return false;

        IsLoading = true;
        LastError = null;
        OnLabelsChanged?.Invoke();

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var bundle = await http.GetFromJsonAsync<BundleData>(BundleUrl, ct);
            if (bundle == null)
            {
                LastError = "Empty response from label registry.";
                return false;
            }

            _remoteLabels.Clear();

            if (bundle.Exchanges != null)
                foreach (var e in bundle.Exchanges)
                    if (!string.IsNullOrEmpty(e.Address) && !string.IsNullOrEmpty(e.Name))
                        _remoteLabels.TryAdd(e.Address, e.Name);

            if (bundle.AddressLabels != null)
                foreach (var a in bundle.AddressLabels)
                    if (!string.IsNullOrEmpty(a.Address) && !string.IsNullOrEmpty(a.Label))
                        _remoteLabels.TryAdd(a.Address, a.Label);

            if (bundle.Tokens != null)
                foreach (var t in bundle.Tokens)
                    if (!string.IsNullOrEmpty(t.Issuer) && !string.IsNullOrEmpty(t.Name))
                        _remoteLabels.TryAdd(t.Issuer, $"{t.Name} (Issuer)");

            if (bundle.SmartContracts != null)
                foreach (var c in bundle.SmartContracts)
                    if (!string.IsNullOrEmpty(c.Address) && !string.IsNullOrEmpty(c.Name))
                        _remoteLabels.TryAdd(c.Address, c.Name);

            LastFetched = DateTime.Now;
            RebuildMerged();
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            OnLabelsChanged?.Invoke();
            return false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Clears all remote labels.</summary>
    public void ClearRemoteLabels()
    {
        _remoteLabels.Clear();
        LastFetched = null;
        RebuildMerged();
    }

    private void RebuildMerged()
    {
        _labels.Clear();
        foreach (var kv in _staticLabels)
            _labels[kv.Key] = kv.Value;
        foreach (var kv in _remoteLabels)
            _labels[kv.Key] = kv.Value;
        foreach (var kv in _userLabels)
            _labels[kv.Key] = kv.Value;
        OnLabelsChanged?.Invoke();
    }

    // ── Bundle JSON model ──

    private sealed class BundleData
    {
        [JsonPropertyName("exchanges")]
        public List<ExchangeEntry>? Exchanges { get; set; }

        [JsonPropertyName("address_labels")]
        public List<AddressLabelEntry>? AddressLabels { get; set; }

        [JsonPropertyName("tokens")]
        public List<TokenEntry>? Tokens { get; set; }

        [JsonPropertyName("smart_contracts")]
        public List<SmartContractEntry>? SmartContracts { get; set; }
    }

    private sealed class ExchangeEntry
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("address")]
        public string? Address { get; set; }
    }

    private sealed class AddressLabelEntry
    {
        [JsonPropertyName("label")]
        public string? Label { get; set; }

        [JsonPropertyName("address")]
        public string? Address { get; set; }
    }

    private sealed class TokenEntry
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("issuer")]
        public string? Issuer { get; set; }
    }

    private sealed class SmartContractEntry
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("address")]
        public string? Address { get; set; }
    }
}
