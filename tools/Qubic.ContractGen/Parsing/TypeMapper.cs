namespace Qubic.ContractGen.Parsing;

public static class TypeMapper
{
    public record TypeInfo(string CSharpType, int Size, bool IsBlittable = true);

    private static readonly Dictionary<string, TypeInfo> PrimitiveTypes = new()
    {
        ["id"] = new("byte[]", 32, false),
        ["m256i"] = new("byte[]", 32, false),
        ["uint64"] = new("ulong", 8),
        ["sint64"] = new("long", 8),
        ["uint32"] = new("uint", 4),
        ["sint32"] = new("int", 4),
        ["uint16"] = new("ushort", 2),
        ["sint16"] = new("short", 2),
        ["uint8"] = new("byte", 1),
        ["sint8"] = new("sbyte", 1),
        ["bit"] = new("bool", 1),
        ["bool"] = new("bool", 1),
        ["Asset"] = new("QubicAsset", 40, false),
    };

    public static bool IsPrimitive(string cppType) => PrimitiveTypes.ContainsKey(cppType);

    public static TypeInfo? GetPrimitiveType(string cppType)
    {
        return PrimitiveTypes.GetValueOrDefault(cppType);
    }

    public static string GetCSharpType(string cppType)
    {
        if (PrimitiveTypes.TryGetValue(cppType, out var info))
            return info.CSharpType;
        // Unknown type - return as-is (it may be a contract-internal struct)
        return cppType;
    }

    public static int GetPrimitiveSize(string cppType)
    {
        return PrimitiveTypes.TryGetValue(cppType, out var info) ? info.Size : -1;
    }

    public static string GetReadExpression(string cppType, string spanExpr)
    {
        return cppType switch
        {
            "id" or "m256i" => $"{spanExpr}.Slice(0, 32).ToArray()",
            "uint64" => $"BinaryPrimitives.ReadUInt64LittleEndian({spanExpr})",
            "sint64" => $"BinaryPrimitives.ReadInt64LittleEndian({spanExpr})",
            "uint32" => $"BinaryPrimitives.ReadUInt32LittleEndian({spanExpr})",
            "sint32" => $"BinaryPrimitives.ReadInt32LittleEndian({spanExpr})",
            "uint16" => $"BinaryPrimitives.ReadUInt16LittleEndian({spanExpr})",
            "sint16" => $"BinaryPrimitives.ReadInt16LittleEndian({spanExpr})",
            "uint8" => $"{spanExpr}[0]",
            "sint8" => $"(sbyte){spanExpr}[0]",
            "bit" or "bool" => $"({spanExpr}[0] != 0)",
            "Asset" => $"QubicAsset.ReadFrom({spanExpr})",
            _ => $"default /* TODO: unknown type {cppType} */"
        };
    }

    public static bool IsQpiArrayTypedef(string type, out string elemType, out int count)
    {
        elemType = "";
        count = 0;
        // Match patterns like id_8, bit_4096, uint8_4
        var m = System.Text.RegularExpressions.Regex.Match(type, @"^(\w+?)_(\d+)$");
        if (m.Success && IsPrimitive(m.Groups[1].Value))
        {
            elemType = m.Groups[1].Value;
            count = int.Parse(m.Groups[2].Value);
            return true;
        }
        return false;
    }

    public static string GetWriteStatement(string cppType, string destExpr, string valueExpr)
    {
        return cppType switch
        {
            "id" or "m256i" => $"{valueExpr}.AsSpan(0, 32).CopyTo({destExpr});",
            "uint64" => $"BinaryPrimitives.WriteUInt64LittleEndian({destExpr}, {valueExpr});",
            "sint64" => $"BinaryPrimitives.WriteInt64LittleEndian({destExpr}, {valueExpr});",
            "uint32" => $"BinaryPrimitives.WriteUInt32LittleEndian({destExpr}, {valueExpr});",
            "sint32" => $"BinaryPrimitives.WriteInt32LittleEndian({destExpr}, {valueExpr});",
            "uint16" => $"BinaryPrimitives.WriteUInt16LittleEndian({destExpr}, {valueExpr});",
            "sint16" => $"BinaryPrimitives.WriteInt16LittleEndian({destExpr}, {valueExpr});",
            "uint8" => $"{destExpr}[0] = {valueExpr};",
            "sint8" => $"{destExpr}[0] = (byte){valueExpr};",
            "bit" or "bool" => $"{destExpr}[0] = (byte)({valueExpr} ? 1 : 0);",
            "Asset" => $"{valueExpr}.WriteTo({destExpr});",
            _ => $"// TODO: serialize unknown type {cppType}"
        };
    }
}
