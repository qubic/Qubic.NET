using System.Text.Json;

namespace Qubic.Toolkit;

public sealed class ToolkitSettingsService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QubicToolkit");
    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    private int _tickOffset = 5;
    private bool _autoResend;
    private int _autoResendMaxRetries = 3;
    private List<string> _favorites = [];
    private int _peerTickThreshold = 10;
    private int _autoDiscoverIntervalMinutes;

    // Connection settings
    private string _defaultBackend = "Rpc";
    private string _rpcUrl = "https://rpc.qubic.org";
    private string _bobUrl = "https://bob.qubic.li";
    private string _nodeHost = "corenet.qubic.li";
    private int _nodePort = 21841;

    public ToolkitSettingsService()
    {
        LoadFromDisk();
    }

    /// <summary>
    /// Number of ticks to add when using auto-tick (default 5).
    /// </summary>
    public int TickOffset
    {
        get => _tickOffset;
        set
        {
            if (value < 1) value = 1;
            if (value > 100) value = 100;
            if (_tickOffset == value) return;
            _tickOffset = value;
            SaveToDisk();
            OnChanged?.Invoke();
        }
    }

    /// <summary>
    /// When enabled, failed/expired transactions are automatically rebroadcast with a fresh tick.
    /// Requires an active seed.
    /// </summary>
    public bool AutoResend
    {
        get => _autoResend;
        set
        {
            if (_autoResend == value) return;
            _autoResend = value;
            SaveToDisk();
            OnChanged?.Invoke();
        }
    }

    /// <summary>
    /// Maximum number of auto-resend attempts per transaction (default 3).
    /// </summary>
    public int AutoResendMaxRetries
    {
        get => _autoResendMaxRetries;
        set
        {
            if (value < 1) value = 1;
            if (value > 20) value = 20;
            if (_autoResendMaxRetries == value) return;
            _autoResendMaxRetries = value;
            SaveToDisk();
            OnChanged?.Invoke();
        }
    }

    public IReadOnlyList<string> Favorites => _favorites;

    public bool IsFavorite(string path) => _favorites.Contains(path);

    public void ToggleFavorite(string path)
    {
        if (!_favorites.Remove(path))
            _favorites.Add(path);
        SaveToDisk();
        OnChanged?.Invoke();
    }

    /// <summary>
    /// Maximum tick difference before auto-discover switches to a more recent peer (default 10).
    /// </summary>
    public int PeerTickThreshold
    {
        get => _peerTickThreshold;
        set
        {
            if (value < 1) value = 1;
            if (value > 100) value = 100;
            if (_peerTickThreshold == value) return;
            _peerTickThreshold = value;
            SaveToDisk();
            OnChanged?.Invoke();
        }
    }

    /// <summary>
    /// Interval in minutes for background auto-discover (0 = disabled).
    /// </summary>
    public int AutoDiscoverIntervalMinutes
    {
        get => _autoDiscoverIntervalMinutes;
        set
        {
            if (value < 0) value = 0;
            if (value > 1440) value = 1440;
            if (_autoDiscoverIntervalMinutes == value) return;
            _autoDiscoverIntervalMinutes = value;
            SaveToDisk();
            OnChanged?.Invoke();
        }
    }

    // ── Connection settings ──

    /// <summary>Default backend: "Rpc", "Bob", or "DirectNetwork".</summary>
    public string DefaultBackend
    {
        get => _defaultBackend;
        set
        {
            if (_defaultBackend == value) return;
            _defaultBackend = value;
            SaveToDisk();
            OnChanged?.Invoke();
        }
    }

    public string RpcUrl
    {
        get => _rpcUrl;
        set
        {
            value = value?.Trim() ?? "";
            if (_rpcUrl == value) return;
            _rpcUrl = value;
            SaveToDisk();
            OnChanged?.Invoke();
        }
    }

    public string BobUrl
    {
        get => _bobUrl;
        set
        {
            value = value?.Trim() ?? "";
            if (_bobUrl == value) return;
            _bobUrl = value;
            SaveToDisk();
            OnChanged?.Invoke();
        }
    }

    public string NodeHost
    {
        get => _nodeHost;
        set
        {
            value = value?.Trim() ?? "";
            if (_nodeHost == value) return;
            _nodeHost = value;
            SaveToDisk();
            OnChanged?.Invoke();
        }
    }

    public int NodePort
    {
        get => _nodePort;
        set
        {
            if (value < 1) value = 1;
            if (value > 65535) value = 65535;
            if (_nodePort == value) return;
            _nodePort = value;
            SaveToDisk();
            OnChanged?.Invoke();
        }
    }

    public event Action? OnChanged;

    private void SaveToDisk()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var data = new SettingsData
            {
                TickOffset = _tickOffset,
                AutoResend = _autoResend,
                AutoResendMaxRetries = _autoResendMaxRetries,
                PeerTickThreshold = _peerTickThreshold,
                AutoDiscoverIntervalMinutes = _autoDiscoverIntervalMinutes,
                Favorites = _favorites,
                DefaultBackend = _defaultBackend,
                RpcUrl = _rpcUrl,
                BobUrl = _bobUrl,
                NodeHost = _nodeHost,
                NodePort = _nodePort
            };
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return;
            var json = File.ReadAllText(SettingsFile);
            var data = JsonSerializer.Deserialize<SettingsData>(json);
            if (data == null) return;
            _tickOffset = Math.Clamp(data.TickOffset, 1, 100);
            _autoResend = data.AutoResend;
            _autoResendMaxRetries = Math.Clamp(data.AutoResendMaxRetries, 1, 20);
            _peerTickThreshold = Math.Clamp(data.PeerTickThreshold, 1, 100);
            _autoDiscoverIntervalMinutes = Math.Clamp(data.AutoDiscoverIntervalMinutes, 0, 1440);
            _favorites = data.Favorites ?? [];
            _defaultBackend = data.DefaultBackend is "Rpc" or "Bob" or "DirectNetwork" ? data.DefaultBackend : "Rpc";
            if (!string.IsNullOrEmpty(data.RpcUrl)) _rpcUrl = data.RpcUrl;
            if (!string.IsNullOrEmpty(data.BobUrl)) _bobUrl = data.BobUrl;
            if (!string.IsNullOrEmpty(data.NodeHost)) _nodeHost = data.NodeHost;
            if (data.NodePort > 0 && data.NodePort <= 65535) _nodePort = data.NodePort;
        }
        catch { /* corrupted file, use defaults */ }
    }

    private sealed class SettingsData
    {
        public int TickOffset { get; set; } = 5;
        public bool AutoResend { get; set; }
        public int AutoResendMaxRetries { get; set; } = 3;
        public int PeerTickThreshold { get; set; } = 10;
        public int AutoDiscoverIntervalMinutes { get; set; }
        public List<string> Favorites { get; set; } = [];
        public string DefaultBackend { get; set; } = "Rpc";
        public string RpcUrl { get; set; } = "https://rpc.qubic.org";
        public string BobUrl { get; set; } = "https://bob.qubic.li";
        public string NodeHost { get; set; } = "corenet.qubic.li";
        public int NodePort { get; set; } = 21841;
    }
}
