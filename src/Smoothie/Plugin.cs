using System;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using Comfort.Common;
using BepInEx.Configuration;
using BepInEx.Logging;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Smoothie;

[BepInPlugin("com.tominocz.smoothie", "Smoothie", "1.0.8")]
// Optional: when Fika is installed it has to load first, or its types aren't there yet when
// Smoothie looks for them and its observed bots aren't recognised. Without Fika this does nothing.
[BepInDependency("com.fika.core", BepInDependency.DependencyFlags.SoftDependency)]
public class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log = null!;

    internal static ConfigEntry<bool> ThrottleAnimators = null!;
    internal static ConfigEntry<bool> UnityStepping = null!;
    internal static ConfigEntry<bool> PauseInEngine = null!;
    internal static ConfigEntry<float> FullRateDistance = null!;
    internal static ConfigEntry<float> LowestRateDistance = null!;
    internal static ConfigEntry<float> LowestVisibleRate = null!;
    internal static ConfigEntry<float> OffscreenRate = null!;
    internal static ConfigEntry<float> FootstepRate = null!;
    internal static ConfigEntry<float> FootstepRange = null!;
    internal static ConfigEntry<bool> ShowStats = null!;
    internal static ConfigEntry<bool> ShowCosts = null!;
    internal static ConfigEntry<bool> PauseFixes = null!;
    internal static ConfigEntry<bool> HideCulledWeapons = null!;
    internal static ConfigEntry<bool> SkipHiddenIk = null!;
    internal static ConfigEntry<bool> SimplifyDistant = null!;
    internal static ConfigEntry<float> SimplifyDistance = null!;
    internal static ConfigEntry<float> CorpseSettleTime = null!;
    internal static ConfigEntry<bool> HotPathFixes = null!;
    internal static ConfigEntry<bool> NoMuzzleShadows = null!;
    internal static ConfigEntry<bool> HideCovered = null!;
    internal static ConfigEntry<float> HideBeyond = null!;
    internal static ConfigEntry<bool> HideShadows = null!;
    internal static ConfigEntry<bool> SkipCoveredLamps = null!;
    internal static ConfigEntry<bool> SkipOutOfView = null!;
    internal static ConfigEntry<bool> SkipTooFar = null!;
    internal static ConfigEntry<bool> FinePhysicsRegions = null!;
    internal static ConfigEntry<bool> SkipHitTriggers = null!;
    internal static ConfigEntry<bool> CapPhysicsRate = null!;
    internal static ConfigEntry<bool> EngineLoops = null!;
#if DEBUG
    internal static ConfigEntry<bool> LightCountTest = null!;
#endif
    internal static ConfigEntry<bool> SortByMaterial = null!;
    internal static ConfigEntry<bool> ShowDepth = null!;

    // Shows static level objects behind walls; never loot, players or anything that moves
    // (those aren't tracked), so it gives away nothing a map doesn't.
    internal static ConfigEntry<bool> ShowCells = null!;
    internal static ConfigEntry<bool> KeepCleanupPaused = null!;
    internal static ConfigEntry<bool> SpreadProfiles = null!;
    internal static ConfigEntry<bool> ProfilesOffThread = null!;
    internal static ConfigEntry<bool> CapSpawnAttempts = null!;
    internal static ConfigEntry<bool> KeepBackendConnection = null!;
    internal static ConfigEntry<bool> ProfileScripts = null!;
    internal static ConfigEntry<bool> LogDrops = null!;

    // Time spent in this plugin's own Update and OnGUI (overlay drawing, diagnostics), for
    // the cost overlay's "Smoothie itself" row.
    internal static long OwnTicks;
    // The part of OwnTicks spent drawing the overlays (OnGUI).
    internal static long OverlayTicks;

    private static ISender? _vanillaSender;
    private static BackendSender? _sender;

    private void Awake()
    {
        Log = Logger;

        const string bots = "1. Bots";
        ThrottleAnimators = Bind(bots, "Fewer animation updates for far or unseen bots", true,
            "Bots far away or out of sight get fewer animation updates per second. You won't notice it, and it saves CPU. Bots close to you, bots in a fight you can see, and bots you can hear stay fully smooth.", 100);
        SkipHiddenIk = Bind(bots, "Skip arm and head aiming for unseen bots nearby", true,
            "Bots near you that you can't see (behind you, or fully behind a wall) skip the costly positioning of their hands, head and gun. Nothing you can see changes; they also count as unseen for the setting above.", 90);
        SimplifyDistant = Bind(bots, "Hide tiny gear details on far-away bots", true,
            "Far-away bots stop drawing tiny details like sights, pouches and patches, which you can't make out at that distance anyway. Lasers and flashlights still work. Everything comes back as they get closer, and dead bots always show everything for looting.", 80);
        HideCulledWeapons = Bind(bots, "Hide guns of bots that are not drawn", true,
            "When the game stops drawing a bot you can't see, also stop drawing the gun in its hands. Online Tarkov already does this; offline it forgot the gun.", 70);

        UnityStepping = Bind(bots, "Update far bots on all CPU cores", true,
            "How \"Fewer animation updates for far or unseen bots\" catches a bot up: on, Unity does it with all other animations, spread over your CPU cores; off, Smoothie does it itself on the main thread, which costs more per bot. Only for comparing.", 65, true);
        PauseInEngine = Bind(bots, "Cheaper animation pauses for far bots (code patch, experimental)", true,
            "Smoothie saves CPU by updating the animation of far and unseen bots only a few times a second, and pausing it in between. Unity can only pause an animation by switching it off, and switching it back on is surprisingly expensive: Unity sets the bot's whole animation up again every time (on Streets with 15 bots ~0.4 ms a frame). With this on, the animation is never switched off; Unity just skips it until its next update. Bots look and move exactly the same. Only for this exact game build: on any other build of the game's engine it switches itself off.", 64, true);
        SimplifyDistance = Bind(bots, "Hide gear details beyond (m)", 60f,
            "How far away a bot has to be before its tiny details are hidden. Scope zoom counts: through a 4x scope, a bot 240 m away counts as 60 m.", 60, true,
            new AcceptableValueRange<float>(20f, 500f));
        FullRateDistance = Bind(bots, "Full animation for visible bots within (m)", 40f,
            "Bots you can see that are closer than this always animate at full rate. Scope zoom counts: through a 4x scope, 160 m counts as 40 m.", 50, true,
            new AcceptableValueRange<float>(10f, 300f));
        LowestRateDistance = Bind(bots, "Visible bots at lowest rate from (m)", 200f,
            "Bots you can see that are this far away (after scope zoom) get the lowest rate below. In between it changes gradually.", 40, true,
            new AcceptableValueRange<float>(20f, 1000f));
        LowestVisibleRate = Bind(bots, "Lowest updates per second, visible bots", 15f,
            "Animation updates per second for the most distant bots you can see.", 30, true,
            new AcceptableValueRange<float>(5f, 60f));
        OffscreenRate = Bind(bots, "Updates per second, unseen bots", 10f,
            "Animation updates per second for bots you can't see or hear.", 20, true,
            new AcceptableValueRange<float>(5f, 60f));
        FootstepRate = Bind(bots, "Updates per second, bots you can hear", 30f,
            "Lowest animation rate for bots close enough to hear. Their footstep sounds follow the animation, so this keeps the steps in time.", 10, true,
            new AcceptableValueRange<float>(10f, 60f));
        FootstepRange = Bind(bots, "Bots count as heard within (m)", 60f,
            "Bots within this distance count as audible. If the game plays footsteps from farther away during a raid, that distance is used instead.", 0, true,
            new AcceptableValueRange<float>(0f, 200f));

        CapSpawnAttempts = Bind(SpawnsSection, "Limit spawn attempts to free bot slots", true,
            "Only matters when the bots run on your PC (hosting, or solo). With high bot counts (Horde mode) the game tries to spawn hundreds of bots at once every 15 seconds, although only a few fit in, and asks the server for a bot profile for each one. That causes heavy stutters and huge memory use. With this on it only tries as many as can actually spawn. The number of bots in the raid stays the same. Switching it off mid-raid lets a flood start within seconds, and it lasts the rest of the raid. In a raid set to no bots (the menu's \"no bots\", Fika's No AI) it stops the game's spawn check from asking for bots at all.", 100);
        SpreadProfiles = Bind(SpawnsSection, "Load new bots a bit at a time", true,
            "Only matters when the bots run on your PC. Every new bot's gear is built from scratch, and doing many at once freezes the game. With this on, only a little is built per frame: no freeze, and new bots appear a moment later. Also, with the frame rate limit switched off, the game's background work queue (where much of a new bot's setup runs) gets a budget from your display's refresh rate instead of the menu's limit, so a spawn wave no longer drags the frame rate down to the menu's 60.", 90);
        ProfilesOffThread = Bind(SpawnsSection, "Load new bots on another CPU core (experimental)", false,
            "With \"Load new bots a bit at a time\" on: build the bots' gear on another CPU core instead. Skipped automatically if another mod changes how gear is built. If anything goes wrong it switches itself off for the session and logs why.", 80, true);

        const string other = "3. Other fixes";
        CorpseSettleTime = Bind(other, "Corpses stop moving after (s)", 5f,
            "Dead bodies keep simulating physics until they settle, up to 15 s (30 s in Fika). Ending it sooner saves CPU in big fights; bodies have almost always landed by then. Off automatically when Rupture is installed: its bodies react to hits after they land, and this would freeze them for good.", 100, false,
            new AcceptableValueRange<float>(2f, 30f));
        HotPathFixes = Bind(other, "Remove small CPU waste in game code", true,
            "Fixes spots where the game or a mod wastes CPU or memory: the main menu's video background is paused while it's hidden (it kept playing through raids, costing 2-3 CPU cores), bots the game tried to spawn no longer stay in memory until the game is closed, and the indoor lighting is no longer prepared every frame for scope and menu cameras that aren't drawing. Nothing you can see changes.", 90);
        NoMuzzleShadows = Bind(other, "Muzzle flashes of others cast no shadows", true,
            "Each shot lights up the area around the gun, and if that light casts shadows the game redraws everything nearby up to 6 times. In a big fight that more than doubles the shadow work. With this on, other players' and bots' muzzle flashes still light things up, just without shadows. Your own gun is left alone.", 85);
        HideCovered = Bind(other, "Hide objects covered by nearer ones (experimental)", true,
            "Most maps have no occlusion culling, so buildings, walls, cars and furniture behind nearer ones are still drawn (on Customs thousands of draw calls). With this on, level objects beyond the distance set below (3 m by default) stop drawing while the last frame's picture shows them completely covered by something nearer, checked on another CPU core. Covered objects' shadows: see \"Covered objects also stop casting shadows\". Something coming into view can appear a few frames late (about 1/50 s), only when you move, not when you turn. Doors, loot, players and anything that moves are left alone. Collecting the objects takes a moment 10 seconds into each raid.", 84);
        HideBeyond = Bind(other, "Hide covered objects only beyond (m)", 3f,
            "With \"Hide objects covered by nearer ones\" on: everything nearer than this stays drawn, all around you (behind you the game doesn't draw anyway). Lower hides more close by, like the room next to yours; higher is safer if something close ever shows up late, where it's big on screen.", 83, false,
            new AcceptableValueRange<float>(1f, 10f));
        SkipCoveredLamps = Bind(other, "Skip lamps lighting only covered areas", true,
            "With \"Hide objects covered by nearer ones\" on: a lamp or area light (the game's glowing panels, signs and windows) whose whole lit area is hidden behind nearer things (the next room, the other side of a wall) lights nothing you can see, so it stops lighting and drawing its shadows until any of that area may come into view. Each lamp with shadows draws everything around it again for its shadow (on Factory up to ~100 draw calls each). Its light can come on a moment late when the area comes into view, like hidden objects. The sun and moon are left alone.", 81);
        HideShadows = Bind(other, "Covered objects also stop casting shadows", true,
            "With \"Hide objects covered by nearer ones\" on: a covered object isn't drawn into shadow maps either, and the game's separate shadow-only meshes (simplified shadow casters for big structures) are checked and switched off when covered too. In buildings most of what's drawn is shadows: on Factory over a thousand draw calls a frame. Off: covered objects keep casting their shadows, which is always right but costs those draws.", 82);
        SkipOutOfView = Bind(other, "Skip LOD work for objects you cannot see", true,
            "Unity recomputes the level of detail of every object on the map for every frame, including everything behind you and everything the game itself isn't drawing; on big maps like Streets that's a large part of each frame (~0.7-1 ms measured). With this on, level objects out of view and beyond shadow range, and those the game has switched off, are skipped through Unity's own forced-level switch, a byte per object, so turning around costs next to nothing: everything is put back before the frame it could show in is drawn. Nothing you see changes. Only for this exact game build: on any other build of the game's engine it switches itself off.", 80);
        SkipTooFar = Bind(other, "Skip LOD work for objects too far to draw", true,
            "With \"Skip LOD work for objects you cannot see\" on: an object past the distance where the game's level of detail stops drawing it (small props and details far down a street) is also left out of Unity's per-frame LOD work, which otherwise works out for every one of them, every frame, that it draws nothing. On Streets that's most of the objects in view (~70k). Each is put back well before it would draw again (25% margin on Unity's own distance), so nothing you see changes. While aiming through a scope (its camera sees much further) this pauses, and what's in the scope's view is put back before the scope draws.", 80);
        FinePhysicsRegions = Bind(other, "Split the map finer for physics", true,
            "Whenever something moves under physics (a falling body, a thrown grenade, dropped loot, an airdrop) the game steps physics for the whole map, and every step checks each moving thing, including the 30-odd hit zones of every bot, against everything else in its part of the map. The map's own setup splits it into only a few parts, so each check runs through tens of thousands of objects. With this on it's split into the most parts Unity allows (16 by 16), so each check only looks at what's nearby. Same physics, same results. Takes a moment while the map loads.", 77);
        SkipHitTriggers = Bind(other, "Skip unused physics trigger checks", true,
            "While physics runs (a falling body, a grenade, dropped loot) Unity checks every hit zone of every bot (about 30 each) against every trigger zone it's inside, like spawn points and quest areas: thousands of checks per frame with bots around, for events nothing in the game listens to. The game finds who is in its zones its own way, every frame, whether physics runs or not. With this on those checks are skipped. Zones, quests, mines, extracts and bullets work exactly as before.", 76);
        CapPhysicsRate = Bind(other, "Run physics at most 60 times a second", true,
            "Whenever something moves under physics (a grenade, a falling body, dropped loot, a door) the game updates physics for the whole map once for every frame you see, and each update costs about the same no matter what moved (on Streets with bots ~1.2 ms). At 120 fps that's twice as many updates as at 60 fps, for the same result. With this on physics updates at most 60 times a second, each one covering the time since the last, exactly as the game runs at 60 fps. Below 60 fps nothing changes.", 77);
        EngineLoops = Bind(other, "Faster engine culling loops (code patch)", true,
            "Rewrites four loops in Unity's own code, in memory while the game runs (nothing on disk changes), to do the same work faster: the scan over every object on the map after each frame's culling (~220k on Streets) checks 16 at a time instead of one, the level-of-detail loop skips the groups \"Skip LOD work for objects you cannot see\" has set aside, instead of working them out just to throw the result away, the navigation-obstacle update stops working out every door obstacle's position for path-following agents the game doesn't have (bots find their way differently), and the light setup, done several times a frame, reads the number of lights Unity already keeps instead of counting every light on the map one by one. Nothing you see changes. Only for this exact game build: on any other build of the game's engine it switches itself off, and off puts Unity's code back.", 78);
        EngineLoops.SettingChanged += (_, _) => StartFlip(EngineLoops, "faster engine loops");
#if DEBUG
        // Debug builds: the light count patch on its own, for a paired A/B (PageDown).
        LightCountTest = Bind(OverlaySection, "Test: read the light count (code patch)", true,
            "With \"Faster engine culling loops\" on: reads the number of lights Unity keeps instead of counting them one by one. Off counts them as Unity does. Debug builds only.", 60, true);
        LightCountTest.SettingChanged += (_, _) => StartFlip(LightCountTest, "reading the light count");
#endif
        SortByMaterial = Bind(other, "Draw level objects grouped by material", true,
            "Draws everything solid grouped by material instead of nearest first, so the game switches materials less often (on Streets ~270 fewer switches a frame). That moves a little work from your CPU, which Tarkov is almost always waiting on, to your graphics card (things hidden behind nearer ones get shaded too). Nothing you see changes. If the overlay shows GPU wait going up with this on, your graphics card is the limit: switch it off.", 79);
        KeepCleanupPaused = Bind(other, "Prevent memory cleanup stutters in raids", false,
            "On PCs with enough RAM the game pauses memory cleanup during a raid, so it can't stutter in a fight. Opening your inventory (an SPT feature) runs a cleanup and leaves cleanup on for the rest of the raid, which then stutters every minute or two. With this on, that cleanup still runs, then cleanup is paused again; if 4 GB of garbage piles up, one more runs. Only in raids, and only on PCs the game itself pauses cleanup on (the server's RAM limit, 25 GB by default): below that, cleanup stays on as the game wants. If something else switches it back on right after every pause (another mod), it's left on for the rest of that raid.", 75);
        KeepBackendConnection = Bind(other, "Keep server connection open (faster menus)", true,
            "Keeps one connection to the SPT server open instead of reconnecting for every request. Speeds up menus, most of all when the server runs on another PC.", 80);

        // All of it for tracking down problems, so hidden unless "Show advanced settings" is ticked.
        PauseFixes = Bind(OverlaySection, "Turn off all fixes (A/B test)", false,
            "Runs the game as if Smoothie weren't installed, without touching any of your settings above: switch it on and off and compare with the overlay. The overlay and logs keep working. Switched on before a raid, that raid runs fully without Smoothie, bot spawning fixes included. Switched on during a raid, the bot spawning fixes stay on: without them one moment in a Horde raid can ruin the rest of it. The main menu's hidden background videos stay paused either way: that's a game bug costing 2-3 CPU cores, and the comparison would mostly measure it. Always starts off when the game starts.", 110, true);
        // Never left on by accident: the mod would look installed but do nothing.
        PauseFixes.Value = false;
        ShowCosts = Bind(OverlaySection, "Show performance overlay", false,
            "Shows what each frame is spent on (bot animation, AI, scripts, rendering, ...) with a graph for each. Toggle a setting and watch its line change; yellow marks a setting change, red a memory cleanup. Costs a little performance while shown.", 100, true);
        ShowCosts.SettingChanged += (_, _) => Overlay.Apply();
        // A/B for hiding covered objects: each flip is measured on its own (see MeasureFlip).
        HideCovered.SettingChanged += (_, _) => StartFlip(HideCovered, "hiding covered objects");
        SortByMaterial.SettingChanged += (_, _) => StartFlip(SortByMaterial, "grouping by material");
        SkipCoveredLamps.SettingChanged += (_, _) => StartFlip(SkipCoveredLamps, "skipping covered lamps");
        SkipOutOfView.SettingChanged += (_, _) => StartFlip(SkipOutOfView, "skipping LOD work");
        SkipTooFar.SettingChanged += (_, _) => StartFlip(SkipTooFar, "skipping LOD work for far objects");
        FinePhysicsRegions.SettingChanged += (_, _) => StartFlip(FinePhysicsRegions, "finer physics regions");
        SkipHitTriggers.SettingChanged += (_, _) => StartFlip(SkipHitTriggers, "skipping hit zone trigger checks");
        CapPhysicsRate.SettingChanged += (_, _) => StartFlip(CapPhysicsRate, "physics at most 60 a second");
        PauseInEngine.SettingChanged += (_, _) => StartFlip(PauseInEngine, "cheaper animation pauses");
        // Shown and checked again from scratch, so each state is measured on its own.
        HideShadows.SettingChanged += (_, _) =>
        {
            DepthCulling.ShowAll();
            StartFlip(HideShadows, "covered objects without shadows");
        };
        LogDrops = Bind(OverlaySection, "Log stutters and their causes", false,
            "Writes a line to the BepInEx log for every stutter, with what caused it, and a snapshot whenever the frame rate drops and stays down. Useful for bug reports.", 90, true);
        ShowStats = Bind(OverlaySection, "Show text stats panel", false,
            "A text panel with frame timings, memory and bot counts.", 80, true);
        ShowCells = Bind(OverlaySection, "Show hiding cells and hidden objects (debug)", false,
            "Draws through walls, within 150 m and in view, every object by its last result: red hidden, yellow something behind it shows; while aiming, magenta something behind it shows in the scope's own depth, cyan close enough to the scope's view that a flick may swing it in, kept drawn, orange the scope has no depth of its own; green covered, but kept drawn because you're moving and its cover's edge could slide off it before the next check. Covered objects the game isn't drawing anyway aren't shown. Only static level objects: never loot, players or anything that moves.", 74, true);
        ShowDepth = Bind(OverlaySection, "Show the depth image hiding uses", false,
            "Shows the small depth image \"Hide objects covered by nearer ones\" tests against, under the overlay: brighter is nearer, black is sky or far away. Purple: what your scope sees while aiming through one, with a margin; anything there must also be covered in the scope's own depth (shown to the right), or it stays drawn. Only what's already on your screen.", 75, true);
        ProfileScripts = Bind(OverlaySection, "Log time per script (slow)", false,
            "Times every script and every stage of each frame, and writes detailed reports to the log. Costs noticeable performance while on: for tracking down a problem, not for playing.", 70, true);
        ProfileScripts.SettingChanged += (_, _) =>
        {
            if (ProfileScripts.Value) Diagnostics.StartProfiling();
            else Diagnostics.StopProfiling();
        };

        // So a toggle mid-raid can be lined up with the frame times around it.
        Config.SettingChanged += (_, e) =>
        {
            ApplyFixes();
            Diagnostics.ApplyRecorders();
            Overlay.MarkChange();
            if (Diagnostics.Logging) Log.LogInfo($"{DateTime.Now:HH:mm:ss.fff} setting changed: {e.ChangedSetting.Definition.Key} = {e.ChangedSetting.BoxedValue}");
        };

        // Each feature on its own: one that can't start (a game or Fika update moved
        // something) is logged and skipped instead of taking the whole plugin down.
        Safely("backend connection", ApplySender);
        Safely("animation throttling", () =>
        {
            new PlayerLateUpdatePatch().Enable();
            Camera.onPreCull += AnimatorThrottle.OpticPreCull;
            new ArmsUpdatePatch().Enable();
            new MovementUpdatePatch().Enable();
        });

        Safely("raid start", () => new RaidStartPatch().Enable());
        Safely("weapon culling", HandsCulling.Enable);
        Safely("hot-path fixes", HotPaths.Enable);
        Safely("hidden-bot IK", Occlusion.Enable);
        Safely("bot profile budget", ProfileBudget.Enable);
        Safely("hiding covered objects", DepthCulling.Enable);
        Safely("skipping LOD groups", LodCulling.Enable);
        // Disabled, see Occlusion.ObservedIkVisibilityPatch: Fika's code is left alone.
        // Safely("Fika hidden-bot IK", () =>
        // {
        //     if (Occlusion.ObservedVisualPass() != null) new Occlusion.ObservedIkVisibilityPatch().Enable();
        // });
        Safely("diagnostics", () =>
        {
            Diagnostics.Start();
            if (ProfileScripts.Value) Diagnostics.StartProfiling();
        });
        Safely("stats overlay", Overlay.Apply);
        Safely("forced-GC watch", () => Diagnostics.WatchForcedCollections(new Harmony("com.tominocz.smoothie.gcwatch")));
        Safely("bot request watch", () => Diagnostics.WatchBotRequests(new Harmony("com.tominocz.smoothie.botwatch")));
    }

    // Order sorts within a section (higher first); advanced ones are hidden in the F12 menu
    // unless "Show advanced settings" is ticked.
    private ConfigEntry<T> Bind<T>(string section, string key, T value, string description, int order,
        bool advanced = false, AcceptableValueBase? range = null) =>
        Config.Bind(section, key, value, new ConfigDescription(description, range,
            new ConfigurationManagerAttributes { Order = order, IsAdvanced = advanced ? true : null }));

    // Our code runs inside game methods called every frame. If it throws, switch that one
    // feature off for the rest of the session and let the game carry on as vanilla, rather
    // than breaking the method. Not by changing the setting: BepInEx would save that, and a
    // fixed version would then start with the feature off.
    private static readonly HashSet<ConfigEntryBase> Broken = new();
    // Features that failed before this session, and features off only until the raid ends.
    private static readonly HashSet<ConfigEntryBase> FailedBefore = new();
    private static readonly HashSet<ConfigEntryBase> RetryNextRaid = new();
    // In-game notice of a feature going off; shown from Update (Fail can run on a worker).
    private static string? _notice;

    // Every feature checks this. "Turn off all fixes" is a second gate on top of each
    // feature's own setting, so it switches the mod off without changing any of them;
    // the overlay and logs aren't fixes and keep working.
    // Nothing changes the hideout: it stays exactly as the game has it. Only the overlay and logs
    // (when asked for) and the server connection, which isn't part of it, keep going there.
    // Called many times a frame (per bot, from worker threads too), so the hideout is looked up
    // once a frame, at the start of Update.
    internal static bool On(ConfigEntry<bool> feature) =>
        feature.Value && !Broken.Contains(feature) && (Fixing || IgnoresPause(feature)) &&
        (!_inHideout || feature.Definition.Section == OverlaySection || feature == KeepBackendConnection);

    private static bool _inHideout;

    // For the menu video pause: the A/B switch leaves it on. It only stops a game bug (the
    // hidden menu background's videos playing on through raids, 2-3 CPU cores); with it off
    // the comparison would mostly measure those videos, not Smoothie.
    internal static bool OnEvenWhenPaused(ConfigEntry<bool> feature) =>
        feature.Value && !Broken.Contains(feature);

    // For settings that aren't on/off switches (corpse settle time).
    internal static bool Fixing => !PauseFixes.Value;

    // The spawn handling stays on too: it isn't a per-frame cost to compare, and a Horde
    // raid that goes one spawn check without it (every 10-15 s) queues ~100 extra bots and
    // freezes for seconds at a time, for the rest of the raid (seen 10:30, A/B switched on).
    private const string SpawnsSection = "2. Bot spawning (when you host)";
    private const string OverlaySection = "4. Overlay and logs";

    // Switched on before a raid, the spawn fixes are off for that raid too: a full A/B. What it
    // was when the raid started counts (kept up to date until then, see Update).
    private static bool IgnoresPause(ConfigEntryBase entry) =>
        entry.Definition.Section == OverlaySection || entry.Definition.Section == SpawnsSection && !_pausedBeforeRaid;

    private static bool _pausedBeforeRaid;

    // For the overlay's banner.
    internal static bool SpawnFixesPaused => !Fixing && _pausedBeforeRaid;

    // A feature that switched itself off (another mod, another game build, an error) says so in
    // the F12 menu: "(disabled: why)" after its name; null puts the plain name back. The menu
    // picks it up the next time it's opened.
    internal static void SetDisabled(ConfigEntryBase entry, string? why)
    {
        foreach (var tag in entry.Description.Tags)
        {
            if (tag is not ConfigurationManagerAttributes attributes) continue;
            var name = why == null ? null : $"{entry.Definition.Key} (disabled: {why})";
            if (attributes.DispName == name) return;
            attributes.DispName = name;
            if (why != null) Log.LogInfo($"\"{entry.Definition.Key}\" is disabled: {why}");
            return;
        }
    }

    // A first error in a raid switches the feature off for the rest of that raid only: it starts
    // again from scratch the next raid, so a rare timing bug can't cost the whole session (a 1.0.4
    // report: covered-object hiding off for good after one). A second error keeps it off for the
    // session. Either way the player is told in game, not only in the log.
    internal static void Fail(ConfigEntryBase feature, Exception ex)
    {
        if (!Broken.Add(feature)) return;
        var retry = FailedBefore.Add(feature) && Diagnostics.InRaid;
        if (retry) RetryNextRaid.Add(feature);
        SetDisabled(feature, retry ? "error, back next raid; see the log" : "error, see the log");
        Log.LogError($"\"{feature.Definition.Key}\" switched off for this {(retry ? "raid" : "session")} after an error: {ex}");
        _notice = $"Smoothie: \"{feature.Definition.Key}\" stopped after an error" +
                  (retry ? " and starts again next raid." : " and stays off until you restart.") +
                  " Please report it with BepInEx/LogOutput.log.";
        Safely("undoing it", ApplyFixes);
    }

    private static void ShowNotice()
    {
        var notice = Interlocked.Exchange(ref _notice, null);
        if (notice == null) return;
        try
        {
            EFT.Communications.NotificationManager.DisplayWarningNotification(notice, EFT.Communications.ENotificationDurationType.Long);
        }
        catch (Exception ex)
        {
            Log.LogWarning($"in-game notice failed ({ex.Message}): {notice}");
        }
    }

    // Puts back what a fix changed in the world once it's off, whichever way it went off:
    // its setting, the A/B switch or an error. Run on every setting change.
    private static void ApplyFixes()
    {
        if (!On(SimplifyDistant)) DistantDetail.RestoreAll();
        if (!On(HideCulledWeapons)) HandsCulling.RestoreAll();
        if (!On(HideCovered)) DepthCulling.ShowAll();
        if (!On(SkipOutOfView)) LodCulling.ShowAll();
        HotPaths.ResyncMenuVideos();
        Guard(FinePhysicsRegions, HotPaths.SyncPhysicsRegions);
        ApplySender();
    }

    private static void SyncAnimatorPause()
    {
        var note = AnimatorPause.Note;
        AnimatorPause.Sync(On(PauseInEngine) && On(ThrottleAnimators) && Diagnostics.InRaid);
        if (AnimatorPause.Note != note) Log.LogInfo($"engine patch, animator pause: {AnimatorPause.Note}");
    }

    private static void SyncPatch(EnginePatch patch, bool on)
    {
        var note = patch.Note;
        patch.Set(on);
        if (patch.Note == note) return;
        Log.LogInfo($"engine patch, {patch.Note}");
        if (on && !patch.Applied && SceneSkip.Available) SetDisabled(EngineLoops, "the game's engine code isn't as expected");
    }

    // Once, on the first frame (every plugin has loaded by then): features that can't run here.
    private static bool _checkedSetup;

    private static void CheckSetup()
    {
        _checkedSetup = true;
        if (HotPaths.RuptureInstalled) SetDisabled(CorpseSettleTime, "Rupture is installed");
        if (SceneSkip.Check()) return;
        SetDisabled(EngineLoops, "not made for this game version");
        SetDisabled(SkipOutOfView, "not made for this game version");
    }

    // A type that may not exist (Fika's, when it isn't installed): null, without the warning
    // AccessTools.TypeByName writes to the log for every miss.
    internal static Type? FindType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetType(fullName, false) is { } type) return type;
        }

        return null;
    }

    // Runs one feature's per-frame work; an error switches off that feature, not the others.
    private static void Guard(ConfigEntryBase feature, Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex)
        {
            Fail(feature, ex);
        }
    }

    private static void Safely(string feature, Action enable)
    {
        try
        {
            enable();
        }
        catch (Exception ex)
        {
            Log.LogError($"{feature} disabled, it failed to start: {ex}");
        }
    }

    private static void ApplySender()
    {
        _vanillaSender ??= SenderHelper._senders[SenderHelper.EConnectionType.Default];

        ISender sender = _vanillaSender;
        if (On(KeepBackendConnection))
        {
            // If the runtime can't build it, lose this one feature, not the whole plugin.
            try
            {
                sender = _sender ??= new BackendSender();
            }
            catch (Exception ex)
            {
                Log.LogError($"keep-alive backend connection unavailable, using the game's sender: {ex}");
            }
        }

        SenderHelper._senders[SenderHelper.EConnectionType.Default] = sender;
    }

    private bool _wasInRaid;

    // After each flip of an A/B setting: FlipSettle s to settle, then FlipWindow s of frame times, draw calls and
    // SetPass averaged into one log line (with logging on), so on and off can be compared
    // standing still.
    private static float _flipAt = -1f;
    private static readonly List<float> FlipFrames = new();
    private static double _flipDraws, _flipSetPass, _flipSync;
    private static ConfigEntry<bool>? _flipSetting;
    private static string _flipName = "";
    // "Turn off all fixes" reads the other way round: on means Smoothie is off.
    private static bool FlipOn => _flipSetting == PauseFixes ? !PauseFixes.Value : On(_flipSetting!);

    // A/B: frames are measured from FlipSettle s after a flip, for FlipWindow s (hiding needs a few
    // rounds and LOD skipping ~15 frames to settle; shorter windows were mostly noise).
    private const float FlipSettle = 3f, FlipWindow = 10f;

    private static void StartFlip(ConfigEntry<bool> setting, string name)
    {
        _flipSetting = setting;
        _flipName = name;
        _flipAt = Time.unscaledTime + FlipSettle;
        FlipFrames.Clear();
        _flipDraws = _flipSetPass = _flipSync = 0;
    }

#if DEBUG
    // Debug, PageDown: an automatic A/B of the setting changed last (none yet: all fixes), flipped
    // after every window, SeriesWindows of them, then both states' averages in one line. Bots
    // move and fights come and go, so a single flip each way is mostly noise; alternating windows
    // average it out. An even count ends where it started.
    private const int SeriesWindows = 8;
    private static int _seriesLeft;
    private static readonly List<float> SeriesOn = new(), SeriesOff = new(), SyncOn = new(), SyncOff = new();

    // Debug, PageDown: a paired A/B of the setting changed last. Long windows compare moments far
    // apart (bots move, fights start and stop, the view drifts), which buried every small change.
    // Here the setting flips every PairSettle + PairWindow s, and each ON second is compared with
    // the OFF second right next to it: the game barely changes within two seconds, so the drift
    // cancels across Pairs pairs. Reported per part of the frame (overlay on), with a 95% margin.
    // Only for settings that act on the next frame (not covered-object hiding or LOD skipping,
    // which need several seconds to settle: PageUp runs the long series for those).
    private const float PairSettle = 1f, PairWindow = 2f;
    private const int Pairs = 30;
    private static int _pairWindowsLeft;
    // A benchmark is measuring: Debug logging that costs frame time holds off.
    internal static bool Benchmarking => _pairWindowsLeft > 0 || _seriesLeft > 0;
    private static float _pairWindowStart;
    private static int _pairFrames;
    private static double _pairFrameSum;
    private static double[] _pairPartSums = Array.Empty<double>();
    private static readonly float[] PairPartsNow = new float[16];
    private static bool _pairHaveParts;
    private static readonly List<(bool On, double Frame, double[] Parts)> PairWindows = new();

    // PageDown and PageUp compare the setting changed last, or else all of Smoothie.
    private static void PickFlip()
    {
        if (_flipSetting != null) return;
        _flipSetting = PauseFixes;
        _flipName = "Smoothie (all fixes)";
    }

    private static void StartPairs()
    {
        PickFlip();
        PairWindows.Clear();
        _pairWindowsLeft = Pairs * 2;
        Log.LogInfo($"A/B pairs of {_flipName}: {Pairs} pairs of {PairWindow:0} s windows, flipping every {PairSettle + PairWindow:0} s; stay put, about {Pairs * 2 * (PairSettle + PairWindow):0} s");
        NextPairWindow();
    }

    private static void NextPairWindow()
    {
        _flipSetting!.Value = !_flipSetting.Value;
        _flipAt = -1f; // not the long A/B window
        _pairWindowStart = Time.unscaledTime + PairSettle;
        _pairFrames = 0;
        _pairFrameSum = 0;
        _pairPartSums = new double[Overlay.PartNames.Length];
        _pairHaveParts = true;
    }

    private static void MeasurePairs()
    {
        if (_pairWindowsLeft == 0) return;
        if (!Diagnostics.InRaid)
        {
            _pairWindowsLeft = 0;
            Log.LogInfo("A/B pairs stopped: left the raid");
            return;
        }

        if (Time.unscaledTime < _pairWindowStart) return;
        _pairFrames++;
        _pairFrameSum += Time.unscaledDeltaTime * 1000f;
        if (Overlay.PartsThisFrame(PairPartsNow))
            for (var i = 0; i < _pairPartSums.Length; i++) _pairPartSums[i] += PairPartsNow[i];
        else _pairHaveParts = false;
        if (Time.unscaledTime < _pairWindowStart + PairWindow) return;

        var on = FlipOn;
        PairWindows.Add((on, _pairFrameSum / _pairFrames, _pairHaveParts ? _pairPartSums.Select(v => v / _pairFrames).ToArray() : Array.Empty<double>()));
        if (--_pairWindowsLeft > 0)
        {
            NextPairWindow();
            return;
        }

        LogPairs();
    }

    // ON minus OFF per neighbouring pair: mean and 95% margin (1.96 standard errors).
    private static void LogPairs()
    {
        static string Difference(List<double> d) =>
            d.Count < 2 ? "n/a" : $"{d.Average():+0.000;-0.000} ± {1.96 * Math.Sqrt(d.Sum(x => (x - d.Average()) * (x - d.Average())) / (d.Count - 1) / d.Count):0.000} ms";

        var frame = new List<double>();
        var names = Overlay.PartNames;
        var parts = names.Select(_ => new List<double>()).ToArray();
        for (var i = 0; i + 1 < PairWindows.Count; i += 2)
        {
            var (a, b) = (PairWindows[i], PairWindows[i + 1]);
            var (on, off) = a.On ? (a, b) : (b, a);
            if (on.On == off.On) continue;
            frame.Add(on.Frame - off.Frame);
            if (on.Parts.Length == names.Length && off.Parts.Length == names.Length)
                for (var p = 0; p < names.Length; p++) parts[p].Add(on.Parts[p] - off.Parts[p]);
        }

        var onAverage = PairWindows.Where(w => w.On).Average(w => w.Frame);
        var offAverage = PairWindows.Where(w => !w.On).Average(w => w.Frame);
        var partsLine = parts[0].Count > 1
            ? string.Join(", ", names.Select((n, p) => $"{n} {Difference(parts[p])}"))
            : "parts: turn the overlay on";
        Log.LogInfo($"A/B pairs {_flipName}: {frame.Count} pairs, frame ON {onAverage:0.00} ms, OFF {offAverage:0.00} ms; ON minus OFF: frame {Difference(frame)} | {partsLine}");
    }

    private static void StartSeries()
    {
        PickFlip();
        SeriesOn.Clear();
        SeriesOff.Clear();
        SyncOn.Clear();
        SyncOff.Clear();
        _seriesLeft = SeriesWindows;
        Log.LogInfo($"A/B series of {_flipName}: {SeriesWindows} windows of {FlipSettle + FlipWindow:0} s, flipping after each; stay put");
        FlipSeries(_flipSetting!, _flipName);
    }

    private static void FlipSeries(ConfigEntry<bool> setting, string name)
    {
        setting.Value = !setting.Value;
        StartFlip(setting, name);
    }

    // After each window of a series: the next flip, or the summary.
    private static void SeriesWindowDone(bool on, float average, float sync)
    {
        (on ? SeriesOn : SeriesOff).Add(average);
        (on ? SyncOn : SyncOff).Add(sync);
        if (--_seriesLeft > 0)
        {
            FlipSeries(_flipSetting!, _flipName);
            return;
        }

        string Line(List<float> frames) => $"{frames.Average():0.00} ms ({string.Join(", ", frames.Select(f => f.ToString("0.00")))})";
        var difference = SeriesOn.Average() - SeriesOff.Average();
        var syncLine = float.IsNaN(SyncOn.Average()) ? " (physics sync: turn the overlay on)" : $"; physics sync ON {SyncOn.Average():0.00} ms, OFF {SyncOff.Average():0.00} ms";
        Log.LogInfo($"A/B series {_flipName}: ON {Line(SeriesOn)}, OFF {Line(SeriesOff)}; ON is {difference:+0.00;-0.00} ms a frame ({100.0 * difference / SeriesOff.Average():+0.0;-0.0}%)" + syncLine);
    }
#endif

    private static void MeasureFlip()
    {
        if (_flipAt < 0f || Time.unscaledTime < _flipAt) return;
        var series = false;
#if DEBUG
        series = _seriesLeft > 0;
#endif
        if (!Diagnostics.InRaid || !Diagnostics.Logging && !series)
        {
            _flipAt = -1f;
#if DEBUG
            _seriesLeft = 0;
#endif
            return;
        }

        FlipFrames.Add(Time.unscaledDeltaTime * 1000f);
        _flipDraws += Math.Max(0, Diagnostics.DrawCalls);
        _flipSetPass += Math.Max(0, Diagnostics.SetPassCalls);
        _flipSync += Overlay.SyncMs;
        if (Time.unscaledTime < _flipAt + FlipWindow) return;

        _flipAt = -1f;
        FlipFrames.Sort();
        var average = FlipFrames.Average();
        var median = FlipFrames[FlipFrames.Count / 2];
        var worst = FlipFrames[(int)(FlipFrames.Count * 0.95f)];
        var draws = Diagnostics.DrawCalls >= 0 ? $"{_flipDraws / FlipFrames.Count:0} ({_flipSetPass / FlipFrames.Count:0} SetPass)" : "n/a (overlay off)";
        var flipOn = FlipOn;
        var sync = (float)(_flipSync / FlipFrames.Count);
        Log.LogInfo($"A/B {_flipName} {(flipOn ? "ON " : "OFF")}: frame {average:0.00} ms (median {median:0.00}, 95% {worst:0.00}, {1000f / average:0} fps), draw calls {draws}" +
                    (float.IsNaN(sync) ? "" : $", physics sync {sync:0.00} ms") +
                    (_flipSetting == HideCovered && On(HideCovered) ? $", {DepthCulling.Saving} drawn objects hidden, check {DepthCulling.WorkerMs:0.0} ms on a worker" : "") +
                    (_flipSetting == SkipOutOfView || _flipSetting == SkipTooFar ? ", " + LodCulling.Summary() : "") +
                    (Overlay.PartsSummary() is { Length: > 0 } parts ? $" |{parts.Replace("  overlay, last second:", " overlay, last second (not this A/B window):")}" : ""));
#if DEBUG
        if (series) SeriesWindowDone(flipOn, (float)average, sync);
#endif
    }
    private float _lastSweep;

    // Player types that replace Player.LateUpdate without calling it (Fika's observed bots
    // on a client), so the patch on it never runs for them. They're driven from here, once
    // per frame, without touching their code, so no Fika update can break it. Before or
    // after their own LateUpdate doesn't matter: a bot is only throttled where Fika skips its
    // IK (AnimatorThrottle.PickInterval), and a curve read a frame late is a footstep a frame late.
    private static readonly Dictionary<Type, bool> OwnLateUpdate = new();

    private void LateUpdate()
    {
        try
        {
            if (!Diagnostics.InRaid) return;
            var players = Singleton<GameWorld>.Instance.AllAlivePlayersList;
            for (var i = 0; i < players.Count; i++)
            {
                // Switched off (AILimit or ORBIT's Ghost Mode on the host, and Fika then on
                // clients too): Unity skips their LateUpdate, so do we, after ending a catch-up
                // step their own LateUpdate would have ended.
                var player = players[i];
                if (player == null) continue;
                if (!player.isActiveAndEnabled)
                {
                    AnimatorThrottle.EndStepIfAsleep(player);
                    continue;
                }

                var type = player.GetType();
                if (!OwnLateUpdate.TryGetValue(type, out var own))
                {
                    own = AccessTools.Method(type, nameof(Player.LateUpdate), Type.EmptyTypes)?.DeclaringType != typeof(Player);
                    OwnLateUpdate[type] = own;
                    if (own) Log.LogInfo($"{type.Name} has its own LateUpdate, throttling it from Smoothie's");
                }

                // Handled at most once a frame (BeforeLateUpdateSafe checks), so an override
                // that does call the base is fine too.
                if (own) AnimatorThrottle.BeforeLateUpdateSafe(player);
            }
        }
        catch (Exception ex)
        {
            Fail(ThrottleAnimators, ex);
        }
    }

    private void Update()
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            _inHideout = Diagnostics.InHideout;
            if (!_checkedSetup) CheckSetup();
            if (_notice != null) ShowNotice();
#if DEBUG
            // Debug builds: Delete flips "Turn off all fixes (A/B test)" and measures it, without
            // the F12 menu, which costs frame time of its own. Needs "Log stutters" on to log.
            if (Input.GetKeyDown(KeyCode.Delete))
            {
                PauseFixes.Value = !PauseFixes.Value;
                StartFlip(PauseFixes, "Smoothie (all fixes)");
            }

            // End: a made-up error in covered-object hiding, to check the notice and the retry.
            if (Input.GetKeyDown(KeyCode.End)) Fail(HideCovered, new InvalidOperationException("test error (End key, Debug build)"));
            // Home: what the physics step is busy with (Diagnostics, logged a second later).
            if (Input.GetKeyDown(KeyCode.Home)) Diagnostics.StartPhysicsCensus();
            // Insert: keep the physics step running, for an A/B under a steady physics load.
            if (Input.GetKeyDown(KeyCode.Insert) && Diagnostics.InRaid) HotPaths.TogglePhysicsKeeper();
            // PageDown: a paired A/B of the setting changed last (StartPairs); PageUp: the long series
            // for settings that need seconds to settle (StartSeries).
            if (Input.GetKeyDown(KeyCode.PageDown) && Diagnostics.InRaid && _seriesLeft == 0 && _pairWindowsLeft == 0) StartPairs();
            if (Input.GetKeyDown(KeyCode.PageUp) && Diagnostics.InRaid && _seriesLeft == 0 && _pairWindowsLeft == 0) StartSeries();
#endif
            // Engine patches are written only here: the main thread's script phase, before this
            // frame's culling and after the last one's, so Unity can't be inside a patched loop.
            SyncPatch(EnginePatch.VisibilityScan, On(EngineLoops));
            // Only skips groups "Skip LOD work" forced: without it, nothing to gain.
            SyncPatch(EnginePatch.LodLoop, On(EngineLoops) && On(SkipOutOfView));
            Guard(PauseInEngine, SyncAnimatorPause);
            // Work only NavMeshAgents use; EFT has none (Unity's code runs while one is active).
            SyncPatch(EnginePatch.NavCrowd, On(EngineLoops));
            SyncPatch(EnginePatch.NavVelocity, On(EngineLoops));
#if DEBUG
            SyncPatch(EnginePatch.LightCount, On(EngineLoops) && LightCountTest.Value);
#else
            SyncPatch(EnginePatch.LightCount, On(EngineLoops));
#endif
            // Leaving a raid: drop everything that refers to its players, so none of it is
            // kept alive through the menu.
            var inRaid = Diagnostics.InRaid;
            if (_wasInRaid && !inRaid)
            {
                HandsCulling.RestoreAll();
                DistantDetail.RestoreAll();
                DepthCulling.Clear();
                LodCulling.Clear();
                Occlusion.OnRaidStart();
                Diagnostics.OnRaidEnd();
                // Its state is cleared above: what failed once this raid gets another go.
                foreach (var feature in RetryNextRaid)
                {
                    Broken.Remove(feature);
                    SetDisabled(feature, null);
                    Log.LogInfo($"\"{feature.Definition.Key}\" back on for the next raid after its error");
                }

                RetryNextRaid.Clear();
            }

            _wasInRaid = inRaid;
            if (inRaid) Guard(SortByMaterial, HotPaths.SortByMaterial);
            if (inRaid) Guard(FinePhysicsRegions, HotPaths.SyncPhysicsRegions);
            Guard(SkipHitTriggers, HotPaths.SyncHitTriggers);
            if (!inRaid) _pausedBeforeRaid = PauseFixes.Value;
            if (inRaid && Time.unscaledTime - _lastSweep > 5f)
            {
                _lastSweep = Time.unscaledTime;
                Guard(HideCulledWeapons, HandsCulling.Sweep);
                Guard(SimplifyDistant, DistantDetail.Sweep);
                Guard(HotPathFixes, HotPaths.ResyncMenuVideos);
#if DEBUG
                JitNames.Poll();
#endif
            }

            if (inRaid) Guard(SkipOutOfView, LodCulling.Tick);
            if (inRaid && On(HideCovered))
            {
                Guard(HideCovered, DepthCulling.Scan);
                Guard(HideCovered, DepthCulling.Tick);
            }
            Guard(KeepCleanupPaused, Diagnostics.KeepCleanupPaused);
            MeasureFlip();
#if DEBUG
            MeasurePairs();
#endif
            Diagnostics.Tick();
        }
        catch (Exception ex)
        {
            Diagnostics.StopProfiling();
            Fail(LogDrops, ex);
        }
        finally
        {
            OwnTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
        }
    }

    private string _overlay = "";
    private float _overlayAt;

    private void OnGUI()
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            DrawOverlays();
        }
        finally
        {
            var ticks = System.Diagnostics.Stopwatch.GetTimestamp() - start;
            OwnTicks += ticks;
            OverlayTicks += ticks;
        }
    }

    private void DrawOverlays()
    {
        var top = 10f;
        if (On(ShowCosts)) top = Overlay.Draw(10, top) + 6;
        if (On(ShowDepth) && DepthCulling.Preview is { } depth && Event.current.type == EventType.Repaint)
        {
            GUI.DrawTexture(new Rect(10, top, depth.width * 3, depth.height * 3), depth);
            // The scope's own depth while aiming through one, to the right.
            if (DepthCulling.ScopePreview is { } scope) GUI.DrawTexture(new Rect(20 + depth.width * 3, top, scope.width * 2, scope.height * 2), scope);
            top += depth.height * 3 + 2;
            GUI.Label(new Rect(10, top, depth.width * 3, 22), "purple: your scope's view (with margin); what's there must also be covered in the scope's own depth (right), or stays drawn");
            top += 24;
        }

        if (!On(ShowStats)) return;

        // Rebuilt twice a second; formatting it every frame would show up in the profile.
        if (Time.unscaledTime - _overlayAt > 0.5f)
        {
            _overlayAt = Time.unscaledTime;
            _overlay = $"Smoothie: {AnimatorThrottle.Throttled} bots throttled, {AnimatorThrottle.Steps} steps " +
                       $"({AnimatorThrottle.StepMs:0.00} ms), {Occlusion.HiddenCount} close bots hidden, " +
                       $"footstep range {AnimatorThrottle.AudibleRange:0} m\n" + Diagnostics.Snapshot(12);
        }

        GUI.Box(new Rect(10, top, 900, 330), "");
        GUI.Label(new Rect(16, top + 4, 890, 320), _overlay);
    }

    private class PlayerLateUpdatePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Player), nameof(Player.LateUpdate));

        [PatchPrefix]
        private static void Prefix(Player __instance) => AnimatorThrottle.BeforeLateUpdateSafe(__instance);
    }

    private class ArmsUpdatePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Player), nameof(Player.ArmsUpdate));

        [PatchPrefix]
        private static void Prefix(Player __instance, out bool __state)
        {
            try
            {
                __state = AnimatorThrottle.HoldArms(__instance);
            }
            catch (Exception ex)
            {
                __state = false;
                Fail(ThrottleAnimators, ex);
            }
        }

        // A finalizer so the arms mode is put back even if the original throws.
        [PatchFinalizer]
        private static void Finalizer(Player __instance, float deltaTime, bool __state, Exception? __exception)
        {
            if (__state) AnimatorThrottle.ReleaseArms(__instance, deltaTime, __exception == null);
        }
    }

    private class MovementUpdatePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(MovementContext), nameof(MovementContext.ManualUpdate));

        [PatchPostfix]
        private static void Postfix(MovementContext __instance, float deltaTime)
        {
            try
            {
                AnimatorThrottle.AfterMovementUpdate(__instance, deltaTime);
            }
            catch (Exception ex)
            {
                Fail(ThrottleAnimators, ex);
            }
        }
    }

    private class RaidStartPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(GameWorld), nameof(GameWorld.OnGameStarted));

        [PatchPostfix]
        private static void Postfix()
        {
            AnimatorThrottle.OnRaidStart();
            HandsCulling.RestoreAll();
            DistantDetail.RestoreAll();
            Occlusion.OnRaidStart();
            Diagnostics.OnRaidStart();
        }
    }
}

// Read by name by BepInEx's ConfigurationManager (the F12 menu); no reference to it needed.
internal sealed class ConfigurationManagerAttributes
{
    public int? Order;
    public bool? IsAdvanced;
    // Shown instead of the key when set (Plugin.SetDisabled); the saved setting keeps its key.
    public string? DispName;
}
