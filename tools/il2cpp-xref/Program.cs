// Offline call graph for the IL2CPP game: interop method <-> GameAssembly RVA (BepInEx's
// MethodAddressToToken.db), call targets by disassembling GameAssembly.dll with Iced. For checking
// that a method Smoothie patches with Harmony is still called on its own after a game update:
// IL2CPP's compiler inlines small methods into their callers (a patch on one never runs) and folds
// identical ones into one address (a patch on one runs for all; "SHARED" in the output).
// Usage (SPT_PATH = the SPT 5.0 folder, after BepInEx has generated BepInEx/interop):
//   dotnet run -- callees <Type> <Method> | callers <Type> <Method> | addr <Type> <Method> | triggers x x
//   dotnet run -- publics x x > GameAssembly.publics.txt  (every method by RVA, for dis.sh and stacks.py)
//   (Type: full name or the last part; "triggers" lists every type declaring Unity trigger messages)
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Iced.Intel;

var game = Environment.GetEnvironmentVariable("SPT_PATH") ?? throw new InvalidOperationException("set SPT_PATH to the SPT 5.0 folder");
var interop = Path.Combine(game, "BepInEx/interop");
var db = File.ReadAllBytes(Path.Combine(interop, "MethodAddressToToken.db"));
int na = BitConverter.ToInt32(db, 8), nm = BitConverter.ToInt32(db, 12), off = BitConverter.ToInt32(db, 16);
var names = new List<string>();
using (var br = new BinaryReader(new MemoryStream(db, 20, off - 20))) for (var i = 0; i < na; i++) names.Add(br.ReadString());
// Method def token -> "Namespace.Type.Method" per interop assembly, straight from metadata.
var methodNames = names.Select(n =>
{
    var file = Path.Combine(interop, n.Split(',')[0] + ".dll");
    var map = new Dictionary<int, (string Type, string Method)>();
    if (!File.Exists(file)) return map;
    using var r = new PEReader(File.OpenRead(file));
    var md = r.GetMetadataReader();
    foreach (var th in md.TypeDefinitions)
    {
        var td = md.GetTypeDefinition(th);
        var type = md.GetString(td.Name);
        for (var d = td; d.GetDeclaringType() is { IsNil: false } outer; ) { d = md.GetTypeDefinition(outer); type = md.GetString(d.Name) + "+" + type; td = d; }
        var ns = md.GetString(td.Namespace);
        if (ns.Length > 0) type = ns + "." + type;
        foreach (var mh in md.GetTypeDefinition(th).GetMethods())
            map[MetadataTokens.GetToken(mh)] = (type, md.GetString(md.GetMethodDefinition(mh).Name));
    }
    return map;
}).ToList();
var rva = new long[nm]; var tok = new int[nm]; var idx = new int[nm];
for (var i = 0; i < nm; i++)
{
    rva[i] = BitConverter.ToInt64(db, off + i * 8);
    tok[i] = BitConverter.ToInt32(db, off + nm * 8 + i * 8);
    idx[i] = BitConverter.ToInt32(db, off + nm * 8 + i * 8 + 4);
}
string Name(int i) => methodNames[idx[i]].TryGetValue(tok[i], out var m) ? $"{m.Type}.{m.Method}" : $"tok {tok[i]:X}";
var byRva = new Dictionary<long, List<int>>();
for (var i = 0; i < nm; i++) { if (!byRva.TryGetValue(rva[i], out var l)) byRva[rva[i]] = l = new(); l.Add(i); }
var starts = byRva.Keys.OrderBy(x => x).ToArray();

var pe = new PEReader(File.OpenRead(Path.Combine(game, "GameAssembly.dll")));
var image = pe.GetEntireImage().GetContent().ToArray();
var sections = pe.PEHeaders.SectionHeaders;
int FileOff(long r) { foreach (var s in sections) if (r >= s.VirtualAddress && r < s.VirtualAddress + s.VirtualSize) return (int)(r - s.VirtualAddress + s.PointerToRawData); return -1; }

// A function runs to the next known method start (at most 64 KB).
IEnumerable<(long At, long Target)> Calls(long start)
{
    var next = Array.BinarySearch(starts, start); next = next < 0 ? ~next : next + 1;
    var end = next < starts.Length ? Math.Min(starts[next], start + 65536) : start + 65536;
    var fo = FileOff(start); if (fo < 0) yield break;
    var dec = Iced.Intel.Decoder.Create(64, new ByteArrayCodeReader(image, fo, (int)(end - start)));
    dec.IP = (ulong)start;
    while (dec.IP < (ulong)end)
    {
        dec.Decode(out var ins);
        if (ins.IsInvalid) break;
        if ((ins.FlowControl == FlowControl.Call || ins.FlowControl == FlowControl.UnconditionalBranch) && ins.Op0Kind == OpKind.NearBranch64)
            yield return ((long)ins.IP, (long)ins.NearBranchTarget);
    }
}
IEnumerable<int> Find(string type, string method)
{
    for (var i = 0; i < nm; i++)
        if (methodNames[idx[i]].TryGetValue(tok[i], out var m) && m.Method == method && (m.Type == type || m.Type.EndsWith("." + type) || m.Type.EndsWith("+" + type))) yield return i;
}
string Label(long target) => byRva.TryGetValue(target, out var l) ? string.Join(" | ", l.Take(3).Select(Name)) + (l.Count > 3 ? $" (+{l.Count - 3} shared)" : "") : $"0x{target:X}";

if (args[0] == "triggers")
{
    foreach (var file in Directory.GetFiles(interop, "*.dll"))
    {
        using var r = new PEReader(File.OpenRead(file));
        if (!r.HasMetadata) continue;
        var md = r.GetMetadataReader();
        foreach (var th in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(th);
            var names2 = td.GetMethods().Select(h => md.GetString(md.GetMethodDefinition(h).Name)).Where(n => n is "OnTriggerEnter" or "OnTriggerStay" or "OnTriggerExit").ToList();
            if (names2.Count == 0) continue;
            var ifaces = td.GetInterfaceImplementations().Select(h => md.GetInterfaceImplementation(h).Interface).Select(e => e.Kind == HandleKind.TypeReference ? md.GetString(md.GetTypeReference((TypeReferenceHandle)e).Name) : e.Kind == HandleKind.TypeDefinition ? md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)e).Name) : "?").ToList();
            var ns = md.GetString(td.Namespace); var nm2 = md.GetString(td.Name);
            if (!td.GetDeclaringType().IsNil) nm2 = md.GetString(md.GetTypeDefinition(td.GetDeclaringType()).Name) + "+" + nm2;
            Console.WriteLine($"{Path.GetFileName(file)} {(ns.Length > 0 ? ns + "." : "")}{nm2} [{string.Join(",", names2)}] ifaces: {string.Join(",", ifaces)}");
        }
    }
    return;
}
if (args[0] == "publics")
{
    // Every method as "RVA Type.Method", sorted: a publics.txt for dis.sh and stacks.py --syms.
    foreach (var s in starts) Console.WriteLine($"{s:X8} {string.Join(" | ", byRva[s].Take(3).Select(Name))}");
    return;
}
var (cmd, t, mth) = (args[0], args[1], args[2]);
var hits = Find(t, mth).ToList();
if (hits.Count == 0) { Console.WriteLine("not found"); return; }
foreach (var h in hits)
{
    Console.WriteLine($"== {Name(h)} @ 0x{rva[h]:X}" + (byRva[rva[h]].Count > 1 ? $" SHARED with {byRva[rva[h]].Count - 1}: {string.Join(" | ", byRva[rva[h]].Where(i => i != h).Take(4).Select(Name))}" : ""));
    if (cmd == "callees")
        foreach (var (at, target) in Calls(rva[h]).DistinctBy(c => c.Target)) Console.WriteLine($"  -> {Label(target)}");
    if (cmd == "callers")
        foreach (var s in starts)
            foreach (var (at, target) in Calls(s))
                if (target == rva[h]) { Console.WriteLine($"  <- {Label(s)}"); break; }
}
