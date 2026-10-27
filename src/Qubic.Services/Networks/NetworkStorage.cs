namespace Qubic.Services.Networks;

/// <summary>
/// Per-network partitioning of local data (wallet DB, asset registry, chain state).
/// The Mainnet profile keeps the pre-profile file names so existing users keep their data.
/// </summary>
public static class NetworkStorage
{
    /// <summary>"" for the Mainnet profile, otherwise "." + profile id (safe for file names).</summary>
    public static string Suffix(string profileId) =>
        profileId == NetworkPresets.MainnetId ? "" : "." + Sanitize(profileId);

    /// <summary>Inserts the profile suffix before the extension: assets.json → assets.aio-local.json.</summary>
    public static string PartitionFileName(string fileName, string profileId)
    {
        var suffix = Suffix(profileId);
        if (suffix.Length == 0) return fileName;
        var ext = Path.GetExtension(fileName);
        return Path.GetFileNameWithoutExtension(fileName) + suffix + ext;
    }

    private static string Sanitize(string id) =>
        new(id.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());
}
