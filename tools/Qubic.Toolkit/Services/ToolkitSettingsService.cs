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
                Favorites = _favorites
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
            _favorites = data.Favorites ?? [];
        }
        catch { /* corrupted file, use defaults */ }
    }

    private sealed class SettingsData
    {
        public int TickOffset { get; set; } = 5;
        public bool AutoResend { get; set; }
        public int AutoResendMaxRetries { get; set; } = 3;
        public List<string> Favorites { get; set; } = [];
    }
}
