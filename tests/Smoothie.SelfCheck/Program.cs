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

// Occlusion sight lines: a fixed rate, none out of view, none for a moment after a sighting.
var casts = 0;
var clear = false;
var sight = new Sight(() => { casts++; return clear; }, 1f);
var t = 0f;
void Run(float seconds, bool inView)
{
    for (var end = t + seconds; t < end; t += 0.005f) sight.Hidden(t, inView);
}

Run(1f, inView: false);
Check(casts == 0, $"out-of-view bot cast {casts} lines");
Check(sight.Hidden(t, false), "bot out of view for 1 s is not hidden");

Run(1f, inView: true);
Check(casts is >= 9 and <= 11, $"blocked bot in view cast {casts} lines in 1 s, expected ~10");
Check(sight.Hidden(t, true), "blocked bot in view is not hidden");

clear = true;
t += Sight.CheckInterval;
casts = 0;
Check(!sight.Hidden(t, true) && casts == 1, "clear line doesn't show the bot at once");
Run(Sight.SeenHold - 0.01f, inView: true);
Check(casts == 1, $"seen bot cast {casts - 1} more lines inside the hold");
clear = false;
Run(0.02f, inView: true);
Check(casts == 2, $"no new line after the hold ({casts})");
Check(!sight.Hidden(t, true), "bot hidden before the hide delay");

// Leaving the view ends the hold: back in view, lines are cast on the first frame.
clear = true;
Run(0.2f, inView: true);
casts = 0;
Run(0.05f, inView: false);
sight.Hidden(t, true);
Check(casts == 1, $"bot back in view inside the hold cast {casts} lines, expected 1");

// Memory cleanup (frames at 60 fps): SPT switches the GC on for an inventory open; it goes off again only
// after that collection finished, at most every 5 s, and runs one itself past the headroom.
var cleanup = new CleanupPause();
long heap = 5L << 30;
cleanup.Start(heap);
Check(cleanup.Tick(false, 10, () => heap, 0f, 0) == CleanupPause.Action.None, "paused GC with a small heap should be left alone");
Check(cleanup.Tick(true, 10, () => heap, 10f, 600) == CleanupPause.Action.None, "GC just switched on should wait for its collection");
Check(cleanup.Tick(true, 10, () => heap, 10.5f, 630) == CleanupPause.Action.None, "GC on with no collection finished yet should stay on");
Check(cleanup.Tick(true, 11, () => heap, 11f, 660) == CleanupPause.Action.Pause, "GC should pause once its collection finished");
cleanup.Tick(true, 11, () => heap, 12f, 720);
Check(cleanup.Tick(true, 12, () => heap, 13f, 780) == CleanupPause.Action.None, "pausing again within 5 s");
Check(cleanup.Tick(true, 12, () => heap, 16.5f, 990) == CleanupPause.Action.Pause, "should pause again after 5 s");
heap += CleanupPause.Headroom;
Check(cleanup.Tick(false, 12, () => heap, 20f, 1200) == CleanupPause.Action.Collect, "4 GB of new garbage should start a collection");
Check(cleanup.Tick(false, 12, () => heap, 22f, 1320) == CleanupPause.Action.None, "only one collection per pile-up");

// Something that switches it straight back on after every pause (the next frame) is given
// up on, not fought all raid; an inventory open a while after a pause isn't a fight; a new
// raid tries again.
var fight = new CleanupPause();
fight.Start(heap);
var now = 0f;
var frame = 0;
var collections = 0;
fight.Tick(true, collections, () => heap, now, frame);
for (var round = 1; round <= CleanupPause.FightsToGiveUp; round++)
{
    Check(!fight.GaveUp, $"gave up before round {round}");
    collections++;
    now += CleanupPause.MinGap + 1f;
    frame += 600;
    Check(fight.Tick(true, collections, () => heap, now, frame) == CleanupPause.Action.Pause, $"round {round} should pause");
    fight.Tick(true, collections, () => heap, now + 0.01f, frame + 1);
}
Check(fight.GaveUp, "GC switched on again the frame after every pause should be left on");
Check(fight.Tick(true, collections + 5, () => heap, now + 60f, frame + 9000) == CleanupPause.Action.None, "given up: no more pauses this raid");
fight.Start(heap);
Check(!fight.GaveUp, "a new raid should try again");

var looting = new CleanupPause();
looting.Start(heap);
now = 0f;
frame = 0;
collections = 0;
for (var open = 1; open <= 10; open++)
{
    // Inventory opened half a second after the last pause (30 frames at 60 fps).
    looting.Tick(true, collections, () => heap, now, frame);
    collections++;
    now += CleanupPause.MinGap + 0.5f;
    frame += 330;
    Check(looting.Tick(true, collections, () => heap, now, frame) == CleanupPause.Action.Pause, $"inventory open {open} should still be paused after");
    now += 0.5f;
    frame += 30;
}
Check(!looting.GaveUp, "inventory opens should never count as fighting");

// Config names: BepInEx refuses = \n \t \ " ' [ ] in section and key names and throws
// in Plugin.Awake, which leaves the whole plugin unpatched. Checked straight off the source.
var pluginSource = File.ReadAllText(Path.Combine(Path.GetDirectoryName(SourcePath())!, "../../src/Smoothie/Plugin.cs"));
var sections = new Dictionary<string, string>();
foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(pluginSource, @"const string (\w+) = ""([^""]*)"";"))
{
    sections[m.Groups[1].Value] = m.Groups[2].Value;
}

var binds = System.Text.RegularExpressions.Regex.Matches(pluginSource, @"(?:Config\.)?Bind\(\s*(""(?:[^""\\]|\\.)*""|\w+)\s*,\s*(""(?:[^""\\]|\\.)*"")");
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

// Far objects against the depth: larger is nearer, rows upside down.
var depth = new float[4 * 4];
for (var i = 0; i < depth.Length; i++) depth[i] = 0.3f;      // a wall everywhere
Check(DepthCover.Test(depth, 4, 4, 0.1f, 0.1f, 0.4f, 0.4f, 0.01f) == DepthCover.Covered, "box behind a wall isn't covered");
Check(DepthCover.Test(depth, 4, 4, 0.1f, 0.1f, 0.4f, 0.4f, 0.5f) == DepthCover.ShowsBehind, "box in front of the wall is covered");
depth[3 * 4 + 0] = 0f;                                         // sky in the bottom-left texel (last row in memory)
Check(DepthCover.Test(depth, 4, 4, 0.0f, 0.0f, 0.2f, 0.2f, 0.01f) == DepthCover.ShowsBehind, "box with sky under its corner is covered");
Check(DepthCover.Test(depth, 4, 4, 0.0f, 0.8f, 0.2f, 0.95f, 0.01f) == DepthCover.Covered, "upside-down rows: top-left box sees the bottom-left sky");
Check(DepthCover.Test(depth, 4, 4, 0.3f, 0.0f, 0.45f, 0.24f, 0.01f) == DepthCover.Covered, "box ending inside texel 1 also tests texel 2 (sky next door)");
depth[3 * 4 + 1] = 0f;                                         // sky right of it, in texel x=1
Check(DepthCover.Test(depth, 4, 4, 0.5f, 0.0f, 0.75f, 0.2f, 0.01f) == DepthCover.Covered, "box ending exactly on a texel edge tests the next texel");
Check(DepthCover.Test(depth, 4, 4, 0.2f, 0.0f, 0.3f, 0.2f, 0.01f) == DepthCover.ShowsBehind, "box over the sky texel is covered");
Check(DepthCover.Test(depth, 4, 4, -0.5f, 0.1f, -0.1f, 0.4f, 0.01f) == DepthCover.OffScreen, "box off the screen is covered");

// The pyramid gives exactly the fine test's answer: random walls, sky and near things on a 37x23 image.
var random = new Random(7);
const int pw = 37, ph = 23;
var image = new float[pw * ph];
var pyramid = new DepthPyramid();
var mismatches = 0;
for (var round = 0; round < 200; round++)
{
    var wall = 0.05f + (float)random.NextDouble() * 0.3f;
    for (var i = 0; i < image.Length; i++)
    {
        var roll = random.NextDouble();
        image[i] = roll < 0.03 ? 0f : roll < 0.06 ? 0.95f : wall + (float)random.NextDouble() * 0.02f;
    }

    // Some rounds: one big wall with no holes, so the coarse levels decide.
    if (round % 3 == 0) for (var i = 0; i < image.Length; i++) image[i] = wall;
    pyramid.Build(image, pw, ph);
    for (var box = 0; box < 200; box++)
    {
        float x = (float)random.NextDouble() * 1.2f - 0.1f, y = (float)random.NextDouble() * 1.2f - 0.1f;
        float bw = (float)random.NextDouble() * 0.6f, bh = (float)random.NextDouble() * 0.6f;
        var nearest = (float)random.NextDouble() * 0.3f;
        if (pyramid.Test(x, y, x + bw, y + bh, nearest) != DepthCover.Test(image, pw, ph, x, y, x + bw, y + bh, nearest)) mismatches++;
    }
}

Check(mismatches == 0, $"depth pyramid disagrees with the fine test {mismatches} times");
for (var i = 0; i < image.Length; i++) image[i] = 0.3f;
image[5 * pw + 5] = 0.4f;                                     // one nearer texel inside the box
pyramid.Build(image, pw, ph);
Check(pyramid.Test(0.05f, 0.05f, 0.95f, 0.95f, 0.1f) == DepthCover.Covered && Math.Abs(pyramid.NearestCover - 0.4f) < 1e-6f,
    $"nearest cover under a covered box is {pyramid.NearestCover}, not 0.4");

if (failures.Count > 0)
{
    Console.Error.WriteLine($"FAIL ({failures.Count}):");
    failures.ForEach(f => Console.Error.WriteLine("  - " + f));
    return 1;
}

Console.WriteLine($"OK: throttle intervals, zoom, depth cover and the depth pyramid behave, {binds.Count} config names are valid.");
return 0;

static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
