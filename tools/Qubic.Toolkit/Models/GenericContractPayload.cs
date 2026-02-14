using Qubic.Core.Payloads;

namespace Qubic.Toolkit.Models;

/// <summary>
/// Generic ITransactionPayload wrapper for arbitrary contract procedure calls.
/// </summary>
public sealed class GenericContractPayload : ITransactionPayload
{
    public ushort InputType { get; }
    public ushort InputSize { get; }
    private readonly byte[] _data;

    public GenericContractPayload(ushort inputType, byte[] data)
    {
        InputType = inputType;
        InputSize = (ushort)data.Length;
        _data = data;
    }

    public byte[] GetPayloadBytes() => _data;
}
