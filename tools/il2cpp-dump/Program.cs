// The game's IL2CPP metadata, offline, through the Cpp2IL that BepInEx ships (the way BepInEx runs
// it to make its interop assemblies). SPT keeps global-metadata.dat decrypted on disk.
//   dotnet run                    dummy assemblies into out/: every type's fields with their
//                                 offsets and every method with its RVA in GameAssembly.dll
//   dotnet run -- globals <va>... what each metadata-usage slot an instruction loads is (type,
//                                 method, field, string literal); va = 0x180000000 + RVA
// SPT_PATH: the SPT 5.0 folder. gadis.sh disassembles a GameAssembly function with both.
using AssetRipper.Primitives;
using Cpp2IL.Core;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.ProcessingLayers;
using LibCpp2IL;
var game = Environment.GetEnvironmentVariable("SPT_PATH") ?? throw new InvalidOperationException("set SPT_PATH to the SPT 5.0 folder");
var quiet = args.Length > 0;
Cpp2IL.Core.Logging.Logger.InfoLog += (m, s) => { if (!quiet) Console.Write($"[{s}] {m}"); };
Cpp2IL.Core.Logging.Logger.WarningLog += (m, s) => Console.Write($"[W {s}] {m}");
Cpp2IL.Core.Logging.Logger.ErrorLog += (m, s) => Console.Write($"[E {s}] {m}");
Cpp2IL.Core.Api.InstructionSetRegistry.RegisterInstructionSet<Cpp2IL.Core.InstructionSets.X86InstructionSet>(LibCpp2IL.DefaultInstructionSets.X86_64);
LibCpp2IlBinaryRegistry.RegisterBuiltInBinarySupport();
Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(game, "GameAssembly.dll"), Path.Combine(game, "EscapeFromTarkov_Data/il2cpp_data/Metadata/global-metadata.dat"), UnityVersion.Parse("2022.3.43f2"));
if (args.Length > 0 && args[0] == "globals")
{
    // globals <va>...: what each IL2CPP metadata-usage slot is (type, method, field, string literal).
    foreach (var a in args.Skip(1))
    {
        var va = Convert.ToUInt64(a, 16);
        var usage = Cpp2IlApi.CurrentAppContext.LibCpp2IlContext.GetAnyGlobalByAddress(va);
        Console.WriteLine($"{va:X} {(usage == null ? "?" : usage.Type + " " + usage)}");
    }
    return;
}
var layers = new List<Cpp2IlProcessingLayer> { new AttributeInjectorProcessingLayer() };
foreach (var l in layers) l.PreProcess(Cpp2IlApi.CurrentAppContext, layers);
foreach (var l in layers) l.Process(Cpp2IlApi.CurrentAppContext);
var asms = new AsmResolverDllOutputFormatDefault().BuildAssemblies(Cpp2IlApi.CurrentAppContext);
Directory.CreateDirectory("out");
foreach (var a in asms) a.Write(Path.Combine("out", a.Name + ".dll"));
Console.WriteLine($"wrote {asms.Count}");
