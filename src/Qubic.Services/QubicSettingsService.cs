using System.Text.Json;
using System.Text.Json.Serialization;

namespace Qubic.Services;

public sealed class QubicSettingsService
{
    private readonly string _settingsDir;
    private readonly string _settingsFile;
    private SettingsData _data;

    public QubicSettingsService(string appName)
    {
        if (string.IsNullOrWhiteSpace(appName))
            throw new ArgumentException("App name is required.", nameof(appName));

        AppName = appName;
        _settingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appName);
        _settingsFile = Path.Combine(_settingsDir, "settings.json");
        _data = new SettingsData();
        LoadFromDisk();
    }

    /// <summary>The application name used for storage directory.</summary>
    public string AppName { get; }

    /// <summary>The storage directory path (%LOCALAPPDATA%/{appName}).</summary>
    public string StorageDirectory => _settingsDir;

    // ── Common settings ──

    /// <summary>Number of ticks to add when using auto-tick (default 12).</summary>
    public int TickOffset
    {
        get => _data.TickOffset;
        set
        {
            value = Math.Clamp(value, 1, 100);
            if (_data.TickOffset == value) return;
            _data.TickOffset = value;
            SaveToDisk();
            OnChanged?.Invoke();
        }
    }

    /// <summary>When enabled, failed/expired transactions are automatically rebroadcast.</summary>
    public bool AutoResend
    {
        get => _data.AutoResend;
        set
        {
            if (_data.AutoResend == value) return;
            _data.AutoResend = value;
            SaveToDisk();
            OnChanged?.Invoke();
        }
    }

    /// <summary>Maximum number of auto-resend attempts per transaction (default 3).</summary>
    public int AutoResendMaxRetries
    {
        get => _data.AutoResendMaxRetries;
        set
        {
            value = Math.Clamp(value, 1, 20);
            if (_data.AutoResendMaxRetries == value) return;
            _data.AutoResendMaxRetries = value;
            SaveToDisk();
            OnChanged?.Invoke();
        }
    }

    /// <summary>Default backend: "Rpc", "Bob", or "DirectNetwork".</summary>
    public string DefaultBackend
    {
        get => _data.DefaultBackend;
        set
        {
            if (_data.DefaultBackend == value) return;
            _data.DefaultBackend = value;
            SaveToDisk();
            OnChanged?.Invoke();
        }
    }

    public string RpcUrl
    {
        get => _data.RpcUrl;
        set
        {
            value = value?.Trim() ?? "";
            if (_data.RpcUrl == value) return;
            _data.RpcUrl = value;
            SaveToDisk();
            OnChanged?.Invoke();
        }
    }

    public string BobUrl
    {
        get => _data.BobUrl;
        set
        {
            value = value?.Trim() ?? "";
            if (_data.BobUrl == value) return;
            _data.BobUrl = value;
            SaveToDisk();
            OnChanged?.Invoke();
        }
    }

    public string NodeHost
    {
        get => _data.NodeHost;
        set
        {
            value = value?.Trim() ?? "";
            if (_data.NodeHost == value) return;
            _data.NodeHost = value;
            SaveToDisk();
            OnChanged?.Invoke();
        }
    }

    public int NodePort
    {
        get => _data.NodePort;
        set
        {
            value = Math.Clamp(value, 1, 65535);
            if (_data.NodePort == value) return;
            _data.NodePort = value;
            SaveToDisk();
            OnChanged?.Invoke();
        }
    }

    /// <summary>
    /// Whether the user has been asked about extended services (external data fetching).
    /// Null means the user hasn't been prompted yet (first run).
    /// </summary>
    public bool? ExtendedServicesConfigured
    {
        get => _data.ExtendedServicesConfigured;
        set
        {
            if (_data.ExtendedServicesConfigured == value) return;
            _data.ExtendedServicesConfigured = value;
            SaveToDisk();
            OnChanged?.Invoke();
        }
    }

    /// <summary>Whether to fetch address labels from the remote label registry on startup.</summary>
    public bool RemoteLabelsEnabled
    {
        get => _data.RemoteLabelsEnabled;
        set
        {
            if (_data.RemoteLabelsEnabled == value) return;
            _data.RemoteLabelsEnabled = value;
            SaveToDisk();
            OnChanged?.Invoke();
        }
    }

    // ── Extensible custom settings ──

    /// <summary>Gets a custom setting value, or default if not found.</summary>
    public T? GetCustom<T>(string key)
    {
        if (_data.Custom == null || !_data.Custom.TryGetValue(key, out var element))
            return default;

        try
        {
            return element.Deserialize<T>();
        }
        catch
        {
            return default;
        }
    }

    /// <summary>Sets a custom setting value.</summary>
    public void SetCustom<T>(string key, T value)
    {
        _data.Custom ??= new Dictionary<string, JsonElement>();
        _data.Custom[key] = JsonSerializer.SerializeToElement(value);
        SaveToDisk();
        OnChanged?.Invoke();
    }

    /// <summary>Removes a custom setting.</summary>
    public bool RemoveCustom(string key)
    {
        if (_data.Custom == null || !_data.Custom.Remove(key)) return false;
        SaveToDisk();
        OnChanged?.Invoke();
        return true;
    }

    public event Action? OnChanged;

    private void SaveToDisk()
    {
        try
        {
            Directory.CreateDirectory(_settingsDir);
            File.WriteAllText(_settingsFile, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_settingsFile)) return;
            var json = File.ReadAllText(_settingsFile);
            var data = JsonSerializer.Deserialize<SettingsData>(json);
            if (data != null) _data = data;
        }
        catch { /* corrupted file, use defaults */ }

        // Migrate retired Bob URL (exact match only)
        if (Uri.TryCreate(_data.BobUrl, UriKind.Absolute, out var bobUri)
            && bobUri.Host.Equals("bob.qubic.li", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(bobUri) { Host = "bobnet.qubic.li" };
            _data.BobUrl = builder.Uri.ToString().TrimEnd('/');
            SaveToDisk();
        }
    }

    private sealed class SettingsData
    {
        public int TickOffset { get; set; } = 12;
        public bool AutoResend { get; set; }
        public int AutoResendMaxRetries { get; set; } = 3;
        public string DefaultBackend { get; set; } = "Rpc";
        public string RpcUrl { get; set; } = "https://rpc.qubic.org";
        public string BobUrl { get; set; } = "https://bobnet.qubic.li";
        public string NodeHost { get; set; } = "corenet.qubic.li";
        public int NodePort { get; set; } = 21841;
        public bool? ExtendedServicesConfigured { get; set; }
        public bool RemoteLabelsEnabled { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Custom { get; set; }
    }
}
