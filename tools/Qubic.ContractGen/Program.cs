using Qubic.ContractGen.Generation;
using Qubic.ContractGen.Parsing;

// Contract definitions: (index, csharpName, cppStructName, headerFile)
// From deps/qubic-core/src/contract_core/contract_def.h
var contracts = new (int Index, string CSharpName, string CppStruct, string? HeaderFile)[]
{
    (0,  "Core",     "Contract0State", null),        // No user functions
    (1,  "Qx",       "QX",        "Qx.h"),
    (2,  "Quottery", "QUOTTERY",  "Quottery.h"),
    (3,  "Random",   "RANDOM",    "Random.h"),
    (4,  "Qutil",    "QUTIL",     "QUtil.h"),
    (5,  "Mlm",      "MLM",       "MyLastMatch.h"),
    (6,  "Gqmprop",  "GQMPROP",   "GeneralQuorumProposal.h"),
    (7,  "Swatch",   "SWATCH",    "SupplyWatcher.h"),
    (8,  "Ccf",      "CCF",       "ComputorControlledFund.h"),
    (9,  "Qearn",    "QEARN",     "Qearn.h"),
    (10, "Qvault",   "QVAULT",    "QVAULT.h"),
    (11, "Msvault",  "MSVAULT",   "MsVault.h"),
    (12, "Qbay",     "QBAY",      "Qbay.h"),
    (13, "Qswap",    "QSWAP",     "Qswap.h"),
    (14, "Nost",     "NOST",      "Nostromo.h"),
    (15, "Qdraw",    "QDRAW",     "Qdraw.h"),
    (16, "Rl",       "RL",        "RandomLottery.h"),
    (17, "Qbond",    "QBOND",     "QBond.h"),
    (18, "Qip",      "QIP",       "QIP.h"),
    (19, "Qraffle",  "QRAFFLE",   "QRaffle.h"),
    (20, "Qrwa",     "QRWA",      "qRWA.h"),
    (21, "Qrp",      "QRP",       "QReservePool.h"),
    (22, "Qtf",      "QTF",       "QThirtyFour.h"),
    (23, "Qduel",    "QDUEL",     "QDuel.h"),
};

// Resolve paths
var toolDir = AppContext.BaseDirectory;
var repoRoot = args.Length > 0 ? args[0] : FindRepoRoot(toolDir);
var headersDir = Path.Combine(repoRoot, "deps", "qubic-core", "src", "contracts");
var outputDir = Path.Combine(repoRoot, "src", "Qubic.Core", "Contracts", "Generated");

if (!Directory.Exists(headersDir))
{
    Console.Error.WriteLine($"ERROR: Headers directory not found: {headersDir}");
    Console.Error.WriteLine("Make sure the qubic-core submodule is initialized.");
    return 1;
}

Directory.CreateDirectory(outputDir);

Console.WriteLine($"Headers: {headersDir}");
Console.WriteLine($"Output:  {outputDir}");
Console.WriteLine();

var emitter = new CSharpEmitter();
int successCount = 0;
int skipCount = 0;
var allWarnings = new List<(string Contract, string Warning)>();

foreach (var (index, csName, cppStruct, headerFile) in contracts)
{
    if (headerFile == null)
    {
        Console.WriteLine($"  [{index:D2}] {csName,-12} SKIP (no header)");
        skipCount++;
        continue;
    }

    var headerPath = Path.Combine(headersDir, headerFile);
    if (!File.Exists(headerPath))
    {
        Console.WriteLine($"  [{index:D2}] {csName,-12} SKIP (file not found: {headerFile})");
        allWarnings.Add((csName, $"Header file not found: {headerFile}"));
        skipCount++;
        continue;
    }

    try
    {
        var parser = new CppHeaderParser();
        var contract = parser.Parse(headerPath, index, csName, cppStruct);

        var code = emitter.Emit(contract);
        var outputPath = Path.Combine(outputDir, $"{csName}.g.cs");
        File.WriteAllText(outputPath, code);

        var funcCount = contract.Functions.Count;
        var procCount = contract.Procedures.Count;
        Console.WriteLine($"  [{index:D2}] {csName,-12} OK  ({funcCount} functions, {procCount} procedures)");

        foreach (var warning in parser.Warnings)
        {
            allWarnings.Add((csName, warning));
        }

        successCount++;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  [{index:D2}] {csName,-12} ERROR: {ex.Message}");
        allWarnings.Add((csName, $"Parse error: {ex.Message}"));
    }
}

Console.WriteLine();
Console.WriteLine($"Generated {successCount} files, skipped {skipCount}.");

if (allWarnings.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Warnings:");
    foreach (var (contract, warning) in allWarnings)
    {
        Console.WriteLine($"  [{contract}] {warning}");
    }
}

return 0;

static string FindRepoRoot(string startDir)
{
    var dir = startDir;
    while (dir != null)
    {
        if (Directory.Exists(Path.Combine(dir, "deps", "qubic-core"))
            || File.Exists(Path.Combine(dir, "Qubic.Net.sln")))
            return dir;

        // Try parent
        var parent = Directory.GetParent(dir)?.FullName;
        if (parent == dir) break;
        dir = parent;
    }
    // Default: assume we're in repo root or passed as arg
    return Directory.GetCurrentDirectory();
}
