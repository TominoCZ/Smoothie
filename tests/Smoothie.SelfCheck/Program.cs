using Smoothie;

// dotnet run --project tests/Smoothie.SelfCheck

var failures = new List<string>();
void Check(bool ok, string what)
{
    if (!ok) failures.Add(what);
}

// Defaults from Plugin.cs.
const float full = 40f, lowest = 200f, visibleHz = 15f, offscreenHz = 10f, footstepHz = 30f;
float Interval(float distance, bool onScreen, float zoom = 1f, float audible = 60f) =>
    ThrottleMath.Interval(distance, onScreen, zoom, audible, full, lowest, visibleHz, offscreenHz, footstepHz);

// On screen: every frame up close, sliding to the lowest rate far out.
Check(Interval(30f, true) == 0f, "on-screen bot inside full-rate distance is throttled");
Check(Math.Abs(Interval(300f, true) - 1f / visibleHz) < 1e-5f, $"far on-screen bot: {Interval(300f, true)}");
Check(Interval(100f, true, audible: 0f) > 0f && Interval(100f, true, audible: 0f) < 1f / visibleHz, "falloff between distances is not gradual");
Check(Interval(150f, true, audible: 0f) > Interval(100f, true, audible: 0f), "farther on-screen bot updates more often");

// Scope zoom: a 6x scope makes 240 m look like 40 m, so full rate again.
Check(Interval(240f, true, zoom: 6f, audible: 0f) == 0f, "scoped bot at 240 m / 6x is throttled");
Check(Interval(240f, true, zoom: 1f, audible: 0f) > 0f, "unscoped bot at 240 m is not throttled");

// Off screen: the off-screen rate, unless the bot could be heard walking.
Check(Math.Abs(Interval(150f, false) - 1f / offscreenHz) < 1e-5f, $"off-screen bot: {Interval(150f, false)}");
Check(Math.Abs(Interval(50f, false, audible: 60f) - 1f / footstepHz) < 1e-5f, "audible off-screen bot is not held to the footstep rate");
Check(Interval(50f, true, audible: 60f) <= 1f / footstepHz, "audible on-screen bot is slower than the footstep rate");

// Zoom: the player's own FOV is 1x, narrower is magnified, never below 1.
Check(Math.Abs(ThrottleMath.Zoom(75f, 75f) - 1f) < 1e-5f, "zoom at reference FOV is not 1");
Check(ThrottleMath.Zoom(75f, 35f) > 2f, $"optic FOV 35 gives zoom {ThrottleMath.Zoom(75f, 35f)}");
Check(ThrottleMath.Zoom(50f, 75f) == 1f, "wider than reference FOV dropped below 1x");
Check(ThrottleMath.Zoom(75f, 0f) == 1f, "zero FOV broke zoom");

// Config names: BepInEx refuses = \n \t \ " ' [ ] in section and key names and throws
// in Plugin.Awake, which leaves the whole plugin unpatched. Checked straight off the source.
var pluginSource = File.ReadAllText(Path.Combine(Path.GetDirectoryName(SourcePath())!, "../../src/Smoothie/Plugin.cs"));
var sections = new Dictionary<string, string>();
foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(pluginSource, @"const string (\w+) = ""([^""]*)"";"))
{
    sections[m.Groups[1].Value] = m.Groups[2].Value;
}

var binds = System.Text.RegularExpressions.Regex.Matches(pluginSource, @"Config\.Bind\(\s*(""(?:[^""\\]|\\.)*""|\w+)\s*,\s*(""(?:[^""\\]|\\.)*"")");
Check(binds.Count >= 10, $"found only {binds.Count} Config.Bind calls in Plugin.cs");
foreach (System.Text.RegularExpressions.Match m in binds)
{
    var section = m.Groups[1].Value.StartsWith("\"") ? m.Groups[1].Value.Trim('"') : sections.GetValueOrDefault(m.Groups[1].Value, "?");
    var key = m.Groups[2].Value.Trim('"');
    foreach (var name in new[] { section, key })
    {
        Check(name.IndexOfAny(new[] { '=', '\n', '\t', '\\', '"', '\'', '[', ']' }) < 0, $"config name BepInEx rejects: {name}");
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"FAIL ({failures.Count}):");
    failures.ForEach(f => Console.Error.WriteLine("  - " + f));
    return 1;
}

Console.WriteLine($"OK: throttle intervals and zoom behave, {binds.Count} config names are valid.");
return 0;

static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
