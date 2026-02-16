using System.Text.Json;

namespace Qubic.Services;

public sealed class AssetRegistryService
{
    private readonly string _storageDir;
    private readonly string _assetsFile;
    private readonly string _lastRefreshFile;
    private List<AssetEntry> _assets = [];

    /// <summary>The SC token issuer identity (contract index 0 = zero public key).</summary>
    public const string ScTokenIssuer = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAFXIB";

    public AssetRegistryService(QubicSettingsService settings)
    {
        _storageDir = settings.StorageDirectory;
        _assetsFile = Path.Combine(_storageDir, "assets.json");
        _lastRefreshFile = Path.Combine(_storageDir, "assets_refreshed.txt");
        LoadFromDisk();
    }

    public IReadOnlyList<AssetEntry> GetAll() => _assets.AsReadOnly();

    public IEnumerable<string> GetIssuers() => _assets.Select(a => a.Issuer).Distinct();

    public IEnumerable<AssetEntry> GetAssetsForIssuer(string issuer) =>
        _assets.Where(a => a.Issuer.Equals(issuer, StringComparison.OrdinalIgnoreCase));

    public bool Add(string issuer, string name, string? label = null)
    {
        issuer = issuer.Trim().ToUpperInvariant();
        name = name.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(name)) return false;
        if (_assets.Any(a => a.Issuer == issuer && a.Name == name)) return false;

        _assets.Add(new AssetEntry { Issuer = issuer, Name = name, Label = label });
        SaveToDisk();
        OnChanged?.Invoke();
        return true;
    }

    public bool Remove(string issuer, string name)
    {
        var removed = _assets.RemoveAll(a =>
            a.Issuer.Equals(issuer, StringComparison.OrdinalIgnoreCase) &&
            a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            SaveToDisk();
            OnChanged?.Invoke();
        }
        return removed > 0;
    }

    /// <summary>
    /// Refreshes the asset list from the network if stale (older than 1 day) or if forced.
    /// Returns the number of assets after refresh, or -1 if refresh was skipped/failed.
    /// </summary>
    public async Task<int> RefreshFromNetworkAsync(QubicBackendService backend, bool force = false)
    {
        if (!force && !IsRefreshNeeded())
            return -1;

        try
        {
            var issuances = await backend.GetAllIssuancesAsync();
            if (issuances.Count == 0)
                return -1; // Don't wipe existing data on empty response

            var newAssets = new List<AssetEntry>();
            foreach (var item in issuances)
            {
                if (string.IsNullOrEmpty(item.Data.Name) || string.IsNullOrEmpty(item.Data.IssuerIdentity))
                    continue;
                newAssets.Add(new AssetEntry
                {
                    Issuer = item.Data.IssuerIdentity,
                    Name = item.Data.Name,
                });
            }

            _assets = newAssets;
            SaveToDisk();
            SaveLastRefresh();
            OnChanged?.Invoke();
            return _assets.Count;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>Whether a network refresh is needed (no data or older than 1 day).</summary>
    public bool IsRefreshNeeded()
    {
        if (_assets.Count == 0) return true;
        return GetLastRefresh() is not { } last || (DateTime.UtcNow - last).TotalHours >= 24;
    }

    public event Action? OnChanged;

    private void SaveToDisk()
    {
        try
        {
            Directory.CreateDirectory(_storageDir);
            File.WriteAllText(_assetsFile, JsonSerializer.Serialize(_assets, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_assetsFile)) return;
            var json = File.ReadAllText(_assetsFile);
            _assets = JsonSerializer.Deserialize<List<AssetEntry>>(json) ?? [];
        }
        catch { _assets = []; }
    }

    private DateTime? GetLastRefresh()
    {
        try
        {
            if (!File.Exists(_lastRefreshFile)) return null;
            return DateTime.TryParse(File.ReadAllText(_lastRefreshFile).Trim(), out var dt) ? dt : null;
        }
        catch { return null; }
    }

    private void SaveLastRefresh()
    {
        try
        {
            Directory.CreateDirectory(_storageDir);
            File.WriteAllText(_lastRefreshFile, DateTime.UtcNow.ToString("O"));
        }
        catch { }
    }

    public sealed class AssetEntry
    {
        public string Issuer { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Label { get; set; }

        public override string ToString() => Label != null ? $"{Name} ({Label})" : Name;
    }
}
