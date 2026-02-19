using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Qubic.Bob.Models;

namespace Qubic.Bob;

/// <summary>
/// WebSocket JSON-RPC client for QubicBob with multi-node failover,
/// automatic reconnection, and managed subscriptions.
/// </summary>
public sealed class BobWebSocketClient : IAsyncDisposable, IDisposable
{
    private readonly BobWebSocketOptions _options;
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly List<BobNodeState> _nodes;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement?>> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, SubscriptionEntry> _activeSubscriptions = new();
    private readonly ConcurrentDictionary<int, SubscriptionEntry> _pendingSubscriptionEntries = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private ClientWebSocket? _webSocket;
    private BobNodeState? _activeNode;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private Task? _healthCheckLoop;
    private int _requestId;
    private int _scQueryNonce = (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() & 0x7FFFFFFF);
    private int _reconnectAttempts;
    private bool _disposed;

    /// <summary>
    /// Current connection state.
    /// </summary>
    public BobConnectionState State { get; private set; } = BobConnectionState.Disconnected;

    /// <summary>
    /// The base URL of the currently active node, or null if disconnected.
    /// </summary>
    public string? ActiveNodeUrl => _activeNode?.BaseUrl;

    public BobWebSocketClient(BobWebSocketOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Nodes is not { Length: > 0 })
            throw new ArgumentException("At least one node URL must be provided.", nameof(options));

        _options = options;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        _nodes = options.Nodes
            .Select(url => new BobNodeState { BaseUrl = url.TrimEnd('/') })
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
            throw new InvalidOperationException("No available Bob nodes found.");

        await ConnectToNodeAsync(bestNode, _cts.Token);

        // Start background loops
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
        _healthCheckLoop = Task.Run(() => HealthCheckLoopAsync(_cts.Token), _cts.Token);
    }

    private async Task ConnectToNodeAsync(BobNodeState node, CancellationToken cancellationToken)
    {
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            State = BobConnectionState.Connecting;
            EmitEvent(BobConnectionEventType.Connecting, $"Connecting to {node.BaseUrl}", node.BaseUrl);

            _webSocket?.Dispose();
            _webSocket = new ClientWebSocket();

            var wsUrl = node.GetWebSocketUrl(_options.WebSocketPath);
            await _webSocket.ConnectAsync(new Uri(wsUrl), cancellationToken);

            _activeNode = node;
            _reconnectAttempts = 0;
            State = BobConnectionState.Connected;
            EmitEvent(BobConnectionEventType.Connected, $"Connected to {node.BaseUrl}", node.BaseUrl);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        State = BobConnectionState.Reconnecting;

        // Mark all subscriptions as disconnected
        foreach (var entry in _activeSubscriptions.Values)
            entry.Subscription.OnDisconnected();

        // Fail all pending requests
        foreach (var pending in _pendingRequests)
        {
            if (_pendingRequests.TryRemove(pending.Key, out var tcs))
                tcs.TrySetCanceled(cancellationToken);
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            _reconnectAttempts++;
            var delay = CalculateBackoff(_reconnectAttempts);

            EmitEvent(BobConnectionEventType.Reconnecting,
                $"Reconnecting in {delay.TotalSeconds:F0}s (attempt {_reconnectAttempts})",
                _activeNode?.BaseUrl);

            await Task.Delay(delay, cancellationToken);

            // Re-probe to find best node
            await ProbeAllNodesAsync(cancellationToken);
            var bestNode = SelectBestNode();

            if (bestNode is null)
                continue;

            try
            {
                var previousNode = _activeNode?.BaseUrl;
                await ConnectToNodeAsync(bestNode, cancellationToken);

                if (previousNode != bestNode.BaseUrl)
                {
                    EmitEvent(BobConnectionEventType.NodeSwitched,
                        $"Switched from {previousNode} to {bestNode.BaseUrl}",
                        bestNode.BaseUrl);
                }

                // Start a temporary receive pump so SendRequestCoreAsync can get responses
                // during resubscription (the main receive loop is blocked waiting for us to return).
                using var resubCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var tempReceiveTask = Task.Run(() => TempReceivePumpAsync(resubCts.Token), resubCts.Token);

                try
                {
                    await ResubscribeAllAsync(cancellationToken);
                }
                finally
                {
                    resubCts.Cancel();
                    try { await tempReceiveTask; } catch (OperationCanceledException) { }
                }

                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                EmitEvent(BobConnectionEventType.Error,
                    $"Reconnection to {bestNode.BaseUrl} failed: {ex.Message}",
                    bestNode.BaseUrl, ex);
            }
        }
    }

    private async Task ResubscribeAllAsync(CancellationToken cancellationToken)
    {
        foreach (var entry in _activeSubscriptions.Values)
        {
            if (entry.Subscription.CancellationToken.IsCancellationRequested)
                continue;

            try
            {
                var resubParams = entry.Subscription.ResubscribeParamsFactory();
                var subscriptionId = await SendSubscribeAsync(
                    entry.Subscription.SubscriptionType,
                    resubParams,
                    entry,
                    cancellationToken);

                // Update the subscription's server ID and re-register in the lookup
                var oldId = entry.Subscription.ServerSubscriptionId;
                entry.Subscription.ServerSubscriptionId = subscriptionId;

                if (oldId is not null)
                    _activeSubscriptions.TryRemove(oldId, out _);

                if (subscriptionId is not null)
                    _activeSubscriptions[subscriptionId] = entry;

                EmitEvent(BobConnectionEventType.SubscriptionRestored,
                    $"Restored {entry.Subscription.SubscriptionType} subscription as {subscriptionId}",
                    _activeNode?.BaseUrl);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                EmitEvent(BobConnectionEventType.Error,
                    $"Failed to restore {entry.Subscription.SubscriptionType} subscription: {ex.Message}",
                    _activeNode?.BaseUrl, ex);
            }
        }
    }

    #endregion

    #region Health Monitoring

    private async Task HealthCheckLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(_options.HealthCheckInterval, cancellationToken);

            try
            {
                await ProbeAllNodesAsync(cancellationToken);
                await EvaluateNodeSwitchAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                EmitEvent(BobConnectionEventType.Error,
                    $"Health check error: {ex.Message}",
                    exception: ex);
            }
        }
    }

    private async Task ProbeAllNodesAsync(CancellationToken cancellationToken)
    {
        var tasks = _nodes.Select(node => ProbeNodeAsync(node, cancellationToken));
        await Task.WhenAll(tasks);
    }

    private async Task ProbeNodeAsync(BobNodeState node, CancellationToken cancellationToken)
    {
        var httpUrl = node.GetHttpUrl(_options.HttpRpcPath);

        try
        {
            var stopwatch = Stopwatch.StartNew();

            var request = new JsonRpcRequest
            {
                Id = Interlocked.Increment(ref _requestId),
                Method = "qubic_syncing"
            };

            var response = await _httpClient.PostAsJsonAsync(httpUrl, request, _jsonOptions, cancellationToken);
            response.EnsureSuccessStatusCode();

            var rpcResponse = await response.Content.ReadFromJsonAsync<JsonRpcResponse<SyncStatusResponse>>(
                _jsonOptions, cancellationToken);

            stopwatch.Stop();

            if (rpcResponse?.Result is { } syncStatus)
            {
                node.LastVerifyLoggingTick = syncStatus.CurrentVerifyLoggingTick > 0
                    ? syncStatus.CurrentVerifyLoggingTick
                    : syncStatus.CurrentFetchingTick;
                node.LastSeenNetworkTick = syncStatus.LastSeenNetworkTick > 0
                    ? syncStatus.LastSeenNetworkTick
                    : syncStatus.CurrentFetchingTick;
                node.Latency = stopwatch.Elapsed;
                node.LastHealthCheckUtc = DateTime.UtcNow;
                node.ConsecutiveFailures = 0;

                if (!node.IsAvailable)
                {
                    node.IsAvailable = true;
                    EmitEvent(BobConnectionEventType.NodeRecovered,
                        $"Node {node.BaseUrl} recovered (tick {node.LastVerifyLoggingTick})",
                        node.BaseUrl);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            node.ConsecutiveFailures++;

            if (node.ConsecutiveFailures >= _options.FailureThreshold && node.IsAvailable)
            {
                node.IsAvailable = false;
                EmitEvent(BobConnectionEventType.NodeMarkedUnavailable,
                    $"Node {node.BaseUrl} marked unavailable after {node.ConsecutiveFailures} failures",
                    node.BaseUrl, ex);
            }
        }
    }

    private Task EvaluateNodeSwitchAsync(CancellationToken cancellationToken)
    {
        if (_activeNode is null || State != BobConnectionState.Connected)
            return Task.CompletedTask;

        var bestNode = SelectBestNode();
        if (bestNode is null || bestNode == _activeNode)
            return Task.CompletedTask;

        // Only switch if the active node is significantly behind
        if (bestNode.LastVerifyLoggingTick <= _activeNode.LastVerifyLoggingTick + _options.SwitchThresholdTicks)
            return Task.CompletedTask;

        // Active node is falling behind — switch
        EmitEvent(BobConnectionEventType.NodeSwitched,
            $"Active node {_activeNode.BaseUrl} (tick {_activeNode.LastVerifyLoggingTick}) " +
            $"is behind best node {bestNode.BaseUrl} (tick {bestNode.LastVerifyLoggingTick}) " +
            $"by {bestNode.LastVerifyLoggingTick - _activeNode.LastVerifyLoggingTick} ticks",
            bestNode.BaseUrl);

        // Force a reconnect to the better node
        _webSocket?.Abort();
        return Task.CompletedTask;
    }

    private BobNodeState? SelectBestNode()
    {
        return _nodes
            .Where(n => n.IsAvailable)
            .OrderByDescending(n => n.LastVerifyLoggingTick)
            .ThenBy(n => n.Latency)
            .FirstOrDefault();
    }

    #endregion

    #region WebSocket Receive Loop

    /// <summary>
    /// Temporary receive pump used during resubscription after reconnect.
    /// The main receive loop is blocked inside ReconnectAsync, so we need this
    /// to read responses from the new WebSocket while SendRequestCoreAsync awaits.
    /// </summary>
    private async Task TempReceivePumpAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var messageBuilder = new StringBuilder();

        try
        {
            while (_webSocket?.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await _webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (result.EndOfMessage)
                {
                    var message = messageBuilder.ToString();
                    messageBuilder.Clear();
                    ProcessMessage(message);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 1024]; // 1MB buffer
        var messageBuilder = new StringBuilder();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                while (_webSocket?.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    var result = await _webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer), cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                    if (result.EndOfMessage)
                    {
                        var message = messageBuilder.ToString();
                        messageBuilder.Clear();
                        ProcessMessage(message);
                    }
                }

                // Connection lost — reconnect
                EmitEvent(BobConnectionEventType.Disconnected,
                    "WebSocket connection lost", _activeNode?.BaseUrl);

                await ReconnectAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (WebSocketException ex)
            {
                EmitEvent(BobConnectionEventType.Disconnected,
                    $"WebSocket error: {ex.Message}", _activeNode?.BaseUrl, ex);

                await ReconnectAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                EmitEvent(BobConnectionEventType.Error,
                    $"Unexpected error in receive loop: {ex.Message}",
                    _activeNode?.BaseUrl, ex);

                await ReconnectAsync(cancellationToken);
            }
        }
    }

    private void ProcessMessage(string message)
    {
        JsonRpcMessage? msg;
        try
        {
            msg = JsonSerializer.Deserialize<JsonRpcMessage>(message, _jsonOptions);
        }
        catch (JsonException)
        {
            // Malformed JSON-RPC envelope — skip
            EmitEvent(BobConnectionEventType.Error,
                $"Failed to parse JSON-RPC message ({message.Length} chars)",
                _activeNode?.BaseUrl);
            return;
        }

        if (msg is null) return;

        if (msg.IsResponse)
        {
            // Response to a pending request
            if (_pendingRequests.TryRemove(msg.Id!.Value, out var tcs))
            {
                if (msg.Error is not null)
                {
                    _pendingSubscriptionEntries.TryRemove(msg.Id.Value, out _);
                    tcs.TrySetException(new BobRpcException(msg.Error.Code, msg.Error.Message));
                }
                else
                {
                    // If this is a subscribe response, register the entry BEFORE resolving the TCS
                    // so notifications arriving immediately after are dispatched correctly.
                    if (_pendingSubscriptionEntries.TryRemove(msg.Id.Value, out var entry) &&
                        msg.Result.HasValue && msg.Result.Value.ValueKind == JsonValueKind.String)
                    {
                        var subId = msg.Result.Value.GetString();
                        if (subId is not null)
                            _activeSubscriptions[subId] = entry;
                    }

                    tcs.TrySetResult(msg.Result);
                }
            }
        }
        else if (msg.IsNotification)
        {
            // Subscription notification
            var subscriptionId = msg.Params!.Subscription!;

            if (_activeSubscriptions.TryGetValue(subscriptionId, out var entry))
            {
                try
                {
                    entry.DispatchNotification(msg.Params.Result!.Value);
                }
                catch (Exception ex)
                {
                    EmitEvent(BobConnectionEventType.Error,
                        $"Dispatch error for subscription {subscriptionId}: {ex.Message}",
                        _activeNode?.BaseUrl, ex);
                }
            }
            else
            {
                EmitEvent(BobConnectionEventType.Error,
                    $"Notification for unknown subscription: {subscriptionId}",
                    _activeNode?.BaseUrl);
            }
        }
    }

    #endregion

    #region JSON-RPC Send

    private async Task<string?> SendSubscribeAsync(
        string subscriptionType,
        object[] subscribeParams,
        SubscriptionEntry entry,
        CancellationToken cancellationToken)
    {
        var result = await SendRequestWithEntryAsync("qubic_subscribe", subscribeParams, entry, cancellationToken);

        // The result should be the subscription ID string
        if (result.HasValue && result.Value.ValueKind == JsonValueKind.String)
            return result.Value.GetString();

        return result?.ToString();
    }

    private async Task<bool> SendUnsubscribeAsync(string subscriptionId, CancellationToken cancellationToken)
    {
        var result = await SendRequestAsync("qubic_unsubscribe",
            new object[] { subscriptionId }, cancellationToken);

        return result.HasValue && result.Value.ValueKind == JsonValueKind.True;
    }

    private Task<JsonElement?> SendRequestAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken)
        => SendRequestCoreAsync(method, parameters, null, cancellationToken);

    private Task<JsonElement?> SendRequestWithEntryAsync(
        string method,
        object? parameters,
        SubscriptionEntry entry,
        CancellationToken cancellationToken)
        => SendRequestCoreAsync(method, parameters, entry, cancellationToken);

    private async Task<JsonElement?> SendRequestCoreAsync(
        string method,
        object? parameters,
        SubscriptionEntry? subscriptionEntry,
        CancellationToken cancellationToken)
    {
        if (_webSocket?.State != WebSocketState.Open)
            throw new InvalidOperationException("WebSocket is not connected.");

        var requestId = Interlocked.Increment(ref _requestId);
        var request = new JsonRpcRequest
        {
            Id = requestId,
            Method = method,
            Params = parameters
        };

        var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[requestId] = tcs;

        // Pre-register the subscription entry so the response handler can add it
        // to _activeSubscriptions BEFORE resolving the TCS, preventing the race
        // where notifications arrive before the subscription is tracked.
        if (subscriptionEntry is not null)
            _pendingSubscriptionEntries[requestId] = subscriptionEntry;

        try
        {
            var json = JsonSerializer.Serialize(request, _jsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);

            await _webSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                cancellationToken);

            // Wait for the response with a timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            return await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch
        {
            _pendingRequests.TryRemove(requestId, out _);
            _pendingSubscriptionEntries.TryRemove(requestId, out _);
            throw;
        }
    }

    #endregion

    #region Subscription API

    /// <summary>
    /// Subscribes to the tickStream, receiving comprehensive tick data with transactions and logs.
    /// The subscription automatically resumes from the last received tick on reconnection.
    /// </summary>
    public async Task<BobSubscription<TickStreamNotification>> SubscribeTickStreamAsync(
        TickStreamOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        options ??= new TickStreamOptions();

        uint? lastReceivedTick = null;

        object[] BuildParams()
        {
            var startTick = lastReceivedTick.HasValue
                ? lastReceivedTick.Value + 1
                : options.StartTick;

            var paramObj = new Dictionary<string, object?>();
            if (startTick.HasValue) paramObj["startTick"] = startTick.Value;
            paramObj["skipEmptyTicks"] = options.SkipEmptyTicks;
            paramObj["includeInputData"] = options.IncludeInputData;
            if (options.TxFilters is { Count: > 0 }) paramObj["txFilters"] = options.TxFilters;
            if (options.LogFilters is { Count: > 0 }) paramObj["logFilters"] = options.LogFilters;

            return ["tickStream", paramObj];
        }

        var initialParams = BuildParams();
        var subscription = new BobSubscription<TickStreamNotification>(
            "tickStream", initialParams, BuildParams, _options.SubscriptionBufferSize);

        var entry = new SubscriptionEntry(subscription, notification =>
        {
            var data = JsonSerializer.Deserialize<TickStreamNotification>(
                notification.GetRawText(), _jsonOptions);

            if (data is null) return;

            // Track cursor for resubscription
            lastReceivedTick = data.Tick;

            _ = subscription.WriteAsync(data, subscription.CancellationToken);
        });

        await RegisterSubscriptionAsync(subscription, entry, cancellationToken);
        return subscription;
    }

    /// <summary>
    /// Subscribes to transfer events for specified identities.
    /// The subscription automatically resumes from the last received logId on reconnection.
    /// </summary>
    public async Task<BobSubscription<TransferNotification>> SubscribeTransfersAsync(
        TransferSubscriptionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        options ??= new TransferSubscriptionOptions();

        long? lastReceivedLogId = null;
        uint? lastEpoch = null;

        object[] BuildParams()
        {
            var paramObj = new Dictionary<string, object?>();
            if (options.Identities is { Count: > 0 }) paramObj["identity"] = options.Identities;

            if (lastReceivedLogId.HasValue)
            {
                paramObj["startLogId"] = lastReceivedLogId.Value + 1;
                if (lastEpoch.HasValue) paramObj["startEpoch"] = lastEpoch.Value;
            }
            else
            {
                if (options.StartLogId.HasValue) paramObj["startLogId"] = options.StartLogId.Value;
                if (options.StartEpoch.HasValue) paramObj["startEpoch"] = options.StartEpoch.Value;
            }

            return ["transfers", paramObj];
        }

        var initialParams = BuildParams();
        var subscription = new BobSubscription<TransferNotification>(
            "transfers", initialParams, BuildParams, _options.SubscriptionBufferSize);

        var entry = new SubscriptionEntry(subscription, notification =>
        {
            var data = JsonSerializer.Deserialize<TransferNotification>(
                notification.GetRawText(), _jsonOptions);

            if (data is null) return;

            // Don't update cursor for catchUpComplete signal (it has no real logId/epoch)
            if (!data.CatchUpComplete)
            {
                lastReceivedLogId = data.LogId;
                lastEpoch = data.Epoch;
            }

            _ = subscription.WriteAsync(data, subscription.CancellationToken);
        });

        await RegisterSubscriptionAsync(subscription, entry, cancellationToken);
        return subscription;
    }

    /// <summary>
    /// Subscribes to log events with optional identity and log type filtering.
    /// The subscription automatically resumes from the last received logId on reconnection.
    /// </summary>
    public async Task<BobSubscription<LogNotification>> SubscribeLogsAsync(
        LogSubscriptionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        options ??= new LogSubscriptionOptions();

        long? lastReceivedLogId = null;
        uint? lastEpoch = null;

        object[] BuildParams()
        {
            var paramObj = new Dictionary<string, object?>();
            if (options.Identities is { Count: > 0 }) paramObj["identity"] = options.Identities;
            if (options.LogTypes is { Count: > 0 }) paramObj["logType"] = options.LogTypes;

            if (lastReceivedLogId.HasValue)
            {
                paramObj["startLogId"] = lastReceivedLogId.Value + 1;
                if (lastEpoch.HasValue) paramObj["startEpoch"] = lastEpoch.Value;
            }
            else
            {
                if (options.StartLogId.HasValue) paramObj["startLogId"] = options.StartLogId.Value;
                if (options.StartEpoch.HasValue) paramObj["startEpoch"] = options.StartEpoch.Value;
            }

            return ["logs", paramObj];
        }

        var initialParams = BuildParams();
        var subscription = new BobSubscription<LogNotification>(
            "logs", initialParams, BuildParams, _options.SubscriptionBufferSize);

        var entry = new SubscriptionEntry(subscription, notification =>
        {
            var data = JsonSerializer.Deserialize<LogNotification>(
                notification.GetRawText(), _jsonOptions);

            if (data is null) return;

            // Don't update cursor for catchUpComplete signal (it has no real logId/epoch)
            if (!data.CatchUpComplete)
            {
                lastReceivedLogId = data.LogId;
                lastEpoch = data.Epoch;
            }

            _ = subscription.WriteAsync(data, subscription.CancellationToken);
        });

        await RegisterSubscriptionAsync(subscription, entry, cancellationToken);
        return subscription;
    }

    /// <summary>
    /// Subscribes to lightweight new tick notifications.
    /// </summary>
    public async Task<BobSubscription<NewTickNotification>> SubscribeNewTicksAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        object[] BuildParams() => ["newTicks"];

        var subscription = new BobSubscription<NewTickNotification>(
            "newTicks", BuildParams(), BuildParams, _options.SubscriptionBufferSize);

        var entry = new SubscriptionEntry(subscription, notification =>
        {
            var data = JsonSerializer.Deserialize<NewTickNotification>(
                notification.GetRawText(), _jsonOptions);

            if (data is null) return;

            _ = subscription.WriteAsync(data, subscription.CancellationToken);
        });

        await RegisterSubscriptionAsync(subscription, entry, cancellationToken);
        return subscription;
    }

    /// <summary>
    /// Unsubscribes a subscription from the server and removes it from tracking.
    /// </summary>
    public async Task UnsubscribeAsync<T>(BobSubscription<T> subscription, CancellationToken cancellationToken = default)
    {
        if (subscription.ServerSubscriptionId is not null)
        {
            try
            {
                await SendUnsubscribeAsync(subscription.ServerSubscriptionId, cancellationToken);
            }
            catch
            {
                // Best-effort unsubscribe
            }

            _activeSubscriptions.TryRemove(subscription.ServerSubscriptionId, out _);
        }

        subscription.Dispose();
    }

    private async Task RegisterSubscriptionAsync<T>(
        BobSubscription<T> subscription,
        SubscriptionEntry entry,
        CancellationToken cancellationToken)
    {
        var subscriptionId = await SendSubscribeAsync(
            subscription.SubscriptionType,
            subscription.OriginalParams,
            entry,
            cancellationToken);

        subscription.ServerSubscriptionId = subscriptionId;

        // Entry is already registered by the response handler, but ensure it's there
        // in case the response wasn't a string (fallback path).
        if (subscriptionId is not null)
            _activeSubscriptions.TryAdd(subscriptionId, entry);
    }

    #endregion

    #region RPC Query API

    /// <summary>
    /// Sends a JSON-RPC request over the WebSocket and deserializes the result.
    /// This is the generic entry point for any RPC call over the persistent connection.
    /// </summary>
    /// <typeparam name="T">The expected result type.</typeparam>
    /// <param name="method">The JSON-RPC method name (e.g. "qubic_chainId").</param>
    /// <param name="parameters">Optional method parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The WebSocket is not connected.</exception>
    /// <exception cref="BobRpcException">The server returned a JSON-RPC error.</exception>
    public async Task<T> CallAsync<T>(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var result = await SendRequestAsync(method, parameters, cancellationToken);

        if (result is null)
            return default!;

        return result.Value.Deserialize<T>(_jsonOptions)!;
    }

    // --- Chain Information ---

    /// <summary>Gets the chain ID and network name.</summary>
    public Task<ChainIdResponse> GetChainIdAsync(CancellationToken cancellationToken = default)
        => CallAsync<ChainIdResponse>("qubic_chainId", null, cancellationToken);

    /// <summary>Gets the client version string.</summary>
    public Task<string> GetClientVersionAsync(CancellationToken cancellationToken = default)
        => CallAsync<string>("qubic_clientVersion", null, cancellationToken);

    /// <summary>Gets the current sync status.</summary>
    public Task<SyncStatusResponse> GetSyncingAsync(CancellationToken cancellationToken = default)
        => CallAsync<SyncStatusResponse>("qubic_syncing", null, cancellationToken);

    /// <summary>Gets the current epoch information.</summary>
    public Task<EpochInfoResponse> GetCurrentEpochAsync(CancellationToken cancellationToken = default)
        => CallAsync<EpochInfoResponse>("qubic_getCurrentEpoch", null, cancellationToken);

    /// <summary>Gets epoch information for a specific epoch.</summary>
    public Task<EpochInfoResponse> GetEpochInfoAsync(int epoch, CancellationToken cancellationToken = default)
        => CallAsync<EpochInfoResponse>("qubic_getEpochInfo", new object[] { epoch }, cancellationToken);

    // --- Tick Operations ---

    /// <summary>Gets the current tick number.</summary>
    public Task<uint> GetTickNumberAsync(CancellationToken cancellationToken = default)
        => CallAsync<uint>("qubic_getTickNumber", null, cancellationToken);

    /// <summary>Gets tick data by tick number.</summary>
    public Task<BobTickResponse> GetTickByNumberAsync(uint tickNumber, CancellationToken cancellationToken = default)
        => CallAsync<BobTickResponse>("qubic_getTickByNumber", new object[] { tickNumber }, cancellationToken);

    /// <summary>Gets tick data by tick hash.</summary>
    public Task<BobTickResponse> GetTickByHashAsync(string tickHash, CancellationToken cancellationToken = default)
        => CallAsync<BobTickResponse>("qubic_getTickByHash", new object[] { tickHash }, cancellationToken);

    // --- Balance & Transfers ---

    /// <summary>Gets the balance for an identity.</summary>
    public Task<BobBalanceResponse> GetBalanceAsync(string identity, CancellationToken cancellationToken = default)
        => CallAsync<BobBalanceResponse>("qubic_getBalance", new object[] { identity }, cancellationToken);

    /// <summary>Gets transfer history for an identity with optional tick range.</summary>
    public Task<List<BobTransferResponse>> GetTransfersAsync(
        string identity,
        uint? startTick = null,
        uint? endTick = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = new List<object> { identity };
        if (startTick.HasValue) parameters.Add(startTick.Value);
        if (endTick.HasValue) parameters.Add(endTick.Value);
        return CallAsync<List<BobTransferResponse>>("qubic_getTransfers", parameters.ToArray(), cancellationToken);
    }

    /// <summary>Gets the asset balance for an identity and asset.</summary>
    public Task<BobAssetBalanceResponse?> GetAssetBalanceAsync(
        string identity,
        string assetId,
        CancellationToken cancellationToken = default)
        => CallAsync<BobAssetBalanceResponse?>("qubic_getAssetBalance", new object[] { identity, assetId }, cancellationToken);

    // --- Transactions ---

    /// <summary>Gets a transaction by hash.</summary>
    public Task<BobTransactionResponse?> GetTransactionByHashAsync(string txHash, CancellationToken cancellationToken = default)
        => CallAsync<BobTransactionResponse?>("qubic_getTransactionByHash", new object[] { txHash }, cancellationToken);

    /// <summary>Gets a transaction receipt by hash.</summary>
    public Task<TransactionReceiptResponse?> GetTransactionReceiptAsync(string txHash, CancellationToken cancellationToken = default)
        => CallAsync<TransactionReceiptResponse?>("qubic_getTransactionReceipt", new object[] { txHash }, cancellationToken);

    // --- Epoch Logs & Computors ---

    /// <summary>Gets the end-of-epoch logs (emission rewards, etc.) for a specific epoch.</summary>
    public Task<List<BobLogEntry>> GetEndEpochLogsAsync(uint epoch, CancellationToken cancellationToken = default)
        => CallAsync<List<BobLogEntry>>("qubic_getEndEpochLogs", new object[] { epoch }, cancellationToken);

    /// <summary>Gets the list of 676 computor identities for a specific epoch.</summary>
    public Task<ComputorsResponse> GetComputorsAsync(uint epoch, CancellationToken cancellationToken = default)
        => CallAsync<ComputorsResponse>("qubic_getComputors", new object[] { epoch }, cancellationToken);

    // --- Logs & Smart Contracts ---

    /// <summary>Gets log entries for a smart contract.</summary>
    public Task<List<BobLogEntry>> GetLogsAsync(
        int contractIndex,
        long? startLogId = null,
        long? endLogId = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = new List<object> { contractIndex };
        if (startLogId.HasValue) parameters.Add(startLogId.Value);
        if (endLogId.HasValue) parameters.Add(endLogId.Value);
        return CallAsync<List<BobLogEntry>>("qubic_getLogs", parameters.ToArray(), cancellationToken);
    }

    /// <summary>Queries a smart contract function (async with polling).</summary>
    public async Task<string> QuerySmartContractAsync(
        int contractIndex,
        int funcNumber,
        string hexData,
        CancellationToken cancellationToken = default)
    {
        var nonce = Interlocked.Increment(ref _scQueryNonce);
        var parameters = new object[]
        {
            new Dictionary<string, object>
            {
                ["nonce"] = nonce,
                ["scIndex"] = contractIndex,
                ["funcNumber"] = funcNumber,
                ["data"] = hexData
            }
        };

        for (int i = 0; i < 50; i++)
        {
            var result = await CallAsync<ScQueryResponse>("qubic_querySmartContract", parameters, cancellationToken);

            if (result.Error is { } error)
                throw new BobRpcException(-1, error);

            if (result.Data != null)
                return result.Data;

            await Task.Delay(100, cancellationToken);
        }

        throw new BobRpcException(-1, "Smart contract query timed out");
    }

    #endregion

    #region Helpers

    private TimeSpan CalculateBackoff(int attempt)
    {
        var delayMs = _options.ReconnectDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var capped = Math.Min(delayMs, _options.MaxReconnectDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(capped);
    }

    private void EmitEvent(BobConnectionEventType type, string message,
        string? nodeUrl = null, Exception? exception = null)
    {
        _options.OnConnectionEvent?.Invoke(new BobConnectionEvent
        {
            Type = type,
            Message = message,
            NodeUrl = nodeUrl,
            Exception = exception
        });
    }

    #endregion

    #region Disposal

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();

        // Wait for background tasks to complete
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

        foreach (var entry in _activeSubscriptions.Values)
            entry.Subscription.Dispose();

        _activeSubscriptions.Clear();

        if (_webSocket?.State == WebSocketState.Open)
        {
            try
            {
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None);
            }
            catch { /* best effort close */ }
        }

        _webSocket?.Dispose();
        _httpClient.Dispose();
        _cts?.Dispose();
        _connectLock.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    #endregion

    /// <summary>
    /// Internal wrapper that holds a subscription and its notification dispatch callback.
    /// This allows the receive loop to dispatch typed notifications without knowing T.
    /// </summary>
    private sealed class SubscriptionEntry
    {
        private readonly Action<JsonElement> _dispatchCallback;

        public IBobSubscription Subscription { get; }

        public SubscriptionEntry(IBobSubscription subscription, Action<JsonElement> dispatchCallback)
        {
            Subscription = subscription;
            _dispatchCallback = dispatchCallback;
        }

        public void DispatchNotification(JsonElement notification)
        {
            _dispatchCallback(notification);
        }
    }
}
