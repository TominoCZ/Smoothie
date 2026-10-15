#if DEBUG
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Smoothie;

// Debug builds only (a profiling tool, not shipped): names the C# methods behind perf samples. perf sees the game's
// JIT-compiled code as bare addresses; Mono's own table maps them to methods, but only inside
// the process that compiled them. Polled in raid every 5 s: BepInEx/smoothie_resolve.txt (one
// hex address per line) becomes smoothie_resolved.txt ("address method" per line).
internal static class JitNames
{
    private const string Mono = "mono-2.0-bdwgc.dll";

    [DllImport(Mono)] private static extern IntPtr mono_domain_get();
    [DllImport(Mono)] private static extern IntPtr mono_jit_info_table_find(IntPtr domain, IntPtr address);
    [DllImport(Mono)] private static extern IntPtr mono_jit_info_get_method(IntPtr info);
    [DllImport(Mono)] private static extern IntPtr mono_method_full_name(IntPtr method, int signature);

    internal static void Poll()
    {
        var input = Path.Combine(BepInEx.Paths.BepInExRootPath, "smoothie_resolve.txt");
        if (!File.Exists(input)) return;
        try
        {
            var domain = mono_domain_get();
            var output = new StringBuilder();
            var found = 0;
            foreach (var line in File.ReadAllLines(input))
            {
                if (!ulong.TryParse(line.Trim(), NumberStyles.HexNumber, null, out var address)) continue;
                var info = mono_jit_info_table_find(domain, (IntPtr)(long)address);
                var name = "?";
                if (info != IntPtr.Zero)
                {
                    var method = mono_jit_info_get_method(info);
                    if (method != IntPtr.Zero) name = Marshal.PtrToStringAnsi(mono_method_full_name(method, 0)) ?? "?";
                    found++;
                }

                output.Append(line.Trim()).Append(' ').AppendLine(name);
            }

            File.WriteAllText(Path.Combine(BepInEx.Paths.BepInExRootPath, "smoothie_resolved.txt"), output.ToString());
            Plugin.Log.LogInfo($"JIT names: {found} addresses resolved");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"JIT names failed: {ex.Message}");
        }
        finally
        {
            File.Delete(input);
        }
    }
}
#endif
