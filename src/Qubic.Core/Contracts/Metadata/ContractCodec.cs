using System.Buffers.Binary;
using System.Globalization;
using Qubic.Crypto;

namespace Qubic.Core.Contracts.Metadata;

/// <summary>One decoded field of a contract input/output struct.</summary>
public sealed record DecodedField(string Name, string Type, string Display, IReadOnlyList<DecodedField>? Children = null);

/// <summary>A decoded contract procedure call (for signing previews).</summary>
public sealed record DecodedCall(
    int ContractIndex,
    string ContractName,
    string? EntryName,
    int InputType,
    IReadOnlyList<DecodedField> Fields,
    int ExpectedSize,
    int ActualSize)
{
    /// <summary>True when the payload size matches the struct layout exactly.</summary>
    public bool SizeMatches => ExpectedSize == ActualSize;
}

/// <summary>
/// Encodes and decodes contract input/output structs at runtime, from parsed
/// <see cref="StructDef"/> metadata (MSVC /Zp8 layout, little endian).
/// </summary>
public static class ContractCodec
{
    private const int MaxListedElements = 64;
    private static readonly IQubicCrypt Crypt = new QubicCrypt();

    // ── Decoding ──

    /// <summary>Decodes <paramref name="data"/> as <paramref name="def"/>. Short data decodes as far as it goes.</summary>
    public static IReadOnlyList<DecodedField> Decode(StructDef def, ReadOnlySpan<byte> data)
    {
        var offsets = ContractLayout.ComputeFieldOffsets(def, out _, out _);
        var result = new List<DecodedField>(def.Fields.Count);
        for (int i = 0; i < def.Fields.Count; i++)
        {
            var field = def.Fields[i];
            ContractLayout.GetFieldSizeAndAlignment(def, field, out var size, out _);
            var off = offsets[i];
            if (off + size > data.Length)
            {
                result.Add(new DecodedField(field.Name, TypeName(field), "(missing)"));
                continue;
            }
            result.Add(DecodeField(def, field, data.Slice(off, size)));
        }
        return result;
    }

    private static DecodedField DecodeField(StructDef parent, FieldDef field, ReadOnlySpan<byte> bytes)
    {
        if (field.IsArray)
        {
            var elemType = field.ArrayElementType ?? field.CppType;
            if (elemType is "uint8" or "sint8")
                return new DecodedField(field.Name, TypeName(field), "0x" + Convert.ToHexString(bytes).ToLowerInvariant());

            var nested = ContractLayout.FindNested(parent, field);
            var elemSize = field.ArrayLength > 0 ? bytes.Length / field.ArrayLength : 0;
            var children = new List<DecodedField>();
            var nonDefault = 0;
            for (int i = 0; i < field.ArrayLength && elemSize > 0; i++)
            {
                var slice = bytes.Slice(i * elemSize, elemSize);
                if (slice.IndexOfAnyExcept((byte)0) >= 0) nonDefault++;
                if (children.Count >= MaxListedElements) continue;
                children.Add(nested != null
                    ? new DecodedField($"[{i}]", nested.CppName, "", Decode(nested, slice))
                    : new DecodedField($"[{i}]", elemType, FormatPrimitive(elemType, slice)));
            }
            var summary = $"{field.ArrayLength} × {elemType}" + (nonDefault < field.ArrayLength ? $" ({nonDefault} non-zero)" : "");
            return new DecodedField(field.Name, TypeName(field), summary, children);
        }

        if (!TypeMapper.IsPrimitive(field.CppType) && ContractLayout.FindNested(parent, field) is { } n)
            return new DecodedField(field.Name, n.CppName, "", Decode(n, bytes));

        return new DecodedField(field.Name, field.CppType, FormatPrimitive(field.CppType, bytes));
    }

    /// <summary>Human-readable value of one primitive.</summary>
    public static string FormatPrimitive(string cppType, ReadOnlySpan<byte> b)
    {
        switch (cppType)
        {
            case "id":
            case "m256i":
                return b.Length >= 32 ? Crypt.GetIdentityFromPublicKey(b[..32].ToArray()) : "(short)";
            case "uint64": return BinaryPrimitives.ReadUInt64LittleEndian(b).ToString(CultureInfo.InvariantCulture);
            case "sint64": return BinaryPrimitives.ReadInt64LittleEndian(b).ToString(CultureInfo.InvariantCulture);
            case "uint32": return BinaryPrimitives.ReadUInt32LittleEndian(b).ToString(CultureInfo.InvariantCulture);
            case "sint32": return BinaryPrimitives.ReadInt32LittleEndian(b).ToString(CultureInfo.InvariantCulture);
            case "uint16": return BinaryPrimitives.ReadUInt16LittleEndian(b).ToString(CultureInfo.InvariantCulture);
            case "sint16": return BinaryPrimitives.ReadInt16LittleEndian(b).ToString(CultureInfo.InvariantCulture);
            case "uint8": return b[0].ToString(CultureInfo.InvariantCulture);
            case "sint8": return ((sbyte)b[0]).ToString(CultureInfo.InvariantCulture);
            case "bit":
            case "bool": return b[0] != 0 ? "true" : "false";
            case "DateAndTime": return BinaryPrimitives.ReadUInt64LittleEndian(b).ToString(CultureInfo.InvariantCulture);
            case "Asset":
            {
                var issuer = Crypt.GetIdentityFromPublicKey(b[..32].ToArray());
                var name = AssetNameHelper.FromUlong(BinaryPrimitives.ReadUInt64LittleEndian(b[32..])) ?? "?";
                return $"{name} (issuer {issuer})";
            }
            default:
                return "0x" + Convert.ToHexString(b).ToLowerInvariant();
        }
    }

    public static string TypeName(FieldDef f) =>
        f.IsArray ? $"{f.ArrayElementType ?? f.CppType}[{f.ArrayLength}]" : f.CppType;

    // ── Encoding ──

    /// <summary>
    /// Encodes <paramref name="def"/> from text values keyed by field path
    /// (<c>amount</c>, <c>nested.field</c>, <c>list[3]</c>, <c>list[3].field</c>).
    /// Missing values encode as zero. Throws <see cref="FormatException"/> with the field path on bad input.
    /// </summary>
    public static byte[] Encode(StructDef def, IReadOnlyDictionary<string, string> values)
    {
        var buffer = new byte[ContractLayout.SizeOf(def)];
        EncodeInto(def, values, "", buffer);
        return buffer;
    }

    private static void EncodeInto(StructDef def, IReadOnlyDictionary<string, string> values, string prefix, Span<byte> dest)
    {
        var offsets = ContractLayout.ComputeFieldOffsets(def, out _, out _);
        for (int i = 0; i < def.Fields.Count; i++)
        {
            var field = def.Fields[i];
            ContractLayout.GetFieldSizeAndAlignment(def, field, out var size, out _);
            var slot = dest.Slice(offsets[i], size);
            var path = prefix + field.Name;
            var nested = ContractLayout.FindNested(def, field);

            if (field.IsArray)
            {
                var elemType = field.ArrayElementType ?? field.CppType;
                var elemSize = field.ArrayLength > 0 ? size / field.ArrayLength : 0;
                if (elemType is "uint8" or "sint8" && values.TryGetValue(path, out var hex) && !string.IsNullOrWhiteSpace(hex))
                {
                    var bytes = ParseBytes(hex, path);
                    if (bytes.Length > size) throw new FormatException($"{path}: {bytes.Length} bytes given, field holds {size}.");
                    bytes.CopyTo(slot);
                    continue;
                }
                for (int e = 0; e < field.ArrayLength && elemSize > 0; e++)
                {
                    var elemPath = $"{path}[{e}]";
                    var elemSlot = slot.Slice(e * elemSize, elemSize);
                    if (nested != null)
                        EncodeInto(nested, values, elemPath + ".", elemSlot);
                    else if (values.TryGetValue(elemPath, out var v) && !string.IsNullOrWhiteSpace(v))
                        WritePrimitive(elemType, v, elemSlot, elemPath);
                }
                continue;
            }

            if (nested != null && !TypeMapper.IsPrimitive(field.CppType))
            {
                EncodeInto(nested, values, path + ".", slot);
                continue;
            }

            if (values.TryGetValue(path, out var value) && !string.IsNullOrWhiteSpace(value))
                WritePrimitive(field.CppType, value, slot, path);
        }
    }

    /// <summary>Writes one primitive parsed from text.</summary>
    public static void WritePrimitive(string cppType, string text, Span<byte> dest, string path = "value")
    {
        text = text.Trim();
        try
        {
            switch (cppType)
            {
                case "id":
                case "m256i":
                    ParsePublicKey(text, path).CopyTo(dest);
                    break;
                case "uint64": BinaryPrimitives.WriteUInt64LittleEndian(dest, ulong.Parse(Strip(text), CultureInfo.InvariantCulture)); break;
                case "sint64": BinaryPrimitives.WriteInt64LittleEndian(dest, long.Parse(Strip(text), CultureInfo.InvariantCulture)); break;
                case "uint32": BinaryPrimitives.WriteUInt32LittleEndian(dest, uint.Parse(Strip(text), CultureInfo.InvariantCulture)); break;
                case "sint32": BinaryPrimitives.WriteInt32LittleEndian(dest, int.Parse(Strip(text), CultureInfo.InvariantCulture)); break;
                case "uint16": BinaryPrimitives.WriteUInt16LittleEndian(dest, ushort.Parse(Strip(text), CultureInfo.InvariantCulture)); break;
                case "sint16": BinaryPrimitives.WriteInt16LittleEndian(dest, short.Parse(Strip(text), CultureInfo.InvariantCulture)); break;
                case "uint8": dest[0] = byte.Parse(Strip(text), CultureInfo.InvariantCulture); break;
                case "sint8": dest[0] = (byte)sbyte.Parse(Strip(text), CultureInfo.InvariantCulture); break;
                case "bit":
                case "bool": dest[0] = ParseBool(text) ? (byte)1 : (byte)0; break;
                case "DateAndTime": BinaryPrimitives.WriteUInt64LittleEndian(dest, ulong.Parse(Strip(text), CultureInfo.InvariantCulture)); break;
                case "Asset":
                {
                    // NAME@ISSUER
                    var at = text.IndexOf('@');
                    if (at <= 0) throw new FormatException("expected NAME@ISSUER");
                    ParsePublicKey(text[(at + 1)..], path).CopyTo(dest);
                    BinaryPrimitives.WriteUInt64LittleEndian(dest[32..], AssetNameHelper.ToUlong(text[..at].Trim().ToUpperInvariant()));
                    break;
                }
                default:
                    var raw = ParseBytes(text, path);
                    if (raw.Length > dest.Length) throw new FormatException($"{raw.Length} bytes given, field holds {dest.Length}");
                    raw.CopyTo(dest);
                    break;
            }
        }
        catch (FormatException ex) when (!ex.Message.StartsWith(path + ":", StringComparison.Ordinal))
        {
            throw new FormatException($"{path}: {ex.Message}", ex);
        }
        catch (OverflowException)
        {
            throw new FormatException($"{path}: value out of range for {cppType}.");
        }
    }

    private static string Strip(string s) => s.Replace("_", "").Replace("'", "").Replace(",", "");

    private static bool ParseBool(string s) => s.ToLowerInvariant() switch
    {
        "1" or "true" or "yes" => true,
        "0" or "false" or "no" or "" => false,
        _ => throw new FormatException("expected true/false")
    };

    /// <summary>60-char identity, or 0x + 64 hex chars.</summary>
    public static byte[] ParsePublicKey(string text, string path = "value")
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var hex = Convert.FromHexString(text[2..]);
            if (hex.Length != 32) throw new FormatException($"{path}: public key must be 32 bytes.");
            return hex;
        }
        if (text.Length != 60 || !Crypt.VerifyIdentityChecksum(text.ToUpperInvariant()))
            throw new FormatException($"{path}: not a valid 60-character identity.");
        return Crypt.GetPublicKeyFromIdentity(text.ToUpperInvariant());
    }

    private static byte[] ParseBytes(string text, string path)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        try { return Convert.FromHexString(text); }
        catch (FormatException) { throw new FormatException($"{path}: expected hex bytes."); }
    }
}
