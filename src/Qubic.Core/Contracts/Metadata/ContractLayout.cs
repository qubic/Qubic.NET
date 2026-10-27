namespace Qubic.Core.Contracts.Metadata;

/// <summary>
/// C++ struct layout for contract input/output structs, matching MSVC default packing (/Zp8):
/// every field is aligned to its natural alignment and the struct size is padded to a
/// multiple of its largest member alignment.
/// </summary>
public static class ContractLayout
{
    /// <summary>Byte offsets of <paramref name="def"/>'s fields, plus total size and alignment.</summary>
    public static List<int> ComputeFieldOffsets(StructDef def, out int totalSize, out int structAlignment)
    {
        var offsets = new List<int>();
        int offset = 0;
        int maxAlign = 1;

        foreach (var field in def.Fields)
        {
            GetFieldSizeAndAlignment(def, field, out var fieldSize, out var fieldAlign);
            offset = AlignUp(offset, fieldAlign);
            offsets.Add(offset);
            offset += fieldSize;
            if (fieldAlign > maxAlign) maxAlign = fieldAlign;
        }

        structAlignment = maxAlign;
        totalSize = AlignUp(offset, maxAlign);
        return offsets;
    }

    /// <summary>Total serialized size of <paramref name="def"/> including trailing padding.</summary>
    public static int SizeOf(StructDef def)
    {
        ComputeFieldOffsets(def, out var size, out _);
        return size;
    }

    /// <summary>Size and alignment of one field (arrays: whole array; nested structs resolved from <paramref name="parent"/>).</summary>
    public static void GetFieldSizeAndAlignment(StructDef parent, FieldDef field, out int size, out int alignment)
    {
        if (field.IsArray)
        {
            var elemType = field.ArrayElementType ?? field.CppType;
            var elemSize = TypeMapper.GetPrimitiveSize(elemType);
            alignment = TypeMapper.IsPrimitive(elemType) ? TypeMapper.GetPrimitiveAlignment(elemType) : 1;
            if (elemSize <= 0 && FindNested(parent, field) is { } nested)
            {
                ComputeFieldOffsets(nested, out elemSize, out alignment);
            }
            size = Math.Max(0, elemSize) * field.ArrayLength;
            return;
        }

        size = Math.Max(0, TypeMapper.GetPrimitiveSize(field.CppType));
        alignment = TypeMapper.IsPrimitive(field.CppType) ? TypeMapper.GetPrimitiveAlignment(field.CppType) : 1;
        if (size == 0 && !TypeMapper.IsPrimitive(field.CppType) && FindNested(parent, field) is { } n)
        {
            ComputeFieldOffsets(n, out size, out alignment);
        }
    }

    /// <summary>The nested struct definition a field refers to, if any.</summary>
    public static StructDef? FindNested(StructDef parent, FieldDef field) =>
        parent.NestedStructs.FirstOrDefault(n => n.CppName == field.CppType)
        ?? (field.NestedStructTypeName != null
            ? parent.NestedStructs.FirstOrDefault(n => n.CppName == field.NestedStructTypeName)
            : null);

    public static int AlignUp(int value, int alignment) =>
        alignment <= 1 ? value : (value + alignment - 1) / alignment * alignment;
}
