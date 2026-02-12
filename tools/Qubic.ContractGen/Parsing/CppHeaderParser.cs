using System.Text.RegularExpressions;

namespace Qubic.ContractGen.Parsing;

public class CppHeaderParser
{
    private readonly Dictionary<string, long> _constants = new();
    private readonly Dictionary<string, StructDef> _fileStructs = new();
    private readonly List<string> _warnings = [];

    public List<string> Warnings => _warnings;

    public ContractDefinition Parse(string headerPath, int contractIndex, string csharpName, string cppStructName)
    {
        var lines = File.ReadAllLines(headerPath);
        var contract = new ContractDefinition
        {
            CppStructName = cppStructName,
            CSharpName = csharpName,
            ContractIndex = contractIndex,
            HeaderFile = Path.GetFileName(headerPath)
        };

        // Phase 1: Collect constexpr constants from the entire file
        CollectConstants(lines);

        // Phase 2: Collect file-scope struct definitions (outside the contract struct)
        CollectFileStructs(lines, cppStructName);

        // Phase 3: Parse REGISTER macros to find function/procedure names and IDs
        var functions = new Dictionary<string, int>();
        var procedures = new Dictionary<string, int>();
        ParseRegisterMacros(lines, functions, procedures);

        // Phase 4: Parse input/output structs inside the contract struct
        var structDefs = ParseContractStructs(lines, cppStructName);

        // Phase 5: Build function and procedure definitions
        foreach (var (name, id) in functions)
        {
            var func = new FunctionDef { Name = name, InputType = id };
            func.Input = ResolveStruct(structDefs, $"{name}_input", name, "input");
            func.Output = ResolveStruct(structDefs, $"{name}_output", name, "output");
            contract.Functions.Add(func);
        }

        foreach (var (name, id) in procedures)
        {
            var proc = new ProcedureDef { Name = name, InputType = id };
            proc.Input = ResolveStruct(structDefs, $"{name}_input", name, "input");
            proc.Output = ResolveStruct(structDefs, $"{name}_output", name, "output");
            contract.Procedures.Add(proc);
        }

        return contract;
    }

    // QPI array typedefs from qpi.h (e.g., id_8 = Array<id, 8>)
    private static readonly Dictionary<string, (string ElemType, int Count)> QpiArrayTypedefs = new()
    {
        ["id_2"] = ("id", 2), ["id_4"] = ("id", 4), ["id_8"] = ("id", 8),
        ["sint8_2"] = ("sint8", 2), ["sint8_4"] = ("sint8", 4), ["sint8_8"] = ("sint8", 8),
        ["uint8_2"] = ("uint8", 2), ["uint8_4"] = ("uint8", 4), ["uint8_8"] = ("uint8", 8),
        ["sint16_2"] = ("sint16", 2), ["sint16_4"] = ("sint16", 4), ["sint16_8"] = ("sint16", 8),
        ["uint16_2"] = ("uint16", 2), ["uint16_4"] = ("uint16", 4), ["uint16_8"] = ("uint16", 8),
        ["sint32_2"] = ("sint32", 2), ["sint32_4"] = ("sint32", 4), ["sint32_8"] = ("sint32", 8),
        ["uint32_2"] = ("uint32", 2), ["uint32_4"] = ("uint32", 4), ["uint32_8"] = ("uint32", 8),
        ["sint64_2"] = ("sint64", 2), ["sint64_4"] = ("sint64", 4), ["sint64_8"] = ("sint64", 8),
        ["uint64_2"] = ("uint64", 2), ["uint64_4"] = ("uint64", 4), ["uint64_8"] = ("uint64", 8),
        ["bit_2"] = ("bit", 2), ["bit_4"] = ("bit", 4), ["bit_8"] = ("bit", 8),
        ["bit_16"] = ("bit", 16), ["bit_32"] = ("bit", 32), ["bit_64"] = ("bit", 64),
        ["bit_128"] = ("bit", 128), ["bit_256"] = ("bit", 256), ["bit_512"] = ("bit", 512),
        ["bit_1024"] = ("bit", 1024), ["bit_2048"] = ("bit", 2048), ["bit_4096"] = ("bit", 4096),
    };

    // All structs found inside the contract (not just _input/_output) for resolving nested types
    private readonly Dictionary<string, StructDef> _contractInternalStructs = new();

    private void CollectConstants(string[] lines)
    {
        var constexprRe = new Regex(@"constexpr\s+\w+\s+(\w+)\s*=\s*(.+?)\s*;");
        foreach (var line in lines)
        {
            var m = constexprRe.Match(line);
            if (m.Success)
            {
                var name = m.Groups[1].Value;
                var expr = m.Groups[2].Value;
                if (TryEvaluateExpr(expr, out var val))
                    _constants[name] = val;
            }
        }
    }

    private bool TryEvaluateExpr(string expr, out long value)
    {
        value = 0;
        // Remove type suffixes
        expr = expr.Trim()
            .Replace("ULL", "")
            .Replace("UL", "")
            .Replace("LL", "")
            .Replace("U", "");

        // Try direct number
        if (TryParseNumber(expr, out value))
            return true;

        // Try constant reference
        if (_constants.TryGetValue(expr, out value))
            return true;

        // Try simple multiplication: A * B
        var mulParts = expr.Split('*').Select(p => p.Trim()).ToArray();
        if (mulParts.Length == 2)
        {
            if (ResolveValue(mulParts[0], out var a) && ResolveValue(mulParts[1], out var b))
            {
                value = a * b;
                return true;
            }
        }

        // Try simple addition: A + B
        if (expr.Contains('+') && !expr.Contains('*'))
        {
            var addParts = expr.Split('+').Select(p => p.Trim()).ToArray();
            if (addParts.Length == 2)
            {
                if (ResolveValue(addParts[0], out var a) && ResolveValue(addParts[1], out var b))
                {
                    value = a + b;
                    return true;
                }
            }
        }

        return false;
    }

    private bool ResolveValue(string token, out long value)
    {
        token = token.Trim().Replace("ULL", "").Replace("UL", "").Replace("LL", "").Replace("U", "");
        if (TryParseNumber(token, out value))
            return true;
        return _constants.TryGetValue(token, out value);
    }

    private static bool TryParseNumber(string s, out long value)
    {
        value = 0;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out value);
        return long.TryParse(s, out value);
    }

    private void CollectFileStructs(string[] lines, string contractStructName)
    {
        // Collect struct definitions at file scope (outside the contract struct)
        var structRe = new Regex(@"^struct\s+(\w+)\s*\{?\s*$");
        int i = 0;
        while (i < lines.Length)
        {
            var line = StripComment(lines[i]).Trim();

            // Skip the contract struct itself and its inner content
            if (line.Contains($"struct {contractStructName}") && line.Contains("ContractBase"))
            {
                // Skip until we exit this struct (track braces)
                int depth = 0;
                while (i < lines.Length)
                {
                    var l = StripComment(lines[i]);
                    depth += l.Count(c => c == '{') - l.Count(c => c == '}');
                    i++;
                    if (depth <= 0 && l.Contains('}'))
                        break;
                }
                continue;
            }

            var m = structRe.Match(line);
            if (m.Success)
            {
                var structName = m.Groups[1].Value;
                // Skip if it's a 2-struct (e.g., QX2, QEARN2) or the contract struct
                if (structName.EndsWith("2") || structName == contractStructName)
                {
                    i++;
                    continue;
                }

                var fields = new List<FieldDef>();
                var nested = new List<StructDef>();
                i++;
                int depth = line.Contains('{') ? 1 : 0;
                while (i < lines.Length)
                {
                    var l = StripComment(lines[i]).Trim();
                    if (l.Contains('{')) depth++;
                    if (l.Contains('}'))
                    {
                        depth--;
                        if (depth <= 0) { i++; break; }
                    }
                    if (depth == 1)
                        ParseFieldLine(l, fields, nested);
                    i++;
                }

                _fileStructs[structName] = new StructDef
                {
                    CppName = structName,
                    Fields = fields,
                    NestedStructs = nested
                };
                continue;
            }
            i++;
        }
    }

    private void ParseRegisterMacros(string[] lines, Dictionary<string, int> functions, Dictionary<string, int> procedures)
    {
        var funcRe = new Regex(@"REGISTER_USER_FUNCTION\s*\(\s*(\w+)\s*,\s*(\d+)\s*\)");
        var procRe = new Regex(@"REGISTER_USER_PROCEDURE\s*\(\s*(\w+)\s*,\s*(\d+)\s*\)");

        foreach (var line in lines)
        {
            var fm = funcRe.Match(line);
            if (fm.Success)
            {
                functions[fm.Groups[1].Value] = int.Parse(fm.Groups[2].Value);
                continue;
            }
            var pm = procRe.Match(line);
            if (pm.Success)
            {
                procedures[pm.Groups[1].Value] = int.Parse(pm.Groups[2].Value);
            }
        }
    }

    private Dictionary<string, StructDef> ParseContractStructs(string[] lines, string contractStructName)
    {
        var result = new Dictionary<string, StructDef>();
        var typedefs = new Dictionary<string, string>();

        // Find the contract struct
        int startIdx = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = StripComment(lines[i]).Trim();
            if (line.Contains($"struct {contractStructName}") && line.Contains("ContractBase"))
            {
                startIdx = i;
                break;
            }
        }

        if (startIdx < 0) return result;

        // Parse inside the contract struct
        // C++ structs are public by default, so start as public
        bool inPublic = true;
        int depth = 0;
        int structDepth = -1;
        string? currentStructName = null;
        List<FieldDef>? currentFields = null;
        List<StructDef>? currentNested = null;
        int nestedStructDepth = -1;
        string? nestedStructName = null;
        List<FieldDef>? nestedFields = null;

        // Match struct declarations - allow trailing whitespace and optional brace
        var structRe = new Regex(@"struct\s+(\w+?)_(input|output)\s*\{?");
        var nestedStructRe = new Regex(@"struct\s+(\w+)\s*\{?");
        var typedefRe = new Regex(@"typedef\s+(.+?)\s+(\w+?)_(input|output)\s*;");

        for (int i = startIdx; i < lines.Length; i++)
        {
            var line = StripComment(lines[i]).Trim();
            var braceOpen = line.Count(c => c == '{');
            var braceClose = line.Count(c => c == '}');

            if (i == startIdx)
            {
                depth = braceOpen;
                continue;
            }

            depth += braceOpen - braceClose;
            if (depth <= 0) break; // Exited contract struct

            if (line == "public:" || line.StartsWith("public:")) { inPublic = true; continue; }
            if (line == "protected:" || line.StartsWith("protected:") ||
                line == "private:" || line.StartsWith("private:")) { inPublic = false; continue; }

            // Handle typedef lines
            var tdm = typedefRe.Match(line);
            if (tdm.Success)
            {
                var sourceType = tdm.Groups[1].Value.Trim();
                var targetName = $"{tdm.Groups[2].Value}_{tdm.Groups[3].Value}";
                var sd = ResolveTypedefToStruct(sourceType);
                result[targetName] = sd;
                continue;
            }

            // Inside a nested struct within a _input/_output struct
            if (nestedStructDepth >= 0)
            {
                if (braceClose > 0 && depth <= nestedStructDepth)
                {
                    // Closing the nested struct
                    var nestedStruct = new StructDef
                    {
                        CppName = nestedStructName ?? "",
                        Fields = nestedFields ?? []
                    };
                    currentNested?.Add(nestedStruct);
                    nestedStructDepth = -1;
                    nestedStructName = null;
                    nestedFields = null;
                    continue;
                }

                if (nestedFields != null)
                    ParseFieldLine(line, nestedFields, []);
                continue;
            }

            // Inside a _input/_output struct
            if (currentStructName != null)
            {
                if (braceClose > 0 && depth <= structDepth)
                {
                    // Closing the struct
                    result[currentStructName] = new StructDef
                    {
                        CppName = currentStructName,
                        Fields = currentFields ?? [],
                        NestedStructs = currentNested ?? []
                    };
                    currentStructName = null;
                    currentFields = null;
                    currentNested = null;
                    structDepth = -1;
                    continue;
                }

                // Check for nested struct inside _input/_output
                var nsm = nestedStructRe.Match(line);
                if (nsm.Success && !line.Contains("ContractBase") && !line.Contains("_input") && !line.Contains("_output") && !line.Contains("_locals"))
                {
                    nestedStructDepth = depth;
                    nestedStructName = nsm.Groups[1].Value;
                    nestedFields = [];
                    continue;
                }

                if (currentFields != null)
                    ParseFieldLine(line, currentFields, []);
                continue;
            }

            // Look for struct Name_input/output (in public sections, or at contract-scope depth 1)
            var sm = structRe.Match(line);
            if (sm.Success && (inPublic || depth == 1) && !line.Contains("_locals"))
            {
                currentStructName = $"{sm.Groups[1].Value}_{sm.Groups[2].Value}";
                currentFields = [];
                currentNested = [];
                structDepth = depth;
                continue;
            }

            // Also collect non-input/output structs at depth 1 (contract-level)
            // These may be used as field types in input/output structs
            var generalStructRe = new Regex(@"struct\s+(\w+)\s*\{?");
            var gsm = generalStructRe.Match(line);
            if (gsm.Success && depth == 1 && !line.Contains("ContractBase")
                && !line.Contains("_locals") && !line.Contains("_input") && !line.Contains("_output"))
            {
                var structName = gsm.Groups[1].Value;
                // Parse this struct and store it as a contract-internal struct
                currentStructName = $"__internal__{structName}";
                currentFields = [];
                currentNested = [];
                structDepth = depth;
                continue;
            }
        }

        // Move internal structs to the _contractInternalStructs dictionary
        var keysToRemove = new List<string>();
        foreach (var kvp in result)
        {
            if (kvp.Key.StartsWith("__internal__"))
            {
                var realName = kvp.Key["__internal__".Length..];
                _contractInternalStructs[realName] = kvp.Value;
                _contractInternalStructs[realName].CppName = realName;
                keysToRemove.Add(kvp.Key);
            }
        }
        foreach (var key in keysToRemove)
            result.Remove(key);

        return result;
    }

    private StructDef ResolveTypedefToStruct(string sourceType)
    {
        sourceType = sourceType.Trim();

        if (sourceType == "NoData")
            return new StructDef { CppName = "NoData", IsTypedef = true, TypedefTarget = "NoData" };

        if (sourceType == "Asset")
            return new StructDef
            {
                CppName = "Asset",
                IsTypedef = true,
                TypedefTarget = "Asset",
                Fields = [
                    new FieldDef { CppType = "id", Name = "issuer" },
                    new FieldDef { CppType = "uint64", Name = "assetName" }
                ]
            };

        if (TypeMapper.IsPrimitive(sourceType))
            return new StructDef
            {
                CppName = sourceType,
                IsTypedef = true,
                TypedefTarget = sourceType,
                Fields = [new FieldDef { CppType = sourceType, Name = "value" }]
            };

        // Check if it's a file-scope struct we collected
        if (_fileStructs.TryGetValue(sourceType, out var fsd))
            return fsd;

        // Check if it's a contract-internal struct
        if (_contractInternalStructs.TryGetValue(sourceType, out var cisd))
            return cisd;

        // Unknown typedef - warn and return empty
        _warnings.Add($"Unknown typedef source type: {sourceType}");
        return new StructDef { CppName = sourceType, IsTypedef = true, TypedefTarget = sourceType };
    }

    private void ParseFieldLine(string line, List<FieldDef> fields, List<StructDef> nested)
    {
        line = line.Trim();
        if (string.IsNullOrEmpty(line) || line.StartsWith("//") || line.StartsWith("/*")
            || line.StartsWith("*") || line == "{" || line == "}" || line == "};"
            || line.StartsWith("PRIVATE") || line.StartsWith("PUBLIC")
            || line.StartsWith("REGISTER") || line.StartsWith("typedef")
            || line.StartsWith("#") || line.StartsWith("static")
            || line.StartsWith("inline") || line.StartsWith("enum")
            || line.StartsWith("constexpr"))
            return;

        // Handle Array<T, N> fields
        var arrayRe = new Regex(@"Array\s*<\s*(\w+)\s*,\s*(.+?)\s*>\s+(\w+)\s*;");
        var am = arrayRe.Match(line);
        if (am.Success)
        {
            var elemType = am.Groups[1].Value;
            var sizeExpr = am.Groups[2].Value;
            var fieldName = am.Groups[3].Value;
            int arrayLen = 0;
            if (TryEvaluateExpr(sizeExpr, out var sz))
                arrayLen = (int)sz;
            else
                _warnings.Add($"Could not evaluate array size: {sizeExpr}");

            fields.Add(new FieldDef
            {
                CppType = elemType,
                Name = fieldName,
                IsArray = true,
                ArrayElementType = elemType,
                ArrayLength = arrayLen,
                NestedStructTypeName = TypeMapper.IsPrimitive(elemType) ? null : elemType
            });
            return;
        }

        // Handle regular fields: type name1, name2, ...;
        var fieldRe = new Regex(@"^(\w+)\s+(.+?)\s*;");
        var fm = fieldRe.Match(line);
        if (fm.Success)
        {
            var type = fm.Groups[1].Value;
            var namesPart = fm.Groups[2].Value;

            // Skip if it looks like a method declaration or a struct
            if (namesPart.Contains('(') || type == "struct" || type == "void" || type == "auto")
                return;

            // Check if the type is a QPI array typedef (e.g., id_8, bit_4096)
            if (QpiArrayTypedefs.TryGetValue(type, out var qpiArray))
            {
                var names2 = namesPart.Split(',').Select(n => n.Trim()).Where(n => !string.IsNullOrEmpty(n)).ToArray();
                foreach (var name in names2)
                {
                    if (name.StartsWith('_')) continue;
                    fields.Add(new FieldDef
                    {
                        CppType = qpiArray.ElemType,
                        Name = name,
                        IsArray = true,
                        ArrayElementType = qpiArray.ElemType,
                        ArrayLength = qpiArray.Count,
                    });
                }
                return;
            }

            var names = namesPart.Split(',').Select(n => n.Trim()).Where(n => !string.IsNullOrEmpty(n)).ToArray();
            foreach (var name in names)
            {
                // Skip names that start with _ (private/internal fields)
                if (name.StartsWith('_'))
                    continue;

                // Check if name contains a QPI array typedef pattern
                fields.Add(new FieldDef { CppType = type, Name = name });
            }
        }
    }

    private StructDef ResolveStruct(Dictionary<string, StructDef> defs, string key, string funcName, string suffix)
    {
        if (defs.TryGetValue(key, out var sd))
            return sd;

        _warnings.Add($"Could not find struct definition for {key} (function: {funcName})");
        return new StructDef { CppName = key };
    }

    private static string StripComment(string line)
    {
        // Strip // comments
        var idx = line.IndexOf("//");
        if (idx >= 0)
        {
            // Make sure it's not inside a string
            line = line[..idx];
        }
        // Strip /* */ inline comments
        while (true)
        {
            var start = line.IndexOf("/*");
            if (start < 0) break;
            var end = line.IndexOf("*/", start + 2);
            if (end < 0) { line = line[..start]; break; }
            line = line[..start] + line[(end + 2)..];
        }
        return line;
    }
}
