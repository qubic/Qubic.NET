using System.Diagnostics;
using System.Net.Sockets;
using Qubic.Core.Entities;
using Qubic.Serialization;
using Qubic.Serialization.Readers;
using Qubic.Serialization.Writers;

namespace Qubic.Network;

/// <summary>
/// Persistent TCP streaming client for Qubic network nodes with multi-node
/// failover, automatic reconnection, and broadcast listening.
/// </summary>
public sealed class QubicStreamClient : IAsyncDisposable, IDisposable
{
    private readonly QubicStreamOptions _options;
    private readonly List<QubicStreamNodeState> _nodes;
    private readonly QubicPacketWriter _writer = new();
    private readonly QubicPacketReader _reader = new();
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private QubicStreamNodeState? _activeNode;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private Task? _healthCheckLoop;
    private int _reconnectAttempts;
    private bool _disposed;

    // Pending request state (only one at a time, guarded by _requestLock)
    private volatile PendingRequest? _pendingRequest;

    /// <summary>
    /// Current connection state.
    /// </summary>
    public QubicStreamConnectionState State { get; private set; }
        = QubicStreamConnectionState.Disconnected;

    /// <summary>
    /// The endpoint of the currently active node, or null if disconnected.
    /// </summary>
    public string? ActiveNodeEndpoint => _activeNode?.Endpoint;

    #region Events

    /// <summary>
    /// Raised for every packet received that is not a response to a pending request.
    /// </summary>
    public event EventHandler<QubicPacketReceivedEventArgs>? PacketReceived;

    /// <summary>
    /// Raised when a BroadcastMessage (type 1) is received.
    /// </summary>
    public event EventHandler<QubicPacketReceivedEventArgs>? BroadcastMessageReceived;

    /// <summary>
    /// Raised when a BroadcastTransaction (type 24) is received unsolicited.
    /// </summary>
    public event EventHandler<QubicPacketReceivedEventArgs>? BroadcastTransactionReceived;

    /// <summary>
    /// Raised when a BroadcastComputors (type 2) is received.
    /// </summary>
    public event EventHandler<QubicPacketReceivedEventArgs>? BroadcastComputorsReceived;

    /// <summary>
    /// Raised when a BroadcastTick (type 3) is received.
    /// </summary>
    public event EventHandler<QubicPacketReceivedEventArgs>? BroadcastTickReceived;

    #endregion

    public QubicStreamClient(QubicStreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Nodes is not { Length: > 0 })
            throw new ArgumentException("At least one node endpoint must be provided.", nameof(options));

        _options = options;
        _nodes = options.Nodes
            .Select(e => QubicStreamNodeState.Parse(e))
            .ToList();
    }

    #region Connection Lifecycle

    /// <summary>
    /// Connects to the best available node and starts the receive loop and health monitoring.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Probe all nodes to find the best one
        await ProbeAllNodesAsync(_cts.Token);

        var bestNode = SelectBestNode();
        if (bestNode is null)
            throw new InvalidOperationException("No available Qubic nodes found.");

        await ConnectToNodeAsync(bestNode, _cts.Token);

        // Start background loops
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
        _healthCheckLoop = Task.Run(() => HealthCheckLoopAsync(_cts.Token), _cts.Token);
    }

    /// <summary>
    /// Disconnects from the current node and stops all background loops.
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (_cts is not null && !_cts.IsCancellationRequested)
        {
            await _cts.CancelAsync();
        }

        // Fail any pending request
        _pendingRequest?.Fail(new InvalidOperationException("Client disconnected."));
        _pendingRequest = null;

        await WaitForBackgroundLoopsAsync();

        CloseConnection();
        State = QubicStreamConnectionState.Disconnected;
    }

    private async Task ConnectToNodeAsync(QubicStreamNodeState node, CancellationToken cancellationToken)
    {
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            State = QubicStreamConnectionState.Connecting;
            EmitEvent(QubicStreamEventType.Connecting, $"Connecting to {node.Endpoint}", node.Endpoint);

            CloseConnection();
            _tcpClient = new TcpClient();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_options.RequestTimeout);

            await _tcpClient.ConnectAsync(node.Host, node.Port, cts.Token);
            _stream = _tcpClient.GetStream();

            // Send ExchangePublicPeers handshake so the node treats us as a peer
            // and starts sending broadcasts (BroadcastMessage, BroadcastTick, etc.)
            var handshake = _writer.WriteExchangePublicPeers();
            await _stream.WriteAsync(handshake, cts.Token);
            await _stream.FlushAsync(cts.Token);

            _activeNode = node;
            _reconnectAttempts = 0;
            State = QubicStreamConnectionState.Connected;
            EmitEvent(QubicStreamEventType.Connected, $"Connected to {node.Endpoint}", node.Endpoint);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        State = QubicStreamConnectionState.Reconnecting;

        // Fail any pending request
        _pendingRequest?.Fail(new InvalidOperationException("Connection lost during pending request."));
        _pendingRequest = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            _reconnectAttempts++;
            var delay = CalculateBackoff(_reconnectAttempts);

            EmitEvent(QubicStreamEventType.Reconnecting,
                $"Reconnecting in {delay.TotalSeconds:F0}s (attempt {_reconnectAttempts})",
                _activeNode?.Endpoint);

            await Task.Delay(delay, cancellationToken);

            await ProbeAllNodesAsync(cancellationToken);
            var bestNode = SelectBestNode();
            if (bestNode is null)
                continue;

            try
            {
                var previousEndpoint = _activeNode?.Endpoint;
                await ConnectToNodeAsync(bestNode, cancellationToken);

                if (previousEndpoint is not null && previousEndpoint != bestNode.Endpoint)
                {
                    EmitEvent(QubicStreamEventType.NodeSwitched,
                        $"Switched from {previousEndpoint} to {bestNode.Endpoint}",
                        bestNode.Endpoint);
                }

                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                EmitEvent(QubicStreamEventType.Error,
                    $"Reconnection to {bestNode.Endpoint} failed: {ex.Message}",
                    bestNode.Endpoint, ex);
            }
        }
    }

    #endregion

    #region Background Loops

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var headerBuffer = new byte[QubicPacketHeader.Size];

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                while (_stream is not null && !cancellationToken.IsCancellationRequested)
                {
                    // Read header
                    await ReadExactAsync(headerBuffer, cancellationToken);
                    var header = _reader.ReadHeader(headerBuffer);

                    if (header.PacketSize < QubicPacketHeader.Size || header.PacketSize > _options.MaxPacketSize)
                    {
                        // Skip invalid/oversized packet
                        if (header.PayloadSize > 0 && header.PayloadSize <= _options.MaxPacketSize)
                        {
                            var discard = new byte[header.PayloadSize];
                            await ReadExactAsync(discard, cancellationToken);
                        }
                        continue;
                    }

                    // Read full packet (header + payload)
                    var rawPacket = new byte[header.PacketSize];
                    Array.Copy(headerBuffer, rawPacket, QubicPacketHeader.Size);
                    if (header.PayloadSize > 0)
                    {
                        await ReadExactAsync(rawPacket.AsMemory(QubicPacketHeader.Size, header.PayloadSize), cancellationToken);
                    }

                    RoutePacket(header, rawPacket);
                }

                // Stream became null — reconnect if not cancelled
                if (!cancellationToken.IsCancellationRequested)
                {
                    EmitEvent(QubicStreamEventType.Disconnected, "TCP connection lost", _activeNode?.Endpoint);
                    await ReconnectAsync(cancellationToken);
                }
            }
            catch (EndOfStreamException)
            {
                if (cancellationToken.IsCancellationRequested) break;
                EmitEvent(QubicStreamEventType.Disconnected, "Connection closed by remote host", _activeNode?.Endpoint);
                await ReconnectAsync(cancellationToken);
            }
            catch (IOException ex) when (!cancellationToken.IsCancellationRequested)
            {
                EmitEvent(QubicStreamEventType.Disconnected, $"IO error: {ex.Message}", _activeNode?.Endpoint, ex);
                await ReconnectAsync(cancellationToken);
            }
            catch (SocketException ex) when (!cancellationToken.IsCancellationRequested)
            {
                EmitEvent(QubicStreamEventType.Disconnected, $"Socket error: {ex.Message}", _activeNode?.Endpoint, ex);
                await ReconnectAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task HealthCheckLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.HealthCheckInterval, cancellationToken);

                await ProbeAllNodesAsync(cancellationToken);
                await EvaluateNodeSwitchAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                EmitEvent(QubicStreamEventType.Error, $"Health check error: {ex.Message}", exception: ex);
            }
        }
    }

    #endregion

    #region Packet Routing

    private void RoutePacket(QubicPacketHeader header, byte[] rawPacket)
    {
        // Check if a pending request wants this packet
        var pending = _pendingRequest;
        if (pending is not null && pending.Matches(header.Type))
        {
            pending.Complete(header, rawPacket);
            return;
        }

        // Not a response to a pending request — treat as broadcast
        var args = new QubicPacketReceivedEventArgs
        {
            Header = header,
            RawPacket = rawPacket
        };

        try { PacketReceived?.Invoke(this, args); } catch { /* don't crash receive loop */ }

        try
        {
            switch (header.Type)
            {
                case QubicPacketTypes.BroadcastMessage:
                    BroadcastMessageReceived?.Invoke(this, args);
                    break;
                case QubicPacketTypes.BroadcastTransaction:
                    BroadcastTransactionReceived?.Invoke(this, args);
                    break;
                case QubicPacketTypes.BroadcastComputors:
                    BroadcastComputorsReceived?.Invoke(this, args);
                    break;
                case QubicPacketTypes.BroadcastTick:
                    BroadcastTickReceived?.Invoke(this, args);
                    break;
            }
        }
        catch { /* don't crash receive loop */ }
    }

    #endregion

    #region Request-Response API

    /// <summary>
    /// Gets the current tick information from the node.
    /// </summary>
    public async Task<CurrentTickInfo> GetCurrentTickInfoAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var packet = _writer.WriteRequestCurrentTickInfo();
        var response = await SendAndReceiveAsync(packet, QubicPacketTypes.RespondCurrentTickInfo, cancellationToken);
        return _reader.ReadCurrentTickInfo(response.AsSpan(QubicPacketHeader.Size));
    }

    /// <summary>
    /// Gets the balance for an identity.
    /// </summary>
    public async Task<QubicBalance> GetBalanceAsync(QubicIdentity identity, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var packet = _writer.WriteRequestEntity(identity);
        var response = await SendAndReceiveAsync(packet, QubicPacketTypes.RespondEntity, cancellationToken);
        return _reader.ReadEntityResponse(response.AsSpan(QubicPacketHeader.Size), identity);
    }

    /// <summary>
    /// Queries a smart contract function. Returns the raw output bytes.
    /// </summary>
    public async Task<byte[]> QuerySmartContractAsync(uint contractIndex, uint inputType, byte[] requestData, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var packet = _writer.WriteRequestContractFunction(contractIndex, (ushort)inputType, requestData);
        var response = await SendAndReceiveAsync(packet, QubicPacketTypes.RespondContractFunction, cancellationToken);
        var header = _reader.ReadHeader(response);
        if (header.PayloadSize == 0)
            throw new InvalidOperationException("Contract function invocation failed (empty response).");
        return response.AsSpan(QubicPacketHeader.Size, header.PayloadSize).ToArray();
    }

    /// <summary>
    /// Gets the peer list from the node via ExchangePublicPeers.
    /// </summary>
    public async Task<string[]> GetPeerListAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var packet = _writer.WriteExchangePublicPeers();
        var response = await SendAndReceiveAsync(packet, QubicPacketTypes.ExchangePublicPeers, cancellationToken);
        return _reader.ReadExchangePublicPeers(response.AsSpan(QubicPacketHeader.Size));
    }

    /// <summary>
    /// Gets the IPO status for a contract.
    /// </summary>
    public async Task<ContractIpo> GetContractIpoAsync(uint contractIndex, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var packet = _writer.WriteRequestContractIPO(contractIndex);
        var response = await SendAndReceiveAsync(packet, QubicPacketTypes.RespondContractIPO, cancellationToken);
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
        await SendAsync(data, cancellationToken);
    }

    /// <summary>
    /// Requests all transactions for a given tick.
    /// </summary>
    public async Task<List<byte[]>> GetTickTransactionsAsync(uint tick, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var packet = _writer.WriteRequestTickTransactions(tick);
        return await SendAndReceiveMultiAsync(packet, QubicPacketTypes.BroadcastTransaction, QubicPacketTypes.EndResponse, cancellationToken);
    }

    /// <summary>
    /// Requests oracle query data by query ID.
    /// </summary>
    public async Task<List<byte[]>> GetOracleQueryAsync(long queryId, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var packet = _writer.WriteRequestOracleData(5, queryId);
        return await SendAndReceiveMultiAsync(packet, QubicPacketTypes.RespondOracleData, QubicPacketTypes.EndResponse, cancellationToken);
    }

    /// <summary>
    /// Requests oracle query IDs by tick.
    /// </summary>
    public async Task<List<byte[]>> GetOracleQueryIdsByTickAsync(uint tick, uint filterType = 0, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var packet = _writer.WriteRequestOracleData(filterType, tick);
        return await SendAndReceiveMultiAsync(packet, QubicPacketTypes.RespondOracleData, QubicPacketTypes.EndResponse, cancellationToken);
    }

    /// <summary>
    /// Requests oracle statistics.
    /// </summary>
    public async Task<List<byte[]>> GetOracleStatisticsAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var packet = _writer.WriteRequestOracleData(7, 0);
        return await SendAndReceiveMultiAsync(packet, QubicPacketTypes.RespondOracleData, QubicPacketTypes.EndResponse, cancellationToken);
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

    #endregion

    #region Internal Send/Receive

    private async Task<byte[]> SendAndReceiveAsync(byte[] packet, byte expectedType, CancellationToken cancellationToken)
    {
        await _requestLock.WaitAsync(cancellationToken);
        try
        {
            var request = new SingleResponseRequest(expectedType);
            _pendingRequest = request;

            try
            {
                await SendAsync(packet, cancellationToken);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(_options.RequestTimeout);

                return await request.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Request timed out waiting for response type {expectedType}.");
            }
            finally
            {
                _pendingRequest = null;
            }
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async Task<List<byte[]>> SendAndReceiveMultiAsync(byte[] packet, byte expectedType, byte endType, CancellationToken cancellationToken)
    {
        await _requestLock.WaitAsync(cancellationToken);
        try
        {
            var request = new MultiResponseRequest(expectedType, endType);
            _pendingRequest = request;

            try
            {
                await SendAsync(packet, cancellationToken);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(_options.MultiResponseTimeout);

                return await request.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Multi-response request timed out waiting for end type {endType}.");
            }
            finally
            {
                _pendingRequest = null;
            }
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async Task SendAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (_stream is null)
            throw new InvalidOperationException("Not connected.");
        await _stream.WriteAsync(packet, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
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

    #endregion

    #region Health & Node Selection

    private async Task ProbeAllNodesAsync(CancellationToken cancellationToken)
    {
        var tasks = _nodes.Select(n => ProbeNodeAsync(n, cancellationToken));
        await Task.WhenAll(tasks);
    }

    private async Task ProbeNodeAsync(QubicStreamNodeState node, CancellationToken cancellationToken)
    {
        try
        {
            var sw = Stopwatch.StartNew();

            using var probe = new QubicNodeClient(node.Host, node.Port,
                timeoutMs: (int)_options.RequestTimeout.TotalMilliseconds);
            await probe.ConnectAsync(cancellationToken);
            var tickInfo = await probe.GetCurrentTickInfoAsync(cancellationToken);

            sw.Stop();

            node.CurrentTick = tickInfo.Tick;
            node.CurrentEpoch = tickInfo.Epoch;
            node.Latency = sw.Elapsed;
            node.LastHealthCheckUtc = DateTime.UtcNow;
            node.ConsecutiveFailures = 0;

            if (!node.IsAvailable)
            {
                node.IsAvailable = true;
                EmitEvent(QubicStreamEventType.NodeRecovered,
                    $"Node {node.Endpoint} recovered (tick {node.CurrentTick})", node.Endpoint);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            node.ConsecutiveFailures++;
            if (node.ConsecutiveFailures >= _options.FailureThreshold && node.IsAvailable)
            {
                node.IsAvailable = false;
                EmitEvent(QubicStreamEventType.NodeMarkedUnavailable,
                    $"Node {node.Endpoint} marked unavailable after {node.ConsecutiveFailures} failures",
                    node.Endpoint, ex);
            }
        }
    }

    private QubicStreamNodeState? SelectBestNode()
    {
        return _nodes
            .Where(n => n.IsAvailable)
            .OrderByDescending(n => n.CurrentTick)
            .ThenBy(n => n.Latency)
            .FirstOrDefault();
    }

    private Task EvaluateNodeSwitchAsync(CancellationToken cancellationToken)
    {
        if (_activeNode is null || State != QubicStreamConnectionState.Connected)
            return Task.CompletedTask;

        var bestNode = SelectBestNode();
        if (bestNode is null || bestNode == _activeNode)
            return Task.CompletedTask;

        // Switch if the best node is significantly ahead
        if (bestNode.CurrentTick > _activeNode.CurrentTick + _options.SwitchThresholdTicks)
        {
            var previousEndpoint = _activeNode.Endpoint;

            // Close current connection — the receive loop will detect the closure and reconnect
            CloseConnection();

            EmitEvent(QubicStreamEventType.NodeSwitched,
                $"Switching from {previousEndpoint} to {bestNode.Endpoint} (tick gap: {bestNode.CurrentTick - _activeNode.CurrentTick})",
                bestNode.Endpoint);
        }

        return Task.CompletedTask;
    }

    #endregion

    #region Helpers

    private TimeSpan CalculateBackoff(int attempt)
    {
        var delayMs = _options.ReconnectDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var capped = Math.Min(delayMs, _options.MaxReconnectDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(capped);
    }

    private void EmitEvent(QubicStreamEventType type, string message,
        string? nodeEndpoint = null, Exception? exception = null)
    {
        try
        {
            _options.OnConnectionEvent?.Invoke(new QubicStreamEvent
            {
                Type = type,
                Message = message,
                NodeEndpoint = nodeEndpoint,
                Exception = exception
            });
        }
        catch { /* don't let callback errors crash us */ }
    }

    private void EnsureConnected()
    {
        if (State != QubicStreamConnectionState.Connected || _stream is null)
            throw new InvalidOperationException("Not connected. Call ConnectAsync first.");
    }

    private void CloseConnection()
    {
        _stream?.Dispose();
        _stream = null;
        _tcpClient?.Dispose();
        _tcpClient = null;
    }

    private async Task WaitForBackgroundLoopsAsync()
    {
        try
        {
            if (_receiveLoop is not null)
                await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch { /* shutting down */ }

        try
        {
            if (_healthCheckLoop is not null)
                await _healthCheckLoop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch { /* shutting down */ }
    }

    #endregion

    #region Disposal

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_cts is not null && !_cts.IsCancellationRequested)
        {
            await _cts.CancelAsync();
        }

        _pendingRequest?.Fail(new ObjectDisposedException(nameof(QubicStreamClient)));
        _pendingRequest = null;

        await WaitForBackgroundLoopsAsync();

        CloseConnection();
        _requestLock.Dispose();
        _connectLock.Dispose();
        _cts?.Dispose();

        State = QubicStreamConnectionState.Disconnected;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();

        _pendingRequest?.Fail(new ObjectDisposedException(nameof(QubicStreamClient)));
        _pendingRequest = null;

        CloseConnection();
        _requestLock.Dispose();
        _connectLock.Dispose();
        _cts?.Dispose();

        State = QubicStreamConnectionState.Disconnected;
    }

    #endregion

    #region Pending Request Types

    private abstract class PendingRequest
    {
        public abstract bool Matches(byte packetType);
        public abstract void Complete(QubicPacketHeader header, byte[] rawPacket);
        public abstract void Fail(Exception ex);
    }

    private sealed class SingleResponseRequest : PendingRequest
    {
        private readonly byte _expectedType;
        private readonly TaskCompletionSource<byte[]> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SingleResponseRequest(byte expectedType) => _expectedType = expectedType;

        public Task<byte[]> Task => _tcs.Task;

        public override bool Matches(byte packetType) => packetType == _expectedType;

        public override void Complete(QubicPacketHeader header, byte[] rawPacket)
            => _tcs.TrySetResult(rawPacket);

        public override void Fail(Exception ex)
            => _tcs.TrySetException(ex);
    }

    private sealed class MultiResponseRequest : PendingRequest
    {
        private readonly byte _expectedType;
        private readonly byte _endType;
        private readonly TaskCompletionSource<List<byte[]>> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<byte[]> _results = new();

        public MultiResponseRequest(byte expectedType, byte endType)
        {
            _expectedType = expectedType;
            _endType = endType;
        }

        public Task<List<byte[]>> Task => _tcs.Task;

        public override bool Matches(byte packetType)
            => packetType == _expectedType || packetType == _endType;

        public override void Complete(QubicPacketHeader header, byte[] rawPacket)
        {
            if (header.Type == _endType)
            {
                _tcs.TrySetResult(_results);
            }
            else if (header.PayloadSize > 0)
            {
                _results.Add(rawPacket.AsSpan(QubicPacketHeader.Size, header.PayloadSize).ToArray());
            }
        }

        public override void Fail(Exception ex)
            => _tcs.TrySetException(ex);
    }

    #endregion
}
