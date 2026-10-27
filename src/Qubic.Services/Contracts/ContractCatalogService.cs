using Qubic.Core.Contracts.Metadata;
using Qubic.Rpc.Models;
using Qubic.Services.Networks;

namespace Qubic.Services.Contracts;

/// <summary>A contract header the user imported for a network (persisted per profile).</summary>
public sealed class ImportedContract
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public string StructName { get; set; } = "";
    public string Source { get; set; } = "";
    public DateTime ImportedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Contract metadata for the active network: built-in contracts plus headers the user
/// imported for this network (e.g. their own contract on an AIO devnet), plus the
/// node's on-chain contract list where the profile can provide it.
/// Used for signing previews and the contract console.
/// </summary>
public sealed class ContractCatalogService
{
    private readonly QubicSettingsService _settings;
    private readonly NetworkProfileService _profiles;
    private readonly QubicBackendService _backend;
    private ContractCatalog _catalog = new();
    private IReadOnlyList<ContractInfo> _onChain = [];

    public event Action? OnChanged;

    public ContractCatalogService(QubicSettingsService settings, NetworkProfileService profiles, QubicBackendService backend)
    {
        _settings = settings;
        _profiles = profiles;
        _backend = backend;
        Reload();
        _profiles.OnActiveProfileChanged += (o, n) =>
        {
            if (o.Id == n.Id) return;
            _onChain = [];
            Reload();
        };
    }

    /// <summary>Catalog for the active network.</summary>
    public ContractCatalog Catalog => _catalog;

    /// <summary>Contracts reported by the node (empty until <see cref="RefreshOnChainAsync"/> succeeded).</summary>
    public IReadOnlyList<ContractInfo> OnChainContracts => _onChain;

    private string Key => "contracts." + _profiles.ActiveId;

    public IReadOnlyList<ImportedContract> Imported =>
        _settings.GetCustom<List<ImportedContract>>(Key) ?? [];

    private void Reload()
    {
        var catalog = new ContractCatalog();
        foreach (var c in Imported)
        {
            try { catalog.ImportHeader(c.Source, c.Index, c.Name, c.StructName); }
            catch { /* header no longer parses; keep the entry so the user can fix or remove it */ }
        }
        _catalog = catalog;
        OnChanged?.Invoke();
    }

    /// <summary>Parses and stores a contract header for the active network. Returns the definition and parser warnings.</summary>
    public (ContractDefinition Definition, IReadOnlyList<string> Warnings) Import(string source, int contractIndex, string? name = null)
    {
        if (contractIndex is < 1 or > 1023) throw new ArgumentOutOfRangeException(nameof(contractIndex), "Contract index must be 1-1023.");
        var structName = ContractCatalog.DetectContractStruct(source)
            ?? throw new FormatException("No 'struct X : public ContractBase' found in the header.");
        var warnings = new List<string>();
        var probe = new ContractCatalog();
        var def = probe.ImportHeader(source, contractIndex, name ?? structName, structName, warnings);
        if (def.Functions.Count == 0 && def.Procedures.Count == 0)
            throw new FormatException("No REGISTER_USER_FUNCTION / REGISTER_USER_PROCEDURE entries found.");

        var list = Imported.Where(c => c.Index != contractIndex).ToList();
        list.Add(new ImportedContract { Index = contractIndex, Name = name ?? structName, StructName = structName, Source = source });
        _settings.SetCustom(Key, list);
        Reload();
        return (def, warnings);
    }

    public void Remove(int contractIndex)
    {
        _settings.SetCustom(Key, Imported.Where(c => c.Index != contractIndex).ToList());
        Reload();
    }

    /// <summary>Loads the node's contract list if the active profile can serve it.</summary>
    public async Task<bool> RefreshOnChainAsync(CancellationToken ct = default)
    {
        if (!_backend.IsAvailable(Capability.ContractRegistry)) return false;
        try
        {
            _onChain = await _backend.GetContractsAsync(ct);
            OnChanged?.Invoke();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Best display name for a contract index on this network.</summary>
    public string? GetName(int contractIndex) =>
        _catalog.Get(contractIndex)?.CSharpName
        ?? _onChain.FirstOrDefault(c => c.Index == contractIndex)?.Name;

    /// <summary>Decodes a transaction to a contract, or null if the destination is not a contract.</summary>
    public DecodedCall? Decode(string destinationIdentity, int inputType, ReadOnlySpan<byte> payload)
    {
        var index = ContractCatalog.GetContractIndex(destinationIdentity);
        if (index is null) return null;
        var decoded = _catalog.DecodeProcedureCall(index.Value, inputType, payload);
        if (decoded != null) return decoded;
        // Unknown to the catalog: still name it if the node told us about it.
        var name = GetName(index.Value) ?? $"Contract #{index}";
        return new DecodedCall(index.Value, name, null, inputType, [], -1, payload.Length);
    }
}
