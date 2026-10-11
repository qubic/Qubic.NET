namespace Qubic.Services.Networks;

/// <summary>Last observed chain position of a network profile, persisted in settings.</summary>
public sealed class ChainStateRecord
{
    public uint Epoch { get; set; }
    public uint LastTick { get; set; }

    /// <summary>Incremented every time a chain reset is detected. Wallet DBs older than this are purged.</summary>
    public int Generation { get; set; }

    public DateTime? LastResetUtc { get; set; }
    public string? LastResetDetail { get; set; }
}

/// <summary>Settings-backed store for <see cref="ChainStateRecord"/> per profile.</summary>
public sealed class NetworkChainState
{
    private readonly QubicSettingsService _settings;

    public NetworkChainState(QubicSettingsService settings)
    {
        _settings = settings;
    }

    private static string Key(string profileId) => "chain." + profileId;

    public ChainStateRecord Get(string profileId) =>
        _settings.GetCustom<ChainStateRecord>(Key(profileId)) ?? new ChainStateRecord();

    public void Set(string profileId, ChainStateRecord record) => _settings.SetCustom(Key(profileId), record);

    public int GetGeneration(string profileId) => Get(profileId).Generation;
}
