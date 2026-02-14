using System.Reflection;
using Qubic.Core.Contracts;

namespace Qubic.Toolkit;

public class ContractInfo
{
    public string Name { get; init; } = "";
    public int ContractIndex { get; init; }
    public List<FunctionInfo> Functions { get; init; } = [];
}

public class FunctionInfo
{
    public string Name { get; init; } = "";
    public uint InputTypeId { get; init; }
    public Type InputType { get; init; } = null!;
    public Type OutputType { get; init; } = null!;
    public bool IsEmptyInput { get; init; }
    public List<InputPropertyInfo> InputProperties { get; init; } = [];
}

public class InputPropertyInfo
{
    public string Name { get; init; } = "";
    public Type PropertyType { get; init; } = null!;
    public bool IsRequired { get; init; }
    public PropertyInfo ReflectionProperty { get; init; } = null!;
}

public class ContractDiscovery
{
    public List<ContractInfo> Contracts { get; }

    public ContractDiscovery()
    {
        Contracts = Discover();
    }

    private static List<ContractInfo> Discover()
    {
        var assembly = typeof(ISmartContractInput).Assembly;
        var contracts = new List<ContractInfo>();

        var contractTypes = assembly.GetTypes()
            .Where(t => t.IsClass && t.IsSealed && t.IsAbstract
                        && t.Namespace?.StartsWith("Qubic.Core.Contracts.") == true
                        && t.Name.EndsWith("Contract"))
            .ToList();

        foreach (var contractType in contractTypes)
        {
            var indexField = contractType.GetField("ContractIndex",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (indexField == null) continue;

            var contractIndex = (int)indexField.GetValue(null)!;
            var contractName = contractType.Name.Replace("Contract", "");
            var ns = contractType.Namespace!;

            var functions = new List<FunctionInfo>();

            var functionsClass = contractType.GetNestedType("Functions",
                BindingFlags.Public | BindingFlags.Static);
            if (functionsClass != null)
            {
                foreach (var field in functionsClass.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (field.FieldType != typeof(uint)) continue;
                    var inputTypeId = (uint)field.GetValue(null)!;
                    var funcName = field.Name;

                    var inputType = assembly.GetType($"{ns}.{funcName}Input");
                    var outputType = assembly.GetType($"{ns}.{funcName}Output");
                    if (inputType == null || outputType == null) continue;

                    var isEmptyInput = GetSerializedSize(inputType) == 0;
                    var props = GetInputProperties(inputType);

                    functions.Add(new FunctionInfo
                    {
                        Name = funcName,
                        InputTypeId = inputTypeId,
                        InputType = inputType,
                        OutputType = outputType,
                        IsEmptyInput = isEmptyInput,
                        InputProperties = props
                    });
                }
            }

            contracts.Add(new ContractInfo
            {
                Name = contractName,
                ContractIndex = contractIndex,
                Functions = functions.OrderBy(f => f.InputTypeId).ToList()
            });
        }

        return contracts.OrderBy(c => c.ContractIndex).ToList();
    }

    private static int GetSerializedSize(Type inputType)
    {
        try
        {
            var instance = Activator.CreateInstance(inputType);
            var prop = inputType.GetProperty("SerializedSize");
            if (prop != null)
                return (int)prop.GetValue(instance)!;
        }
        catch { }
        return -1;
    }

    private static List<InputPropertyInfo> GetInputProperties(Type inputType)
    {
        var result = new List<InputPropertyInfo>();
        foreach (var prop in inputType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.Name is "SerializedSize") continue;

            var isRequired = prop.GetCustomAttributes()
                .Any(a => a.GetType().Name == "RequiredMemberAttribute");

            result.Add(new InputPropertyInfo
            {
                Name = prop.Name,
                PropertyType = prop.PropertyType,
                IsRequired = isRequired,
                ReflectionProperty = prop
            });
        }
        return result;
    }

    public static object? CallFromBytes(Type outputType, byte[] data)
    {
        var helper = typeof(ContractDiscovery)
            .GetMethod(nameof(CallFromBytesGeneric), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(outputType);
        return helper.Invoke(null, [data]);
    }

    private static object CallFromBytesGeneric<T>(byte[] data)
        where T : ISmartContractOutput<T>
    {
        return T.FromBytes(data)!;
    }

    public static byte[] CreateInputBytes(Type inputType, Dictionary<string, object?> values)
    {
        var instance = Activator.CreateInstance(inputType)!;

        foreach (var (name, value) in values)
        {
            var prop = inputType.GetProperty(name);
            if (prop != null && value != null)
                prop.SetValue(instance, value);
        }

        var toBytes = inputType.GetMethod("ToBytes")!;
        return (byte[])toBytes.Invoke(instance, null)!;
    }

    public static Dictionary<string, object?> ReadOutputProperties(object output)
    {
        var result = new Dictionary<string, object?>();
        var type = output.GetType();
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            result[prop.Name] = prop.GetValue(output);
        }
        return result;
    }
}
