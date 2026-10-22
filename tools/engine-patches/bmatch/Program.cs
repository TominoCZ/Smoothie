// Native code tools for a UnityPlayer.dll without symbols (needs SPT_PATH for Iced when building).
//   bmatch <A.dll> <A publics.txt> <B.dll> [rvaA ...]   match functions of build B to named build A;
//       DUMP=file writes every pair as "rvaA rvaB nameA" (a publics.txt for B: columns 2 and 3).
//       Function ranges from .pdata. Rounds: (1) exact normalised code (RIP-relative operands and
//       branches outside the function masked), (2) mnemonic sequence only, (3) referenced string
//       sets, (4) call-graph propagation: matched pairs with the same number of direct calls pair
//       their callees in order. Only a third of 2022.3.43f2 matches 43f1 this way: use it for
//       hints, and check every function that matters by hand.
//   bmatch xref <dll> <rva>...       every instruction whose RIP-relative operand or branch target is one of these
//   bmatch xstr <dll> <substring>    every instruction referencing a string containing it
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using Iced.Intel;

if (args[0] == "xref")
{
    // xref <dll> <rva hex>...: instructions whose RIP-relative operand or branch target is one of these.
    var bin = new Bin(args[1]);
    var want = args.Skip(2).Select(x => Convert.ToInt64(x, 16)).ToHashSet();
    foreach (var start in bin.Starts)
    {
        var code = bin.Instructions(start);
        for (var i = 0; i < code.Count; i++)
        {
            var target = code[i].IsIPRelativeMemoryOperand ? (long)code[i].IPRelativeMemoryAddress : code[i].Op0Kind == OpKind.NearBranch64 ? (long)code[i].NearBranchTarget : -1;
            if (!want.Contains(target)) continue;
            Console.WriteLine($"0x{target:X} used in func 0x{start:X}:");
            for (var k = Math.Max(0, i - 4); k < Math.Min(code.Count, i + 5); k++) Console.WriteLine($"   {code[k].IP:X}  {code[k]}");
        }
    }
    return;
}
if (args[0] == "xstr")
{
    // xstr <dll> <substring>: every instruction referencing a string containing it, with the
    // instructions around it (an icall's registration pairs the name with its function).
    var bin = new Bin(args[1]);
    foreach (var (rva, text) in bin.FindStrings(args[2]))
    foreach (var start in bin.Starts)
    {
        var code = bin.Instructions(start);
        for (var i = 0; i < code.Count; i++)
        {
            if (!code[i].IsIPRelativeMemoryOperand || (long)code[i].IPRelativeMemoryAddress != rva) continue;
            Console.WriteLine($"\"{text}\" @0x{rva:X} used in func 0x{start:X}:");
            for (var k = Math.Max(0, i - 3); k < Math.Min(code.Count, i + 4); k++)
                Console.WriteLine($"   {code[k].IP:X}  {code[k]}");
        }
    }
    return;
}
var a = new Bin(args[0]); var b = new Bin(args[2]);
var names = File.ReadLines(args[1]).Select(l => l.Split(' ', 2)).Where(p => p.Length == 2)
    .Select(p => (Rva: Convert.ToInt64(p[0], 16), Name: p[1])).OrderBy(p => p.Rva).ToArray();
var pairs = new Dictionary<long, long>(); var taken = new HashSet<long>();
void Round(string what, Func<Bin, long, string> key)
{
    var ka = a.Starts.Where(s => !pairs.ContainsKey(s)).AsParallel().Select(s => (s, k: key(a, s))).Where(x => x.k != null).ToList();
    var kb = b.Starts.Where(s => !taken.Contains(s)).AsParallel().Select(s => (s, k: key(b, s))).Where(x => x.k != null).ToList();
    var ua = ka.GroupBy(x => x.k).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First().s);
    var ub = kb.GroupBy(x => x.k).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First().s);
    var n = 0;
    foreach (var (k, s) in ua) if (ub.TryGetValue(k, out var t) && taken.Add(t)) { pairs[s] = t; n++; }
    Console.WriteLine($"{what}: +{n}, {pairs.Count} ({100.0 * pairs.Count / a.Starts.Count:0.0}%)");
}
Round("exact", (bin, s) => bin.Hash(s, true));
Round("mnemonics", (bin, s) => bin.Hash(s, false));
Round("strings", (bin, s) => bin.Strings(s) is { Length: > 0 } str ? str + "|" + bin.Calls(s).Count : null);
for (var pass = 0; pass < 20; pass++)
{
    var n = 0;
    foreach (var (s, t) in pairs.ToList())
    {
        var ca = a.Calls(s); var cb = b.Calls(t);
        if (ca.Count != cb.Count) continue;
        for (var i = 0; i < ca.Count; i++)
        {
            if (pairs.TryGetValue(ca[i], out var have)) continue;
            if (taken.Contains(cb[i]) || !a.IsStart(ca[i]) || !b.IsStart(cb[i])) continue;
            pairs[ca[i]] = cb[i]; taken.Add(cb[i]); n++;
        }
    }
    Console.WriteLine($"calls pass {pass}: +{n}, {pairs.Count} ({100.0 * pairs.Count / a.Starts.Count:0.0}%)");
    if (n == 0) break;
}
string NameOf(long rva) { var i = Array.FindLastIndex(names, n => n.Rva <= rva); return i < 0 ? "?" : $"{names[i].Name}+0x{rva - names[i].Rva:X}"; }
foreach (var s in args.Skip(3))
{
    var rva = Convert.ToInt64(s, 16);
    var f = a.FunctionAt(rva);
    if (f == null) { Console.WriteLine($"{s}: no function"); continue; }
    var (start, end) = f.Value;
    Console.WriteLine($"{s}: A 0x{start:X}-0x{end:X} {NameOf(start)} (+0x{rva - start:X})" +
        (pairs.TryGetValue(start, out var bs) ? $" -> B 0x{bs:X} (size A {end - start:X}, B {b.FunctionAt(bs)!.Value.Item2 - bs:X}){(a.Hash(start, true) == b.Hash(bs, true) ? " identical" : a.Hash(start, false) == b.Hash(bs, false) ? " same mnemonics" : " differs")}" : " -> no match"));
}
if (Environment.GetEnvironmentVariable("DUMP") is { } dump)
    File.WriteAllLines(dump, pairs.OrderBy(p => p.Key).Select(p => $"{p.Key:X8} {p.Value:X8} {NameOf(p.Key)}"));

class Bin
{
    readonly byte[] img; readonly PEReader pe; readonly List<(long S, long E)> funcs = new();
    readonly HashSet<long> starts = new(); readonly Dictionary<long, long> ends = new();
    public List<long> Starts => funcs.Select(f => f.S).ToList();
    public Bin(string path)
    {
        pe = new PEReader(new MemoryStream(File.ReadAllBytes(path)));
        img = pe.GetEntireImage().GetContent().ToArray();
        var dir = pe.PEHeaders.PEHeader!.ExceptionTableDirectory;
        var o = FileOff(dir.RelativeVirtualAddress);
        for (var i = 0; i < dir.Size / 12; i++)
        {
            long s = BitConverter.ToUInt32(img, o + i * 12), e = BitConverter.ToUInt32(img, o + i * 12 + 4);
            if (starts.Add(s)) { funcs.Add((s, e)); ends[s] = e; }
        }
        funcs.Sort();
    }
    public bool IsStart(long rva) => starts.Contains(rva);
    public List<Instruction> Instructions(long s) => Code(s).ToList();
    public IEnumerable<(long Rva, string Text)> FindStrings(string part)
    {
        var needle = Encoding.ASCII.GetBytes(part);
        foreach (var sec in pe.PEHeaders.SectionHeaders)
        {
            if (sec.Name != ".rdata") continue;
            int o = sec.PointerToRawData, end = o + sec.SizeOfRawData;
            for (var i = img.AsSpan(o, end - o).IndexOf(needle); i >= 0; )
            {
                var at = o + i; var s0 = at; while (s0 > o && img[s0 - 1] >= 0x20 && img[s0 - 1] < 0x7f) s0--;
                var e = at; while (e < end && img[e] != 0) e++;
                yield return (s0 - sec.PointerToRawData + sec.VirtualAddress, Encoding.ASCII.GetString(img, s0, e - s0));
                var next = img.AsSpan(at + 1, end - at - 1).IndexOf(needle); if (next < 0) break; i = at + 1 - o + next;
            }
        }
    }
    public int FileOff(long rva) { foreach (var s in pe.PEHeaders.SectionHeaders) if (rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData)) return (int)(rva - s.VirtualAddress + s.PointerToRawData); return -1; }
    public (long, long)? FunctionAt(long rva) { var i = funcs.BinarySearch((rva, long.MaxValue)); i = i < 0 ? ~i - 1 : i; return i >= 0 && rva < funcs[i].E ? funcs[i] : null; }
    IEnumerable<Instruction> Code(long s)
    {
        var e = ends[s]; var o = FileOff(s); if (o < 0) yield break;
        var dec = Iced.Intel.Decoder.Create(64, new ByteArrayCodeReader(img, o, (int)(e - s))); dec.IP = (ulong)s;
        while (dec.IP < (ulong)e) { dec.Decode(out var ins); yield return ins; }
    }
    readonly System.Collections.Concurrent.ConcurrentDictionary<long, List<long>> calls = new();
    public List<long> Calls(long s) => calls.GetOrAdd(s, _ => Code(s).Where(i => (i.FlowControl == FlowControl.Call || i.FlowControl == FlowControl.UnconditionalBranch) && i.Op0Kind == OpKind.NearBranch64 && ((long)i.NearBranchTarget < s || (long)i.NearBranchTarget >= ends[s])).Select(i => (long)i.NearBranchTarget).ToList());
    public string Strings(long s)
    {
        var list = new List<string>();
        foreach (var i in Code(s))
        {
            if (!i.IsIPRelativeMemoryOperand) continue;
            var o = FileOff((long)i.IPRelativeMemoryAddress); if (o < 0) continue;
            var n = 0; while (o + n < img.Length && n < 200 && img[o + n] >= 0x20 && img[o + n] < 0x7f) n++;
            if (n >= 6 && o + n < img.Length && img[o + n] == 0) list.Add(Encoding.ASCII.GetString(img, o, n));
        }
        return string.Join("\u0001", list);
    }
    public string Hash(long s, bool full)
    {
        var e = ends[s]; var sb = new StringBuilder();
        foreach (var ins in Code(s))
        {
            sb.Append(full ? ((int)ins.Code).ToString() : ((int)ins.Mnemonic).ToString()).Append(',');
            if (!full) continue;
            for (var k = 0; k < ins.OpCount; k++)
            {
                var kind = ins.GetOpKind(k);
                sb.Append((int)kind).Append(':');
                switch (kind)
                {
                    case OpKind.Register: sb.Append((int)ins.GetOpRegister(k)); break;
                    case OpKind.NearBranch64: var t = (long)ins.NearBranchTarget; sb.Append(t >= s && t < e ? (t - s).ToString() : "X"); break;
                    case OpKind.Memory:
                        sb.Append((int)ins.MemoryBase).Append('/').Append((int)ins.MemoryIndex).Append('/').Append(ins.MemoryIndexScale).Append('/')
                          .Append(ins.IsIPRelativeMemoryOperand ? "R" : ins.MemoryDisplacement64.ToString()); break;
                    case OpKind.Immediate8 or OpKind.Immediate8_2nd or OpKind.Immediate16 or OpKind.Immediate32 or OpKind.Immediate8to16 or OpKind.Immediate8to32 or OpKind.Immediate8to64 or OpKind.Immediate32to64: sb.Append(ins.GetImmediate(k)); break;
                }
                sb.Append(' ');
            }
            sb.Append(';');
        }
        return Convert.ToHexString(SHA1.HashData(Encoding.ASCII.GetBytes(sb.ToString())));
    }
}
