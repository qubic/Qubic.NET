using System.Text.Json.Serialization;

namespace Qubic.Bob.Models;

/// <summary>
/// Response from qubic_getComputors containing the list of 676 computor identities for an epoch.
/// </summary>
public sealed class ComputorsResponse
{
    [JsonPropertyName("epoch")]
    public uint Epoch { get; set; }

    [JsonPropertyName("computors")]
    public List<string> Computors { get; set; } = new();

    /// <summary>Arbitrator signature over the computor list (hex, 64 bytes).</summary>
    [JsonPropertyName("signature")]
    public string? Signature { get; set; }
}
