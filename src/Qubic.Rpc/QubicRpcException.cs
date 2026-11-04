namespace Qubic.Rpc;

/// <summary>
/// Thrown when an RPC call fails. Carries the HTTP status and the raw response body
/// (the node's actual error text), which <see cref="System.Net.Http.HttpResponseMessage.EnsureSuccessStatusCode"/>
/// discards. Also raised when a 2xx response carries a JSON error envelope
/// (<c>{"code":..,"message":..}</c>) instead of the expected payload — core-lite answers
/// broadcast/query failures that way, with HTTP 200.
/// </summary>
public sealed class QubicRpcException : Exception
{
    /// <summary>HTTP status code of the response (200 when the body carried an error envelope).</summary>
    public int StatusCode { get; }

    /// <summary>The node's error code from a JSON envelope, if any.</summary>
    public int? NodeCode { get; }

    /// <summary>The raw response body (truncated), for diagnostics.</summary>
    public string? ResponseBody { get; }

    /// <summary>The request path that failed, e.g. <c>/live/v1/broadcast-transaction</c>.</summary>
    public string? RequestPath { get; }

    public QubicRpcException(string message, int statusCode, string? responseBody = null,
        int? nodeCode = null, string? requestPath = null)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
        NodeCode = nodeCode;
        RequestPath = requestPath;
    }
}
