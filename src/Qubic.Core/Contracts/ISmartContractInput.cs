namespace Qubic.Core.Contracts;

/// <summary>
/// Interface for smart contract input structs with binary serialization.
/// </summary>
public interface ISmartContractInput
{
    /// <summary>
    /// The size of the serialized payload in bytes.
    /// </summary>
    int SerializedSize { get; }

    /// <summary>
    /// Serializes this input to its wire-format byte representation.
    /// </summary>
    byte[] ToBytes();
}
