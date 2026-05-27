using System.Buffers.Binary;
using System.Globalization;
using Qubic.Core;
using Qubic.Core.Entities;

if (args.Length < 2 || args.Contains("--help") || args.Contains("-h"))
{
    Console.Error.WriteLine("""
        qubic-spectrum-diff — compare two spectrum files

        usage:
          qubic-spectrum-diff <spectrum-a> <spectrum-b> [--csv] [--no-empty]
                                                       [--limit N] [--out FILE]

        Each spectrum file is a flat array of 2^24 EntityRecord (64 bytes each, ~1 GB).
        Compared fields: incoming, outgoing, n_incoming, n_outgoing, balance.
        Tick fields (latest-incoming-tick / latest-outgoing-tick) are ignored.
        Output columns: index | identity | field | value-a | value-b | delta
        A "delta" of (a=empty / b=present) means the slot only exists in one file.

          --csv         emit machine-readable CSV (otherwise pretty table)
          --no-empty    skip slots that are empty in both files (default already does this)
          --limit N     stop after reporting N differing slots
          --out FILE    write output to FILE instead of stdout

        examples:
          qubic-spectrum-diff spectrum.179 spectrum.180
          qubic-spectrum-diff a.bin b.bin --csv --out diff.csv
        """);
    return args.Length < 2 ? 1 : 0;
}

var fileA = args[0];
var fileB = args[1];
bool csv = false;
long limit = long.MaxValue;
string? outPath = null;

for (int i = 2; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--csv": csv = true; break;
        case "--no-empty": /* default behavior; flag kept for clarity */ break;
        case "--limit" when i + 1 < args.Length:
            limit = long.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--out" when i + 1 < args.Length:
            outPath = args[++i];
            break;
        default:
            Console.Error.WriteLine($"unknown arg: {args[i]}");
            return 2;
    }
}

const int RecordSize = QubicConstants.EntityRecordSize; // 64
const int SlotCount = QubicConstants.SpectrumCapacity;  // 1 << 24
const long ExpectedSize = (long)SlotCount * RecordSize;

foreach (var (label, path) in new[] { ("A", fileA), ("B", fileB) })
{
    var info = new FileInfo(path);
    if (!info.Exists)
    {
        Console.Error.WriteLine($"{label}: file not found: {path}");
        return 3;
    }
    if (info.Length != ExpectedSize)
    {
        Console.Error.WriteLine(
            $"{label}: file {path} is {info.Length} bytes; expected {ExpectedSize} " +
            $"({SlotCount} x {RecordSize})");
        return 3;
    }
}

using var outStream = outPath is null
    ? Console.Out
    : new StreamWriter(File.Create(outPath));

if (csv)
{
    outStream.WriteLine("index,identity,field,value_a,value_b,delta");
}
else
{
    outStream.WriteLine($"A: {fileA}");
    outStream.WriteLine($"B: {fileB}");
    outStream.WriteLine(new string('-', 80));
}

// Stream both files in lockstep. 1 MiB chunks = 16,384 records per chunk.
const int RecordsPerChunk = 16384;
const int ChunkBytes = RecordsPerChunk * RecordSize;

var bufA = new byte[ChunkBytes];
var bufB = new byte[ChunkBytes];

using var streamA = new FileStream(fileA, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBytes, FileOptions.SequentialScan);
using var streamB = new FileStream(fileB, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBytes, FileOptions.SequentialScan);

long differingSlots = 0;
long emptyBoth = 0;
long onlyInA = 0;
long onlyInB = 0;
long bothPresentDiffer = 0;
long totalSlots = 0;

int slotIndex = 0;
var progressEvery = TimeSpan.FromSeconds(2);
var start = DateTime.UtcNow;
var nextProgress = start + progressEvery;

while (slotIndex < SlotCount)
{
    ReadExact(streamA, bufA);
    ReadExact(streamB, bufB);

    var spanA = bufA.AsSpan();
    var spanB = bufB.AsSpan();

    for (int r = 0; r < RecordsPerChunk; r++, slotIndex++)
    {
        totalSlots++;
        var recA = spanA.Slice(r * RecordSize, RecordSize);
        var recB = spanB.Slice(r * RecordSize, RecordSize);

        var emptyA = IsEmpty(recA);
        var emptyB = IsEmpty(recB);

        if (emptyA && emptyB)
        {
            emptyBoth++;
            continue;
        }

        // Compare only the 5 fields we care about: incoming, outgoing,
        // n_incoming, n_outgoing, balance. Tick fields are ignored.
        var ea = ParseRecord(recA);
        var eb = ParseRecord(recB);
        if (!emptyA && !emptyB
            && ea.Incoming    == eb.Incoming
            && ea.Outgoing    == eb.Outgoing
            && ea.NumIncoming == eb.NumIncoming
            && ea.NumOutgoing == eb.NumOutgoing)
        {
            // Balance is derived; if incoming & outgoing match, balance matches.
            continue;
        }

        differingSlots++;
        if (emptyA) onlyInB++;
        else if (emptyB) onlyInA++;
        else bothPresentDiffer++;

        // Use the populated side's identity (or A if both populated).
        var identityBytes = emptyA ? recB[..32].ToArray() : recA[..32].ToArray();
        string identity;
        try
        {
            identity = QubicIdentity.FromPublicKey(identityBytes).Identity;
        }
        catch
        {
            identity = "<invalid-pubkey>";
        }

        ReportSlotDiff(outStream, csv, slotIndex, identity, recA, recB, emptyA, emptyB);

        if (differingSlots >= limit)
        {
            outStream.WriteLine();
            outStream.WriteLine($"-- stopped at --limit {limit} --");
            goto done;
        }
    }

    if (DateTime.UtcNow >= nextProgress)
    {
        double pct = 100.0 * slotIndex / SlotCount;
        Console.Error.WriteLine(
            $"  scanned {slotIndex:N0} / {SlotCount:N0} ({pct:F1}%) — diffs so far: {differingSlots:N0}");
        nextProgress = DateTime.UtcNow + progressEvery;
    }
}

done:
outStream.Flush();

var elapsed = DateTime.UtcNow - start;
Console.Error.WriteLine();
Console.Error.WriteLine($"scanned {totalSlots:N0} slots in {elapsed.TotalSeconds:F1}s");
Console.Error.WriteLine($"  differing slots:        {differingSlots:N0}");
Console.Error.WriteLine($"    only in A (B empty):  {onlyInA:N0}");
Console.Error.WriteLine($"    only in B (A empty):  {onlyInB:N0}");
Console.Error.WriteLine($"    both populated:       {bothPresentDiffer:N0}");
Console.Error.WriteLine($"  empty-in-both (ignored): {emptyBoth:N0}");

return differingSlots == 0 ? 0 : 1;

static void ReadExact(Stream s, byte[] buf)
{
    int total = 0;
    while (total < buf.Length)
    {
        var n = s.Read(buf, total, buf.Length - total);
        if (n == 0) throw new EndOfStreamException();
        total += n;
    }
}

// An entity slot is considered empty when its 32-byte public key is all zero.
static bool IsEmpty(ReadOnlySpan<byte> record)
{
    var pk = record[..32];
    for (int i = 0; i < pk.Length; i++)
        if (pk[i] != 0) return false;
    return true;
}

static void ReportSlotDiff(
    TextWriter w, bool csv, int slotIndex, string identity,
    ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, bool emptyA, bool emptyB)
{
    // Parse all six numeric fields from each side.
    var ea = ParseRecord(a);
    var eb = ParseRecord(b);

    // If one side is empty, surface a single "slot" row so user sees presence flip.
    if (emptyA || emptyB)
    {
        var presence = emptyA ? "(empty -> present)" : "(present -> empty)";
        if (csv)
        {
            w.WriteLine($"{slotIndex},{identity},slot,{(emptyA ? "empty" : "present")},{(emptyB ? "empty" : "present")},{presence}");
        }
        else
        {
            w.WriteLine($"[{slotIndex,10}] {identity}  {presence}");
        }
        WriteField(w, csv, slotIndex, identity, "incoming",   emptyA ? 0 : ea.Incoming,    emptyA ? eb.Incoming    : 0);
        WriteField(w, csv, slotIndex, identity, "outgoing",   emptyA ? 0 : ea.Outgoing,    emptyA ? eb.Outgoing    : 0);
        WriteField(w, csv, slotIndex, identity, "n_incoming", emptyA ? 0 : ea.NumIncoming, emptyA ? eb.NumIncoming : 0);
        WriteField(w, csv, slotIndex, identity, "n_outgoing", emptyA ? 0 : ea.NumOutgoing, emptyA ? eb.NumOutgoing : 0);
        WriteField(w, csv, slotIndex, identity, "balance",    emptyA ? 0 : ea.Balance,     emptyA ? eb.Balance     : 0);
        if (!csv) w.WriteLine();
        return;
    }

    if (!csv)
        w.WriteLine($"[{slotIndex,10}] {identity}");

    if (ea.Incoming    != eb.Incoming)    WriteField(w, csv, slotIndex, identity, "incoming",   ea.Incoming,    eb.Incoming);
    if (ea.Outgoing    != eb.Outgoing)    WriteField(w, csv, slotIndex, identity, "outgoing",   ea.Outgoing,    eb.Outgoing);
    if (ea.NumIncoming != eb.NumIncoming) WriteField(w, csv, slotIndex, identity, "n_incoming", ea.NumIncoming, eb.NumIncoming);
    if (ea.NumOutgoing != eb.NumOutgoing) WriteField(w, csv, slotIndex, identity, "n_outgoing", ea.NumOutgoing, eb.NumOutgoing);
    if (ea.Balance     != eb.Balance)     WriteField(w, csv, slotIndex, identity, "balance",    ea.Balance,     eb.Balance);

    if (!csv) w.WriteLine();
}

static void WriteField(TextWriter w, bool csv, int slotIndex, string identity, string field, long a, long b)
{
    var delta = b - a;
    if (csv)
    {
        w.WriteLine($"{slotIndex},{identity},{field},{a},{b},{(delta >= 0 ? "+" : "")}{delta}");
    }
    else
    {
        w.WriteLine($"  {field,-10}  A={a,22:N0}  B={b,22:N0}  Δ={(delta >= 0 ? "+" : "")}{delta:N0}");
    }
}

static ParsedRecord ParseRecord(ReadOnlySpan<byte> r) => new()
{
    Incoming    = BinaryPrimitives.ReadInt64LittleEndian(r.Slice(32, 8)),
    Outgoing    = BinaryPrimitives.ReadInt64LittleEndian(r.Slice(40, 8)),
    NumIncoming = BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(48, 4)),
    NumOutgoing = BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(52, 4)),
};

internal record struct ParsedRecord
{
    public long Incoming;
    public long Outgoing;
    public uint NumIncoming;
    public uint NumOutgoing;
    public long Balance => Incoming - Outgoing;
}
