using System.Net.Sockets;
using Qubic.Core.Entities;
using Qubic.Serialization;
using Qubic.Serialization.Readers;
using Qubic.Serialization.Writers;

namespace Qubic.Network;

/// <summary>
/// Client for direct TCP communication with Qubic network nodes.
/// </summary>
public sealed class QubicNodeClient : IDisposable, IAsyncDisposable
{
    private const int DefaultPort = 21841;
    private const int DefaultTimeoutMs = 10000;
    private const int MaxPacketSize = 1024 * 1024; // 1MB

    private readonly string _host;
    private readonly int _port;
    private readonly int _timeoutMs;

    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly QubicPacketWriter _writer = new();
    private readonly QubicPacketReader _reader = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public QubicNodeClient(string host, int port = DefaultPort, int timeoutMs = DefaultTimeoutMs)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _port = port;
        _timeoutMs = timeoutMs;
    }

    /// <summary>
    /// Whether the client is currently connected.
    /// </summary>
    public bool IsConnected => _client?.Connected ?? false;

    /// <summary>
    /// Connects to the Qubic node.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
            return;

        _client = new TcpClient();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeoutMs);

        await _client.ConnectAsync(_host, _port, cts.Token);
        _stream = _client.GetStream();
    }

    /// <summary>
    /// Gets the current tick information from the node.
    /// </summary>
    public async Task<CurrentTickInfo> GetCurrentTickInfoAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        var requestPacket = _writer.WriteRequestCurrentTickInfo();
        var response = await SendAndReceiveAsync(requestPacket, QubicPacketTypes.RespondCurrentTickInfo, cancellationToken);

        return _reader.ReadCurrentTickInfo(response.AsSpan(QubicPacketHeader.Size));
    }

    /// <summary>
    /// Gets the balance for an identity.
    /// </summary>
    public async Task<QubicBalance> GetBalanceAsync(QubicIdentity identity, CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        var requestPacket = _writer.WriteRequestEntity(identity);
        var response = await SendAndReceiveAsync(requestPacket, QubicPacketTypes.RespondEntity, cancellationToken);

        return _reader.ReadEntityResponse(response.AsSpan(QubicPacketHeader.Size), identity);
    }

    /// <summary>
    /// Queries a smart contract function via direct TCP.
    /// Returns the raw output bytes. Throws if the invocation fails (empty response).
    /// </summary>
    public async Task<byte[]> QuerySmartContractAsync(uint contractIndex, uint inputType, byte[] requestData, CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        var requestPacket = _writer.WriteRequestContractFunction(contractIndex, (ushort)inputType, requestData);
        var response = await SendAndReceiveAsync(requestPacket, QubicPacketTypes.RespondContractFunction, cancellationToken);

        var header = _reader.ReadHeader(response);
        if (header.PayloadSize == 0)
            throw new InvalidOperationException("Contract function invocation failed (empty response).");

        return response.AsSpan(QubicPacketHeader.Size, header.PayloadSize).ToArray();
    }

    /// <summary>
    /// Gets the peer list from the node via ExchangePublicPeers.
    /// Returns non-zero IPv4 addresses.
    /// </summary>
    public async Task<string[]> GetPeerListAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        var requestPacket = _writer.WriteExchangePublicPeers();
        var response = await SendAndReceiveAsync(requestPacket, QubicPacketTypes.ExchangePublicPeers, cancellationToken);

        return _reader.ReadExchangePublicPeers(response.AsSpan(QubicPacketHeader.Size));
    }

    /// <summary>
    /// Gets the IPO status for a contract (676 participant slots with bids).
    /// </summary>
    public async Task<ContractIpo> GetContractIpoAsync(uint contractIndex, CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        var requestPacket = _writer.WriteRequestContractIPO(contractIndex);
        var response = await SendAndReceiveAsync(requestPacket, QubicPacketTypes.RespondContractIPO, cancellationToken);

        return _reader.ReadContractIpoResponse(response.AsSpan(QubicPacketHeader.Size));
    }

    /// <summary>
    /// Sends a signed SpecialCommand to the node and returns the response payload.
    /// </summary>
    public async Task<byte[]> SendSpecialCommandAsync(byte[] commandPayload, byte[] signature, CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        var packet = _writer.WriteSpecialCommand(commandPayload, signature);
        var response = await SendAndReceiveAsync(packet, QubicPacketTypes.SpecialCommand, cancellationToken);

        var header = _reader.ReadHeader(response);
        return response.AsSpan(QubicPacketHeader.Size, header.PayloadSize).ToArray();
    }

    /// <summary>
    /// Sends raw bytes directly to the node TCP stream.
    /// </summary>
    public async Task SendRawPacketAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        await _stream!.WriteAsync(data, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Broadcasts a signed transaction to the network.
    /// </summary>
    public async Task BroadcastTransactionAsync(QubicTransaction transaction, CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        if (!transaction.IsSigned)
            throw new InvalidOperationException("Transaction must be signed before broadcasting.");

        var packet = _writer.WriteBroadcastTransaction(transaction);
        await SendAsync(packet, cancellationToken);
    }

    private async Task<byte[]> SendAndReceiveAsync(byte[] packet, byte expectedType, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await SendAsync(packet, cancellationToken);
            return await ReceiveAsync(expectedType, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendAsync(byte[] packet, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await _stream!.WriteAsync(packet, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    private async Task<byte[]> ReceiveAsync(byte expectedType, CancellationToken cancellationToken)
    {
        EnsureConnected();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeoutMs);

        // Loop to skip unexpected packet types (e.g. broadcast messages from the node)
        while (true)
        {
            // Read header first
            var headerBuffer = new byte[QubicPacketHeader.Size];
            await ReadExactAsync(headerBuffer, cts.Token);

            var header = _reader.ReadHeader(headerBuffer);

            if (header.PacketSize > MaxPacketSize)
                throw new InvalidOperationException($"Packet too large: {header.PacketSize} bytes.");

            // Read full packet
            var packetBuffer = new byte[header.PacketSize];
            Array.Copy(headerBuffer, packetBuffer, QubicPacketHeader.Size);

            if (header.PayloadSize > 0)
            {
                await ReadExactAsync(packetBuffer.AsMemory(QubicPacketHeader.Size, header.PayloadSize), cts.Token);
            }

            // If this is the response we're waiting for, return it
            if (header.Type == expectedType)
            {
                return packetBuffer;
            }

            // Otherwise skip this packet and keep reading
            // (nodes may send broadcasts, peer exchange, etc.)
        }
    }

    private async Task ReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await _stream!.ReadAsync(buffer[totalRead..], cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("Connection closed by remote host.");
            totalRead += read;
        }
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
            throw new InvalidOperationException("Not connected. Call ConnectAsync first.");
    }

    /// <summary>
    /// Disconnects from the node.
    /// </summary>
    public void Disconnect()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
    }

    public void Dispose()
    {
        Disconnect();
        _sendLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Disconnect();
        _sendLock.Dispose();
        await Task.CompletedTask;
    }
}
