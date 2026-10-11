using Qubic.Crypto;

namespace Qubic.Services.Networks;

/// <summary>A public dev account derived from a core-lite seed.</summary>
public sealed record DevAccount(int Index, string Seed, string Identity, bool IsComputor);

public static partial class CoreLiteDevSeeds
{
    private static readonly Lazy<IReadOnlyList<DevAccount>> AccountsLazy = new(DeriveAccounts, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<HashSet<string>> ComputorIdentitiesLazy = new(
        () => AccountsLazy.Value.Where(a => a.IsComputor).Select(a => a.Identity).ToHashSet(StringComparer.Ordinal),
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<HashSet<string>> AllIdentitiesLazy = new(
        () => AccountsLazy.Value.Select(a => a.Identity).ToHashSet(StringComparer.Ordinal),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>All public dev accounts: the 676 computors first, then the custom seeds.</summary>
    public static IReadOnlyList<DevAccount> Accounts => AccountsLazy.Value;

    /// <summary>Identities of the 676 core-lite computors.</summary>
    public static IReadOnlySet<string> ComputorIdentities => ComputorIdentitiesLazy.Value;

    /// <summary>
    /// True if <paramref name="identity"/> belongs to a public core-lite seed.
    /// Funds held by such an identity can be taken by anyone.
    /// </summary>
    public static bool IsPublicDevIdentity(string? identity) =>
        !string.IsNullOrEmpty(identity) && AllIdentitiesLazy.Value.Contains(identity);

    /// <summary>True if <paramref name="seed"/> is one of the public core-lite seeds.</summary>
    public static bool IsPublicDevSeed(string? seed) =>
        !string.IsNullOrEmpty(seed) && (ComputorSeeds.Contains(seed) || CustomSeeds.Contains(seed));

    /// <summary>Starts deriving identities in the background so the first lookup is fast.</summary>
    public static void Warmup() => ThreadPool.QueueUserWorkItem(_ => _ = AllIdentitiesLazy.Value);

    private static IReadOnlyList<DevAccount> DeriveAccounts()
    {
        var crypt = new QubicCrypt();
        var list = new List<DevAccount>(ComputorSeeds.Count + CustomSeeds.Count);
        for (int i = 0; i < ComputorSeeds.Count; i++)
            list.Add(new DevAccount(i, ComputorSeeds[i], crypt.GetIdentityFromPublicKey(crypt.GetPublicKey(ComputorSeeds[i])), true));
        for (int i = 0; i < CustomSeeds.Count; i++)
            list.Add(new DevAccount(ComputorSeeds.Count + i, CustomSeeds[i], crypt.GetIdentityFromPublicKey(crypt.GetPublicKey(CustomSeeds[i])), false));
        return list;
    }
}
