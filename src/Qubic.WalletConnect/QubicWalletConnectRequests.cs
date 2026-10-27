using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Reown.Core.Common.Utils;
using Reown.Core.Network.Models;

namespace Qubic.WalletConnect;

/// <summary>
/// Base for incoming request params. dApps send params as an object, an empty array, or nothing,
/// and numbers sometimes as strings — so params are kept as a raw token and read leniently.
/// </summary>
[JsonConverter(typeof(LenientParamsConverter))]
public abstract class QubicWcParams
{
    public JToken Raw { get; set; } = new JObject();

    /// <summary>The params object (first element if the dApp wrapped it in an array).</summary>
    protected JObject Obj => Raw switch
    {
        JObject o => o,
        JArray { Count: > 0 } a when a[0] is JObject first => first,
        _ => new JObject()
    };

    public string? Str(string name) => Obj.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var v) && v.Type != JTokenType.Null ? v.ToString() : null;

    public long? Long(string name)
    {
        var s = Str(name);
        if (s == null) return null;
        if (long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var l)) return l;
        if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d == Math.Floor(d)) return (long)d;
        throw new FormatException($"{name} must be an integer.");
    }
}

internal sealed class LenientParamsConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => typeof(QubicWcParams).IsAssignableFrom(objectType);

    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        var token = reader.TokenType == JsonToken.Null ? new JObject() : JToken.Load(reader);
        var instance = (QubicWcParams)Activator.CreateInstance(objectType)!;
        instance.Raw = token;
        return instance;
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) =>
        ((value as QubicWcParams)?.Raw ?? new JObject()).WriteTo(writer);
}

[RpcMethod("qubic_requestAccounts"), RpcRequestOptions(Clock.FIVE_MINUTES, 99710), RpcResponseOptions(Clock.FIVE_MINUTES, 99711)]
public sealed class RequestAccountsParams : QubicWcParams { }

[RpcMethod("qubic_sendQubic"), RpcRequestOptions(Clock.FIVE_MINUTES, 99712), RpcResponseOptions(Clock.FIVE_MINUTES, 99713)]
public sealed class SendQubicParams : QubicWcParams { }

[RpcMethod("qubic_signTransaction"), RpcRequestOptions(Clock.FIVE_MINUTES, 99714), RpcResponseOptions(Clock.FIVE_MINUTES, 99715)]
public sealed class SignTransactionParams : QubicWcParams { }

[RpcMethod("qubic_sendTransaction"), RpcRequestOptions(Clock.FIVE_MINUTES, 99716), RpcResponseOptions(Clock.FIVE_MINUTES, 99717)]
public sealed class SendTransactionParams : QubicWcParams { }

[RpcMethod("qubic_sign"), RpcRequestOptions(Clock.FIVE_MINUTES, 99718), RpcResponseOptions(Clock.FIVE_MINUTES, 99719)]
public sealed class SignParams : QubicWcParams { }

[RpcMethod("qubic_sendAsset"), RpcRequestOptions(Clock.FIVE_MINUTES, 99720), RpcResponseOptions(Clock.FIVE_MINUTES, 99721)]
public sealed class SendAssetParams : QubicWcParams { }
