using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Qubic.Serialization;

namespace Qubic.Network.Tests;

/// <summary>
/// A mock Qubic node server for testing network operations.
/// Simulates the Qubic protocol including the peer exchange handshake.
/// </summary>
internal sealed class MockQubicServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptTask;
    private readonly Dictionary<byte, Func<byte[], byte[]>> _handlers = new();
    private readonly ConcurrentBag<NetworkStream> _connectedStreams = new();

    public int Port { get; }

    /// <summary>
    /// Number of currently tracked connected streams.
    /// </summary>
    public int ConnectedClientCount => _connectedStreams.Count;

    public MockQubicServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    /// <summary>
    /// Registers a handler for a specific request type.
    /// </summary>
    public void OnRequest(byte requestType, Func<byte[], byte[]> responseGenerator)
    {
        _handlers[requestType] = responseGenerator;
    }

    /// <summary>
    /// Sends a raw packet to all connected clients (unsolicited broadcast).
    /// </summary>
    public async Task SendBroadcastAsync(byte[] packet)
    {
        foreach (var stream in _connectedStreams)
        {
            try
            {
                await stream.WriteAsync(packet);
                await stream.FlushAsync();
            }
            catch { /* client may have disconnected */ }
        }
    }

    /// <summary>
    /// Starts accepting connections.
    /// </summary>
    public void Start()
    {
        _acceptTask = AcceptClientsAsync(_cts.Token);
    }

    private async Task AcceptClientsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = HandleClientAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        NetworkStream? stream = null;
        try
        {
            stream = client.GetStream();
            _connectedStreams.Add(stream);

            // Send ExchangePublicPeers immediately on connection (like a real Qubic node)
            var exchangePacket = MockResponseBuilder.CreateExchangePublicPeersPacket();
            await stream.WriteAsync(exchangePacket, cancellationToken);
            await stream.FlushAsync(cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                // Read header (8 bytes)
                var header = new byte[QubicPacketHeader.Size];
                var bytesRead = await ReadExactAsync(stream, header, cancellationToken);
                if (bytesRead == 0)
                    break;

                // Parse header
                var sizeAndType = BinaryPrimitives.ReadUInt32LittleEndian(header);
                var packetSize = (int)(sizeAndType & 0x00FFFFFF);
                var requestType = (byte)(sizeAndType >> 24);

                // Read payload if any
                var payloadSize = packetSize - QubicPacketHeader.Size;
                var payload = new byte[payloadSize];
                if (payloadSize > 0)
                {
                    await ReadExactAsync(stream, payload, cancellationToken);
                }

                // Generate response
                if (_handlers.TryGetValue(requestType, out var handler))
                {
                    var response = handler(payload);
                    await stream.WriteAsync(response, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
                // ExchangePublicPeers from client is silently consumed (no response needed)
            }
        }
        catch (Exception)
        {
            // Client disconnected or error
        }
        finally
        {
            stream?.Dispose();
            client.Dispose();
        }
    }

    private static async Task<int> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead), cancellationToken);
            if (read == 0)
                return 0;
            totalRead += read;
        }
        return totalRead;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();

        if (_acceptTask != null)
        {
            try
            {
                await _acceptTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        _cts.Dispose();
    }
}

/// <summary>
/// Helper class to build mock Qubic protocol responses.
/// </summary>
internal static class MockResponseBuilder
{
    /// <summary>
    /// Creates an ExchangePublicPeers packet (type 0) with 4 empty peer addresses.
    /// This is sent by the node immediately after accepting a connection.
    /// </summary>
    public static byte[] CreateExchangePublicPeersPacket(byte[][]? peerIPs = null)
    {
        // Payload: 4 IPv4 addresses × 4 bytes = 16 bytes
        var payload = new byte[16];
        if (peerIPs != null)
        {
            for (int i = 0; i < Math.Min(4, peerIPs.Length); i++)
            {
                if (peerIPs[i].Length == 4)
                    Array.Copy(peerIPs[i], 0, payload, i * 4, 4);
            }
        }
        return CreatePacket(QubicPacketTypes.ExchangePublicPeers, payload);
    }

    /// <summary>
    /// Creates a CurrentTickInfo response packet.
    /// </summary>
    public static byte[] CreateCurrentTickInfoResponse(
        ushort tickDuration,
        ushort epoch,
        uint tick,
        ushort alignedVotes,
        ushort misalignedVotes,
        uint initialTick)
    {
        // Payload: 16 bytes
        var payload = new byte[16];
        var offset = 0;

        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset), tickDuration);
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset), epoch);
        offset += 2;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset), tick);
        offset += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset), alignedVotes);
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset), misalignedVotes);
        offset += 2;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset), initialTick);

        return CreatePacket(QubicPacketTypes.RespondCurrentTickInfo, payload);
    }

    /// <summary>
    /// Creates an Entity (balance) response packet.
    /// </summary>
    public static byte[] CreateEntityResponse(
        byte[] publicKey,
        long incomingAmount,
        long outgoingAmount,
        uint incomingTransfers,
        uint outgoingTransfers)
    {
        // Payload: 32 (pubkey) + 8 + 8 + 4 + 4 = 56 bytes
        var payload = new byte[56];
        var offset = 0;

        // Echo public key
        Array.Copy(publicKey, 0, payload, offset, 32);
        offset += 32;

        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset), incomingAmount);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset), outgoingAmount);
        offset += 8;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset), incomingTransfers);
        offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset), outgoingTransfers);

        return CreatePacket(QubicPacketTypes.RespondEntity, payload);
    }

    /// <summary>
    /// Creates a raw packet with header and payload.
    /// </summary>
    public static byte[] CreatePacket(byte type, byte[] payload)
    {
        var packetSize = QubicPacketHeader.Size + payload.Length;
        var packet = new byte[packetSize];

        // Write header
        uint sizeAndType = (uint)packetSize | ((uint)type << 24);
        BinaryPrimitives.WriteUInt32LittleEndian(packet, sizeAndType);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 0); // Dejavu

        // Write payload
        Array.Copy(payload, 0, packet, QubicPacketHeader.Size, payload.Length);

        return packet;
    }
}
