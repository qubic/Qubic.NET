namespace Qubic.ContractGen.Parsing;

public class ContractDefinition
{
    public string CppStructName { get; set; } = "";
    public string CSharpName { get; set; } = "";
    public int ContractIndex { get; set; }
    public string HeaderFile { get; set; } = "";
    public List<FunctionDef> Functions { get; set; } = [];
    public List<ProcedureDef> Procedures { get; set; } = [];
}

public class FunctionDef
{
    public string Name { get; set; } = "";
    public int InputType { get; set; }
    public StructDef Input { get; set; } = new();
    public StructDef Output { get; set; } = new();
}

public class ProcedureDef
{
    public string Name { get; set; } = "";
    public int InputType { get; set; }
    public StructDef Input { get; set; } = new();
    public StructDef Output { get; set; } = new();
}

public class StructDef
{
    public string CppName { get; set; } = "";
    public List<FieldDef> Fields { get; set; } = [];
    public List<StructDef> NestedStructs { get; set; } = [];
    public bool IsEmpty => Fields.Count == 0 && NestedStructs.Count == 0;
    public bool IsTypedef { get; set; }
    public string? TypedefTarget { get; set; }
}

public class FieldDef
{
    public string CppType { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsArray { get; set; }
    public string? ArrayElementType { get; set; }
    public int ArrayLength { get; set; }
    public string? NestedStructTypeName { get; set; }
}
