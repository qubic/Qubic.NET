using System.Buffers.Binary;
using System.Net.Sockets;

namespace Qubic.NodeTester;

/// <summary>
/// Passively listens to a Qubic node TCP stream for a fixed window and
/// tallies incoming packet types. Uses its own dedicated TCP connection so it
/// doesn't interfere with the main test client.
/// </summary>
public static class BroadcastListener
{
    public static async Task<IReadOnlyDictionary<byte, int>> ListenAsync(
        string host, int port, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        using var tcp = new TcpClient();
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(TimeSpan.FromSeconds(5));
        await tcp.ConnectAsync(host, port, connectCts.Token).ConfigureAwait(false);
        var stream = tcp.GetStream();

        // Identify as a peer so the node starts pushing broadcasts (BroadcastTick,
        // BroadcastTransaction, ...) to our connection. Without this the node only
        // sends its own initial ExchangePublicPeers and then stays silent.
        await stream.WriteAsync(BuildExchangePublicPeers(), connectCts.Token).ConfigureAwait(false);
        await stream.FlushAsync(connectCts.Token).ConfigureAwait(false);

        using var listenCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        listenCts.CancelAfter(duration);

        var counts = new Dictionary<byte, int>();
        try
        {
            while (true)
            {
                var header = new byte[8];
                await ReadExact(stream, header, listenCts.Token).ConfigureAwait(false);
                var sizeAndType = BinaryPrimitives.ReadUInt32LittleEndian(header);
                var size = (int)(sizeAndType & 0x00FFFFFF);
                var type = (byte)(sizeAndType >> 24);
                var payloadSize = size - 8;
                if (payloadSize > 0)
                {
                    var payload = new byte[payloadSize];
                    await ReadExact(stream, payload, listenCts.Token).ConfigureAwait(false);
                }
                counts[type] = counts.GetValueOrDefault(type, 0) + 1;
            }
        }
        catch (OperationCanceledException) when (listenCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // window expired — normal
        }

        return counts;
    }

    private static byte[] BuildExchangePublicPeers()
    {
        // 8-byte header + 16-byte payload (4 IPv4 placeholders, all zero).
        var packet = new byte[24];
        var sizeAndType = (uint)24 | ((uint)0 << 24); // type 0 = ExchangePublicPeers
        BinaryPrimitives.WriteUInt32LittleEndian(packet, sizeAndType);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)Random.Shared.Next(1, int.MaxValue));
        return packet;
    }

    private static async Task ReadExact(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("Connection closed.");
            offset += n;
        }
    }
}
