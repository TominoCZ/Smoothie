// Lists interop methods Smoothie.dll calls that are (or reach, within 3 calls) Il2CppInterop's
// "Method unstripping failed" stubs: Unity/game C# the game's build stripped (nothing in the game
// used it), which only throws once called, in game. Also rebuilt methods that add IL2CPP's string
// data offset to a pinned .NET string (they hand Unity a pointer past the text; ProfilerRecorder.StartNew).
// Any hit needs another way in (an internal call, see DepthCulling.Readback).
// Expected output: "0 stripped call targets" (and the off-main-thread check at the end).
// Usage: dotnet run -p:SptPath=$SPT_PATH -- src/Smoothie/bin/Release/net10.0/Smoothie.dll
using System; using System.IO; using System.Linq; using System.Collections.Generic; using Mono.Cecil; using Mono.Cecil.Cil;
var spt = Environment.GetEnvironmentVariable("SPT_PATH") ?? throw new InvalidOperationException("Set SPT_PATH to the SPT 5.0 folder");
var interop = Path.Combine(spt, "BepInEx/interop");
var resolver = new DefaultAssemblyResolver();
foreach (var d in new[] { interop, Path.Combine(spt, "BepInEx/core"), Path.Combine(spt, "BepInEx/plugins/sptushonka"), System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory() }) resolver.AddSearchDirectory(d);
var smoothie = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver });
var memo = new Dictionary<string, string>();
string Stripped(MethodDefinition m, int depth)
{
    if (m == null || !m.HasBody) return null;
    var key = m.FullName;
    if (memo.TryGetValue(key, out var known)) return known;
    memo[key] = null;
    string found = null;
    foreach (var i in m.Body.Instructions)
    {
        // The stub's text: "Method unstripping failed" up to BepInXDX be.9, "... was stripped from this game ..." after.
        if (i.OpCode == OpCodes.Ldstr && i.Operand is string text && (text == "Method unstripping failed" || text.Contains("stripped from this game"))) { found = m.FullName; break; }
        // Rebuilt code that pins a .NET string and adds IL2CPP's string data offset: points past the text.
        if (i.Operand is MethodReference g && g.Name == "get_OffsetToStringData" && g.DeclaringType.FullName.StartsWith("Il2CppSystem.")) { found = m.FullName + "  [IL2CPP string offset on a .NET string]"; break; }
        if (depth <= 0 || i.Operand is not MethodReference r) continue;
        if (i.OpCode != OpCodes.Call && i.OpCode != OpCodes.Callvirt && i.OpCode != OpCodes.Newobj) continue;
        if (!File.Exists(Path.Combine(interop, r.DeclaringType.Scope.Name.Replace(".dll", "") + ".dll"))) continue;
        {
            MethodDefinition d = null; try { d = r.Resolve(); } catch { }
            var inner = Stripped(d, depth - 1);
            if (inner != null) { found = inner == m.FullName ? m.FullName : $"{m.FullName}\n        -> {inner}"; break; }
        }
    }
    memo[key] = found;
    return found;
}
var hits = new SortedDictionary<string, SortedSet<string>>();
foreach (var t in smoothie.MainModule.GetTypes())
foreach (var m in t.Methods.Where(m => m.HasBody))
foreach (var i in m.Body.Instructions)
{
    if (i.Operand is not MethodReference r) continue;
    var scope = r.DeclaringType.Scope.Name.Replace(".dll", "");
    if (!File.Exists(Path.Combine(interop, scope + ".dll"))) continue;
    MethodDefinition d = null; try { d = r.Resolve(); } catch { }
    var s = Stripped(d, 3);
    if (s == null) continue;
    if (!hits.TryGetValue(s, out var users)) hits[s] = users = new();
    users.Add($"{t.Name}.{m.Name}");
}
foreach (var pair in hits) Console.WriteLine($"{pair.Key}\n    used by: {string.Join(", ", pair.Value)}");
Console.WriteLine($"{hits.Count} stripped call targets");

// Code run on threads IL2CPP doesn't know (DepthCulling's worker) must not reach an interop method
// that goes through il2cpp_runtime_invoke: it boxes its result on the game's heap, and a collection
// started there kills the game ("Fatal error in GC: Collecting from unknown thread"). Unity's
// struct math (Vector3 operators, constructors, magnitude) is all of that kind. Getters with a
// direct call pointer (plain field reads, e.g. CullingObject.IsVisible) are left out.
// Expected output: "0 runtime_invoke calls off the main thread".
bool Invokes(MethodDefinition d) => d is { HasBody: true }
    && d.Body.Instructions.Any(i => i.Operand is MethodReference c && c.Name == "il2cpp_runtime_invoke")
    && !d.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name.StartsWith("NativeDirectCallPtr_"));
var walked = new HashSet<string>();
var offMain = new SortedDictionary<string, SortedSet<string>>();
var queue = new Queue<MethodDefinition>(smoothie.MainModule.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody && $"{m.DeclaringType.Name}.{m.Name}" == "DepthCulling.Test"));
while (queue.Count > 0)
{
    var m = queue.Dequeue();
    if (!walked.Add(m.FullName)) continue;
    foreach (var i in m.Body.Instructions)
    {
        if (i.Operand is not MethodReference r) continue;
        MethodDefinition d = null; try { d = r.Resolve(); } catch { }
        if (d == null) continue;
        if (d.Module == smoothie.MainModule) { if (d.HasBody) queue.Enqueue(d); continue; }
        if (!File.Exists(Path.Combine(interop, r.DeclaringType.Scope.Name.Replace(".dll", "") + ".dll")) || !Invokes(d)) continue;
        if (!offMain.TryGetValue(d.FullName, out var users)) offMain[d.FullName] = users = new();
        users.Add($"{m.DeclaringType.Name}.{m.Name}");
    }
}
foreach (var pair in offMain) Console.WriteLine($"{pair.Key}\n    used off the main thread by: {string.Join(", ", pair.Value)}");
Console.WriteLine($"{offMain.Count} runtime_invoke calls off the main thread");
