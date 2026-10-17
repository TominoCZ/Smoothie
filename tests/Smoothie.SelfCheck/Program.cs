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
// Coming into view is checked on that very frame.
sight.Hidden(t, true);
Check(casts == 1, "bot coming into view isn't checked at once");
casts = 0;
t += Sight.CheckInterval;

Run(1f, inView: true);
Check(Math.Abs(casts - 1f / Sight.CheckInterval) <= 1.5f, $"blocked bot in view cast {casts} lines in 1 s, expected ~{1f / Sight.CheckInterval:0}");
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

// The player's gun: a block of near texels (0.9) in front of a wall (0.3). Registered as gear,
// it still covers but isn't the nearest cover; a wall texel inside the gun's box (outside its
// depth range) still is.
for (var i = 0; i < image.Length; i++) image[i] = 0.3f;
for (var r = 2; r < 8; r++)
for (var x = 2; x < 10; x++) image[r * pw + x] = 0.9f;
pyramid.Build(image, pw, ph);
Check(pyramid.Test(0.05f, 0.05f, 0.95f, 0.95f, 0.1f) == DepthCover.Covered && Math.Abs(pyramid.NearestCover - 0.9f) < 1e-6f && !pyramid.GearCover,
    $"without a gear mask the gun isn't the nearest cover ({pyramid.NearestCover})");
// Rows are stored upside down: rows 2-7 are screen y from (ph - 8) / ph to (ph - 2) / ph.
pyramid.AddGear(2f / pw, (ph - 8f) / ph, 10f / pw, (ph - 2f) / ph, 0.95f, 0.85f);
pyramid.Build(image, pw, ph);
Check(pyramid.Test(0.05f, 0.05f, 0.95f, 0.95f, 0.1f) == DepthCover.Covered && Math.Abs(pyramid.NearestCover - 0.3f) < 1e-6f && pyramid.GearCover,
    $"gear taken as the nearest cover ({pyramid.NearestCover}, gear {pyramid.GearCover})");
image[4 * pw + 4] = 0.5f;                                     // a wall texel inside the gun's box
pyramid.Build(image, pw, ph);
Check(pyramid.Test(0.05f, 0.05f, 0.95f, 0.95f, 0.1f) == DepthCover.Covered && Math.Abs(pyramid.NearestCover - 0.5f) < 1e-6f,
    $"a wall texel inside the gun's box was taken for gear ({pyramid.NearestCover})");
pyramid.ClearGear();
pyramid.Build(image, pw, ph);
Check(Math.Abs(pyramid.Test(0.05f, 0.05f, 0.95f, 0.95f, 0.1f) == DepthCover.Covered ? pyramid.NearestCover - 0.9f : 1f) < 1e-6f, "gear mask survived ClearGear");

// The native pyramid (src/SmoothieNative, SSE2) answers exactly as the C# one: random sizes
// (odd ones and the game's 185x102 included), walls with holes, sky, near things and gear.
Check(DepthPyramid.TryNative(), "libSmoothieNative.so didn't load");
if (DepthPyramid.UseNative)
{
    var managed = new DepthPyramid();
    var native = new DepthPyramid();
    int[][] sizes = { new[] { 185, 102 }, new[] { 37, 23 }, new[] { 1, 1 }, new[] { 2, 7 }, new[] { 64, 64 }, new[] { 9, 130 } };
    var nativeMismatches = 0;
    var tests = 0;
    for (var round = 0; round < 300; round++)
    {
        var size = sizes[round % sizes.Length];
        int w = size[0], h = size[1];
        var img = new float[w * h];
        var wall = 0.05f + (float)random.NextDouble() * 0.3f;
        for (var i = 0; i < img.Length; i++)
        {
            var roll = random.NextDouble();
            img[i] = roll < 0.03 ? 0f : roll < 0.06 ? 0.95f : wall + (float)random.NextDouble() * 0.05f;
        }

        if (round % 4 == 0) for (var i = 0; i < img.Length; i++) img[i] = wall;
        managed.ClearGear();
        native.ClearGear();
        if (round % 2 == 1)
        {
            float gx = (float)random.NextDouble(), gy = (float)random.NextDouble();
            managed.AddGear(gx, gy, gx + 0.3f, gy + 0.3f, 0.99f, 0.0f);
            native.AddGear(gx, gy, gx + 0.3f, gy + 0.3f, 0.99f, 0.0f);
        }

        DepthPyramid.UseNative = false;
        managed.Build(img, w, h);
        DepthPyramid.UseNative = true;
        native.Build(img, w, h);
        for (var box = 0; box < 300; box++)
        {
            float x = (float)random.NextDouble() * 1.2f - 0.1f, y = (float)random.NextDouble() * 1.2f - 0.1f;
            float bw = (float)random.NextDouble() * 0.7f, bh = (float)random.NextDouble() * 0.7f;
            var nearest = (float)random.NextDouble() * 0.4f;
            // The managed pyramid was built without native levels, so it tests in C#.
            var a = managed.Test(x, y, x + bw, y + bh, nearest, out var coverA, out var gearA);
            var b = native.Test(x, y, x + bw, y + bh, nearest, out var coverB, out var gearB);
            tests++;
            if (a != b || a == DepthCover.Covered && (coverA != coverB || gearA != gearB)) nativeMismatches++;
        }
    }

    Check(nativeMismatches == 0, $"native pyramid disagrees with the C# one {nativeMismatches} of {tests} times");
    // ponytail: rough speed check only, printed, not asserted (machines differ).
    var bench = new float[185 * 102];
    for (var i = 0; i < bench.Length; i++) bench[i] = 0.2f + (float)random.NextDouble() * 0.01f;
    foreach (var useNative in new[] { false, true })
    {
        DepthPyramid.UseNative = useNative;
        var py = new DepthPyramid();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var sink = 0;
        for (var k = 0; k < 200; k++)
        {
            py.Build(bench, 185, 102);
            for (var box = 0; box < 2000; box++) sink += py.Test(0.1f + box % 50 * 0.01f, 0.2f, 0.3f + box % 50 * 0.01f, 0.5f, 0.1f, out _, out _);
        }

        Console.WriteLine($"{(useNative ? "native" : "C#    ")}: {watch.Elapsed.TotalMilliseconds / 200:0.000} ms per build + 2000 tests ({sink})");
    }

    DepthPyramid.UseNative = false;
}

// The native cell walk (src/SmoothieNative/walk.c, SSE2) lists and writes exactly what the C#
// one does: random cells (odd lengths for the scalar tail, long empty runs for the 16-at-a-time
// skip), covered, hidden, waiting and moved objects, a view that leaves some boxes out.
if (DepthPyramid.TryNative())
{
    unsafe
    {
        var rng = new Random(7);
        const int n = 4000;
        float R(float lo, float hi) => lo + (float)rng.NextDouble() * (hi - lo);
        var retry = new float[n]; var next = new float[n]; var slack = new float[n];
        var from3 = new float[n * 3]; var min3 = new float[n * 3]; var max3 = new float[n * 3];
        var streak = new byte[n]; var hidden = new byte[n];
        for (var i = 0; i < n; i++)
        {
            // Every 200 objects, a run of 100 with nothing covered or hidden.
            var empty = i % 200 < 100;
            retry[i] = rng.Next(3) == 0 ? 0f : R(0f, 10f);
            next[i] = R(0f, 10f);
            slack[i] = R(0f, 4f);
            for (var a = 0; a < 3; a++)
            {
                from3[i * 3 + a] = R(-2f, 2f);
                var c = R(-60f, 60f);
                var e = R(0.1f, 8f);
                min3[i * 3 + a] = c - e;
                max3[i * 3 + a] = c + e;
            }

            streak[i] = empty ? (byte)0 : (byte)rng.Next(4);
            hidden[i] = empty || rng.Next(5) != 0 ? (byte)0 : (byte)1;
        }

        // Left, right, bottom, top of a view looking down +z.
        var planes = new[] { 1f, 0f, 0.8f, 0f, -1f, 0f, 0.8f, 0f, 0f, 1f, 0.6f, 0f, 0f, -1f, 0.6f, 0f };
        var eye = new[] { 0.3f, -0.2f, 0.1f };
        int walkMismatches = 0, cellsChecked = 0;
        (byte[] Streak, float[] Next, int[] WantedIn, List<int> Lists, bool Quiet) Walk(bool useNative, int from, int to, bool inView)
        {
            DepthPyramid.UseNative = useNative;
            var s = (byte[])streak.Clone(); var nx = (float[])next.Clone(); var w = new int[n];
            var a = new int[n]; var b = new int[n]; var c = new int[n]; var counts = new int[3];
            var quiet = false;
            fixed (float* pr = retry, pn = nx, ps = slack, pf = from3, pmin = min3, pmax = max3, pe = eye, pp = planes)
            fixed (byte* pst = s, ph = hidden)
            fixed (int* pw = w, pa = a, pb = b, pc = c, pcount = counts)
            {
                var u = new CellWalk.Units { RetryAt = pr, NextCheck = pn, Slack = ps, CheckedFrom = pf, Min = pmin, Max = pmax, Streak = pst, Hidden = ph, WantedIn = pw };
                if (inView) CellWalk.InView(&u, from, to, pe, 5f, 5f, 42, pp, 2, pa, pb, pc, pcount);
                else quiet = CellWalk.OutOfViewCell(&u, from, to, pe, 42, 2, pa, pb, pcount);
            }

            var lists = new List<int>();
            for (var k = 0; k < (inView ? 3 : 2); k++)
            {
                lists.Add(-1 - k);
                lists.AddRange((k == 0 ? a : k == 1 ? b : c).Take(counts[k]));
            }

            return (s, nx, w, lists, quiet);
        }

        for (var from = 0; from < n; from += rng.Next(1, 300))
        {
            var to = Math.Min(n, from + rng.Next(1, 300));
            foreach (var inView in new[] { true, false })
            {
                var managed = Walk(false, from, to, inView);
                var native = Walk(true, from, to, inView);
                cellsChecked++;
                if (!managed.Streak.SequenceEqual(native.Streak) || !managed.Next.SequenceEqual(native.Next) || !managed.WantedIn.SequenceEqual(native.WantedIn)
                    || !managed.Lists.SequenceEqual(native.Lists) || managed.Quiet != native.Quiet) walkMismatches++;
            }
        }

        Check(walkMismatches == 0, $"native cell walk disagrees with the C# one in {walkMismatches} of {cellsChecked} cells");
        // ponytail: rough speed check only, printed, not asserted (machines differ; this is .NET's
        // JIT, which does far better than the game's Mono).
        fixed (float* qr = retry, qn = next, qs = slack, qf = from3, qmin = min3, qmax = max3, qe = eye, qp = planes)
        fixed (byte* qst = streak, qh = hidden)
        {
            var w = new int[n]; var a = new int[n]; var b = new int[n]; var c = new int[n]; var counts = new int[3];
            fixed (int* qw = w, qa = a, qb = b, qc = c, qcount = counts)
            {
                var u = new CellWalk.Units { RetryAt = qr, NextCheck = qn, Slack = qs, CheckedFrom = qf, Min = qmin, Max = qmax, Streak = qst, Hidden = qh, WantedIn = qw };
                foreach (var useNative in new[] { false, true })
                {
                    DepthPyramid.UseNative = useNative;
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    for (var k = 0; k < 2000; k++) CellWalk.InView(&u, 0, n, qe, 5f, 5f, 42, qp, 2, qa, qb, qc, qcount);
                    Console.WriteLine($"{(useNative ? "native" : "C#    ")}: {watch.Elapsed.TotalMilliseconds * 1e6 / 2000 / n:0.0} ns per object walked in view");
                }
            }
        }

        DepthPyramid.UseNative = false;
    }
}

// Growth while moving: reversed-Z projection with near 0.05 m, far 10 km; depth of a distance z
// is p23 / z - p22. Cover at 2 m, the box's far point at 20 m, eye 0.18 m: 0.18 * (1/2 - 1/20)
// = 0.081 rad, about 8.9 texels of 0.0091 rad.
const float zn = 0.05f, zf = 10000f;
float p22 = zn / (zf - zn), p23 = zf * zn / (zf - zn);
float DepthAt(float z) => p23 / z - p22;
var grow = DepthPyramid.MoveGrowTexels(0.18f, DepthAt(2f), DepthAt(20f), p23, 0.0091f, 100f);
Check(Math.Abs(grow - 8.9f) < 0.1f, $"growth for cover at 2 m, box at 20 m, 0.18 m of movement is {grow} texels, not ~8.9");
Check(DepthPyramid.MoveGrowTexels(0.18f, DepthAt(2f), DepthAt(20f), p23, 0.0091f, 5f) == 5f, "growth isn't capped");
Check(DepthPyramid.MoveGrowTexels(0f, DepthAt(2f), DepthAt(20f), p23, 0.0091f, 12f) == 0f, "standing still still grows");
Check(DepthPyramid.MoveGrowTexels(0.18f, DepthAt(19.9f), DepthAt(20f), p23, 0.0091f, 12f) < 0.1f, "cover right in front of the box grows it much");
// Behind the gun: only the object's own shift, 0.2 m at 20 m = 0.01 rad, about 1.1 texels.
var own = DepthPyramid.OwnShiftTexels(0.2f, DepthAt(20f), p22, p23, 0.0091f, 100f);
Check(Math.Abs(own - 1.1f) < 0.05f, $"own shift behind the gun is {own} texels, not ~1.1");

// The animator pause hooks (src/SmoothieNative/animpause.c), with recording stand-ins for Unity's
// functions: listed animators' outputs are left out of the pose list, listed graphs skip only
// their normal frames, and everything else reaches Unity unchanged.
unsafe
{
    var outputs = new IntPtr[3];
    var animators = new IntPtr[] { (IntPtr)0xA1, (IntPtr)0xA2, (IntPtr)0xA3 };
    for (var i = 0; i < 3; i++)
    {
        outputs[i] = System.Runtime.InteropServices.Marshal.AllocHGlobal(0x100);
        *(IntPtr*)((byte*)outputs[i] + 0xD0) = animators[i];
    }

    fixed (IntPtr* data = outputs)
    {
        var vector = stackalloc IntPtr[] { (IntPtr)data, (IntPtr)0x1234, (IntPtr)3, (IntPtr)3 };
        AnimHooks.SmoothieAnimInit((IntPtr)(delegate* unmanaged<IntPtr*, byte, byte, byte, void>)&AnimHooks.Update,
            (IntPtr)(delegate* unmanaged<IntPtr, int, double, float, ulong, uint, int, void>)&AnimHooks.Prepare);
        var one = stackalloc IntPtr[] { (IntPtr)0xA2 };
        var graph = stackalloc IntPtr[] { (IntPtr)0x62 };
        AnimHooks.SmoothieAnimSet(one, graph, 1);
        AnimHooks.SmoothieUpdateAvatars(vector, 1, 0, 1);
        Check(AnimHooks.Seen.SequenceEqual(new[] { outputs[0], outputs[2] }), $"pose list kept {AnimHooks.Seen.Count} outputs, not the two unlisted ones");
        Check(AnimHooks.Label == (IntPtr)0x1234 && AnimHooks.Flags == (1, 0, 1), "pose list lost its label or flags");
        AnimHooks.Prepares.Clear();
        AnimHooks.SmoothiePrepareFrame((IntPtr)0x62, 0, 1.5, 0.25f, 77, 3, 2);
        AnimHooks.SmoothiePrepareFrame((IntPtr)0x62, 1, 1.5, 0.25f, 77, 3, 2);
        AnimHooks.SmoothiePrepareFrame((IntPtr)0x61, 0, 2.5, 0.5f, 78, 4, 1);
        Check(AnimHooks.Prepares.SequenceEqual(new[] { ((IntPtr)0x62, 1, 1.5, 0.25f, 77UL, 3U, 2), ((IntPtr)0x61, 0, 2.5, 0.5f, 78UL, 4U, 1) }),
            $"graph frames passed: {string.Join(", ", AnimHooks.Prepares)}");
        AnimHooks.SmoothieAnimSet(null, null, 0);
        AnimHooks.SmoothieUpdateAvatars(vector, 0, 0, 0);
        Check(AnimHooks.Vector == (IntPtr)vector, "with nothing listed the pose list isn't Unity's own");
    }

    foreach (var output in outputs) System.Runtime.InteropServices.Marshal.FreeHGlobal(output);
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"FAIL ({failures.Count}):");
    failures.ForEach(f => Console.Error.WriteLine("  - " + f));
    return 1;
}

Console.WriteLine($"OK: throttle intervals, zoom, depth cover, the depth pyramid, the cell walk and the animator pause hooks behave, {binds.Count} config names are valid.");
return 0;

static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

static unsafe class AnimHooks
{
    internal static readonly List<IntPtr> Seen = new();
    internal static readonly List<(IntPtr, int, double, float, ulong, uint, int)> Prepares = new();
    internal static IntPtr Label, Vector;
    internal static (int, int, int) Flags;

    [System.Runtime.InteropServices.DllImport("SmoothieNative")] internal static extern void SmoothieAnimInit(IntPtr update, IntPtr prepare);
    [System.Runtime.InteropServices.DllImport("SmoothieNative")] internal static extern void SmoothieAnimSet(IntPtr* animators, IntPtr* graphs, int count);
    [System.Runtime.InteropServices.DllImport("SmoothieNative")] internal static extern void SmoothieUpdateAvatars(IntPtr* outputs, byte a, byte b, byte c);
    [System.Runtime.InteropServices.DllImport("SmoothieNative")] internal static extern void SmoothiePrepareFrame(IntPtr graph, int type, double time, float delta, ulong frame, uint index, int context);

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    internal static void Update(IntPtr* outputs, byte a, byte b, byte c)
    {
        Vector = (IntPtr)outputs;
        Label = outputs[1];
        Flags = (a, b, c);
        Seen.Clear();
        for (var i = 0; i < (int)outputs[2]; i++) Seen.Add(((IntPtr*)outputs[0])[i]);
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    internal static void Prepare(IntPtr graph, int type, double time, float delta, ulong frame, uint index, int context) =>
        Prepares.Add((graph, type, time, delta, frame, index, context));
}
