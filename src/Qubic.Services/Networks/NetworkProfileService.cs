namespace Qubic.Services.Networks;

/// <summary>
/// Stores the user's network profiles and which one is active.
/// Persisted in settings.json under <c>network.profiles</c> / <c>network.active</c>.
/// On first run the legacy flat settings (DefaultBackend, RpcUrl, BobUrl, NodeHost,
/// NodePort, TickOffset) are migrated into a "Mainnet" profile so existing users
/// keep their endpoints and their wallet database.
/// </summary>
public sealed class NetworkProfileService
{
    private const string ProfilesKey = "network.profiles";
    private const string ActiveKey = "network.active";

    private readonly QubicSettingsService _settings;
    private readonly object _lock = new();
    private List<NetworkProfile> _profiles;
    private string _activeId;

    /// <summary>Raised after the active profile changed (old, new).</summary>
    public event Action<NetworkProfile, NetworkProfile>? OnActiveProfileChanged;

    /// <summary>Raised when any profile was added, edited or removed.</summary>
    public event Action? OnProfilesChanged;

    public NetworkProfileService(QubicSettingsService settings)
    {
        _settings = settings;
        _profiles = settings.GetCustom<List<NetworkProfile>>(ProfilesKey) ?? [];
        _activeId = settings.GetCustom<string>(ActiveKey) ?? "";

        if (_profiles.Count == 0)
        {
            _profiles = [MigrateLegacySettings(settings), NetworkPresets.AioLocal(), NetworkPresets.AioRemote()];
            _activeId = NetworkPresets.MainnetId;
            Persist();
        }

        if (_profiles.All(p => p.Id != _activeId))
        {
            _activeId = _profiles[0].Id;
            Persist();
        }
    }

    public IReadOnlyList<NetworkProfile> Profiles
    {
        get { lock (_lock) return _profiles.Select(p => p.Clone()).ToList(); }
    }

    /// <summary>A snapshot of the active profile. Edit via <see cref="Save"/>.</summary>
    public NetworkProfile Active
    {
        get { lock (_lock) return _profiles.First(p => p.Id == _activeId).Clone(); }
    }

    public string ActiveId
    {
        get { lock (_lock) return _activeId; }
    }

    public NetworkProfile? Get(string id)
    {
        lock (_lock) return _profiles.FirstOrDefault(p => p.Id == id)?.Clone();
    }

    /// <summary>
    /// Makes <paramref name="id"/> the active profile. Prefer <see cref="NetworkSwitchService"/>
    /// in apps: it stops and restarts the dependent services in the right order.
    /// </summary>
    public void SetActive(string id)
    {
        NetworkProfile oldP, newP;
        lock (_lock)
        {
            if (id == _activeId) return;
            newP = _profiles.FirstOrDefault(p => p.Id == id)?.Clone()
                ?? throw new ArgumentException($"Unknown network profile '{id}'.", nameof(id));
            oldP = _profiles.First(p => p.Id == _activeId).Clone();
            _activeId = id;
            Persist();
        }
        OnActiveProfileChanged?.Invoke(oldP, newP);
    }

    /// <summary>Adds or replaces a profile (matched by Id). Returns the stored copy.</summary>
    public NetworkProfile Save(NetworkProfile profile)
    {
        Validate(profile);
        NetworkProfile? oldActive = null, newActive = null;
        lock (_lock)
        {
            var copy = profile.Clone();
            var idx = _profiles.FindIndex(p => p.Id == copy.Id);
            if (idx >= 0)
            {
                if (copy.Id == _activeId) oldActive = _profiles[idx].Clone();
                _profiles[idx] = copy;
            }
            else
            {
                _profiles.Add(copy);
            }
            if (copy.Id == _activeId) newActive = copy.Clone();
            Persist();
        }
        OnProfilesChanged?.Invoke();
        // Editing the active profile counts as a switch for dependent services (endpoints may differ).
        if (oldActive != null && newActive != null)
            OnActiveProfileChanged?.Invoke(oldActive, newActive);
        return profile.Clone();
    }

    /// <summary>Creates a copy of an existing profile with a new id.</summary>
    public NetworkProfile Duplicate(string id)
    {
        var source = Get(id) ?? throw new ArgumentException($"Unknown network profile '{id}'.", nameof(id));
        var copy = source.Clone();
        copy.Id = Guid.NewGuid().ToString("N")[..12];
        copy.Name = source.Name + " (copy)";
        return Save(copy);
    }

    /// <summary>Removes a profile. The active profile and the last profile cannot be removed.</summary>
    public void Delete(string id)
    {
        lock (_lock)
        {
            if (id == _activeId)
                throw new InvalidOperationException("Switch to another network before deleting this one.");
            if (_profiles.Count <= 1)
                throw new InvalidOperationException("At least one network profile is required.");
            _profiles.RemoveAll(p => p.Id == id);
            Persist();
        }
        OnProfilesChanged?.Invoke();
    }

    /// <summary>Restores a preset-derived profile to the preset's defaults (keeps its Id).</summary>
    public NetworkProfile ResetToPreset(string id)
    {
        var current = Get(id) ?? throw new ArgumentException($"Unknown network profile '{id}'.", nameof(id));
        var preset = NetworkPresets.ById(current.PresetId)
            ?? throw new InvalidOperationException("This profile was not created from a preset.");
        preset.Id = current.Id;
        return Save(preset);
    }

    /// <summary>Re-adds any built-in preset the user deleted.</summary>
    public void RestoreMissingPresets()
    {
        var added = false;
        lock (_lock)
        {
            foreach (var preset in NetworkPresets.All())
            {
                if (_profiles.Any(p => p.Id == preset.Id || p.PresetId == preset.PresetId)) continue;
                _profiles.Add(preset);
                added = true;
            }
            if (added) Persist();
        }
        if (added) OnProfilesChanged?.Invoke();
    }

    public static void Validate(NetworkProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Id)) throw new ArgumentException("Profile id is required.");
        if (string.IsNullOrWhiteSpace(profile.Name)) throw new ArgumentException("Profile name is required.");
        if (!profile.EnabledMethods.Any())
            throw new ArgumentException("A network needs at least one enabled method (Bob, Direct or RPC).");
        foreach (var m in profile.EnabledMethods)
        {
            if (m.Kind == BackendMethodKind.Direct)
            {
                if (string.IsNullOrWhiteSpace(m.Host)) throw new ArgumentException("Direct method needs a host.");
                if (m.Port is < 1 or > 65535) throw new ArgumentException("Direct method port must be 1-65535.");
            }
            else if (!Uri.TryCreate(m.Url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
            {
                throw new ArgumentException($"{m.Kind} method needs an http(s) URL.");
            }
        }
        if (profile.EnabledMethods.GroupBy(m => m.Kind).Any(g => g.Count() > 1))
            throw new ArgumentException("Each method kind can only be enabled once per network.");
        if (profile.Routes != null)
        {
            // Drop choices a method cannot serve (e.g. edited by hand); keep pins to disabled methods (shown as unavailable).
            foreach (var (cap, kind) in profile.Routes.ToList())
                if (!Capabilities.SupportedBy(cap).Contains(kind)) profile.Routes.Remove(cap);
            if (profile.Routes.Count == 0) profile.Routes = null;
        }
        profile.TickOffset = Math.Clamp(profile.TickOffset, 1, 100);
        profile.TickTargetSeconds = Math.Clamp(profile.TickTargetSeconds, 1, 600);
    }

    private void Persist()
    {
        _settings.SetCustom(ProfilesKey, _profiles);
        _settings.SetCustom(ActiveKey, _activeId);
    }

    /// <summary>
    /// Builds the Mainnet profile from the pre-profile flat settings. The default backend
    /// moves to the front so routing matches what the user had selected before.
    /// </summary>
    internal static NetworkProfile MigrateLegacySettings(QubicSettingsService s)
    {
        var p = NetworkPresets.Mainnet();
        p.TickOffset = s.TickOffset;

        var rpc = p.Methods.First(m => m.Kind == BackendMethodKind.Rpc);
        var bob = p.Methods.First(m => m.Kind == BackendMethodKind.Bob);
        var direct = p.Methods.First(m => m.Kind == BackendMethodKind.Direct);
        if (!string.IsNullOrWhiteSpace(s.RpcUrl)) rpc.Url = s.RpcUrl;
        if (!string.IsNullOrWhiteSpace(s.BobUrl)) bob.Url = s.BobUrl;
        if (!string.IsNullOrWhiteSpace(s.NodeHost)) direct.Host = s.NodeHost;
        if (s.NodePort > 0) direct.Port = s.NodePort;

        var preferred = s.DefaultBackend switch
        {
            "Bob" => BackendMethodKind.Bob,
            "DirectNetwork" => BackendMethodKind.Direct,
            _ => BackendMethodKind.Rpc
        };
        var first = p.Methods.First(m => m.Kind == preferred);
        p.Methods.Remove(first);
        p.Methods.Insert(0, first);
        return p;
    }
}
