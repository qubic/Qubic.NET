namespace Qubic.Core.Contracts;

/// <summary>
/// Interface for smart contract output structs that can deserialize from bytes.
/// </summary>
public interface ISmartContractOutput<TSelf> where TSelf : ISmartContractOutput<TSelf>
{
    /// <summary>
    /// Deserializes an instance from the wire-format byte representation.
    /// </summary>
    static abstract TSelf FromBytes(ReadOnlySpan<byte> data);
}
