using System;
using System.Collections.Generic;
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

[BepInPlugin("com.tominocz.smoothie", "Smoothie", "1.0.1")]
// Optional: when Fika is installed it has to load first, or its types aren't there yet when
// Smoothie looks for them and its observed bots go unpatched. Without Fika this does nothing.
[BepInDependency("com.fika.core", BepInDependency.DependencyFlags.SoftDependency)]
public class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log = null!;

    internal static ConfigEntry<bool> ThrottleAnimators = null!;
    internal static ConfigEntry<bool> UnityStepping = null!;
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
            "Only matters when the bots run on your PC (hosting, or solo). With high bot counts (Horde mode) the game tries to spawn hundreds of bots at once every 15 seconds, although only a few fit in, and asks the server for a bot profile for each one. That causes heavy stutters and huge memory use. With this on it only tries as many as can actually spawn. The number of bots in the raid stays the same. Switching it off mid-raid lets a flood start within seconds, and it lasts the rest of the raid.", 100);
        SpreadProfiles = Bind(SpawnsSection, "Load new bots a bit at a time", true,
            "Only matters when the bots run on your PC. Every new bot's gear is built from scratch, and doing many at once freezes the game. With this on, only a little is built per frame: no freeze, and new bots appear a moment later. Also, with the frame rate limit switched off, the game's background work queue (where much of a new bot's setup runs) gets a budget from your display's refresh rate instead of the menu's limit, so a spawn wave no longer drags the frame rate down to the menu's 60.", 90);
        ProfilesOffThread = Bind(SpawnsSection, "Load new bots on another CPU core (experimental)", false,
            "With \"Load new bots a bit at a time\" on: build the bots' gear on another CPU core instead. Skipped automatically if another mod changes how gear is built. If anything goes wrong it switches itself off for the session and logs why.", 80, true);

        const string other = "3. Other fixes";
        CorpseSettleTime = Bind(other, "Corpses stop moving after (s)", 5f,
            "Dead bodies keep simulating physics until they settle, up to 15 s (30 s in Fika). Ending it sooner saves CPU in big fights; bodies have almost always landed by then.", 100, false,
            new AcceptableValueRange<float>(2f, 30f));
        HotPathFixes = Bind(other, "Remove small CPU waste in game code", true,
            "Fixes spots where the game or a mod wastes CPU or memory: the main menu's video background is paused while it's hidden (it kept playing through raids, costing 2-3 CPU cores), and bots the game tried to spawn no longer stay in memory until the game is closed. Nothing you can see changes.", 90);
        NoMuzzleShadows = Bind(other, "Muzzle flashes of others cast no shadows", true,
            "Each shot lights up the area around the gun, and if that light casts shadows the game redraws everything nearby up to 6 times. In a big fight that more than doubles the shadow work. With this on, other players' and bots' muzzle flashes still light things up, just without shadows. Your own gun is left alone.", 85);
        KeepCleanupPaused = Bind(other, "Prevent memory cleanup stutters in raids", true,
            "On PCs with enough RAM the game pauses memory cleanup during a raid, so it can't stutter in a fight. Opening your inventory (an SPT feature) runs a cleanup and leaves cleanup on for the rest of the raid, which then stutters every minute or two. With this on, that cleanup still runs, then cleanup is paused again; if 4 GB of garbage piles up, one more runs. Only in raids, and only on PCs the game itself pauses cleanup on (the server's RAM limit, 25 GB by default): below that, cleanup stays on as the game wants. If something else switches it back on right after every pause (another mod), it's left on for the rest of that raid.", 75);
        KeepBackendConnection = Bind(other, "Keep server connection open (faster menus)", true,
            "Keeps one connection to the SPT server open instead of reconnecting for every request. Speeds up menus, most of all when the server runs on another PC.", 80);

        // All of it for tracking down problems, so hidden unless "Show advanced settings" is ticked.
        PauseFixes = Bind(OverlaySection, "Turn off all fixes (A/B test)", false,
            "Runs the game as if Smoothie weren't installed, without touching any of your settings above: switch it on and off and compare with the overlay. The overlay and logs keep working. Switched on before a raid, that raid runs fully without Smoothie, bot spawning fixes included. Switched on during a raid, the bot spawning fixes stay on: without them one moment in a Horde raid can ruin the rest of it. Always starts off when the game starts.", 110, true);
        // Never left on by accident: the mod would look installed but do nothing.
        PauseFixes.Value = false;
        ShowCosts = Bind(OverlaySection, "Show performance overlay", false,
            "Shows what each frame is spent on (bot animation, AI, scripts, rendering, ...) with a graph for each. Toggle a setting and watch its line change; yellow marks a setting change, red a memory cleanup. Costs a little performance while shown.", 100, true);
        ShowCosts.SettingChanged += (_, _) => Overlay.Apply();
        LogDrops = Bind(OverlaySection, "Log stutters and their causes", false,
            "Writes a line to the BepInEx log for every stutter, with what caused it, and a snapshot whenever the frame rate drops and stays down. Useful for bug reports.", 90, true);
        ShowStats = Bind(OverlaySection, "Show text stats panel", false,
            "A text panel with frame timings, memory and bot counts.", 80, true);
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
            new ArmsUpdatePatch().Enable();
            new MovementUpdatePatch().Enable();
        });

        Safely("raid start", () => new RaidStartPatch().Enable());
        Safely("weapon culling", HandsCulling.Enable);
        Safely("hot-path fixes", HotPaths.Enable);
        Safely("hidden-bot IK", Occlusion.Enable);
        Safely("bot profile budget", ProfileBudget.Enable);
        Safely("Fika hidden-bot IK", () =>
        {
            if (Occlusion.ObservedIkVisibilityPatch.Target() != null) new Occlusion.ObservedIkVisibilityPatch().Enable();
        });
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

    // Every feature checks this. "Turn off all fixes" is a second gate on top of each
    // feature's own setting, so it switches the mod off without changing any of them;
    // the overlay and logs aren't fixes and keep working.
    internal static bool On(ConfigEntry<bool> feature) =>
        feature.Value && !Broken.Contains(feature) && (Fixing || IgnoresPause(feature));

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

    internal static void Fail(ConfigEntryBase feature, Exception ex)
    {
        if (!Broken.Add(feature)) return;
        Log.LogError($"\"{feature.Definition.Key}\" switched off for this session after an error: {ex}");
        Safely("undoing it", ApplyFixes);
    }

    // Puts back what a fix changed in the world once it's off, whichever way it went off:
    // its setting, the A/B switch or an error. Run on every setting change.
    private static void ApplyFixes()
    {
        if (!On(SimplifyDistant)) DistantDetail.RestoreAll();
        if (!On(HideCulledWeapons)) HandsCulling.RestoreAll();
        HotPaths.ResyncMenuVideos();
        ApplySender();
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
    private float _lastSweep;

    // Player types that replace Player.LateUpdate without calling it (Fika's observed bots
    // on a client), so the patch on it never runs for them. They're driven from here, once
    // per frame, without touching their code, so no Fika update can break it. Before or
    // after their own LateUpdate doesn't matter: a bot is only throttled where the game
    // skips its IK, and a curve read a frame late is a footstep a frame late.
    private static readonly Dictionary<Type, bool> OwnLateUpdate = new();

    private void LateUpdate()
    {
        try
        {
            if (!Diagnostics.InRaid) return;
            var players = Singleton<GameWorld>.Instance.AllAlivePlayersList;
            for (var i = 0; i < players.Count; i++)
            {
                // Switched off (AILimit disables far bots on the host, and Fika then switches
                // them off on clients too): Unity skips their LateUpdate, so do we.
                var player = players[i];
                if (player == null || !player.isActiveAndEnabled) continue;

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
            // Leaving a raid: drop everything that refers to its players, so none of it is
            // kept alive through the menu.
            var inRaid = Diagnostics.InRaid;
            if (_wasInRaid && !inRaid)
            {
                HandsCulling.RestoreAll();
                DistantDetail.RestoreAll();
                Occlusion.OnRaidStart();
                Diagnostics.OnRaidEnd();
            }

            _wasInRaid = inRaid;
            if (!inRaid) _pausedBeforeRaid = PauseFixes.Value;
            if (inRaid && Time.unscaledTime - _lastSweep > 5f)
            {
                _lastSweep = Time.unscaledTime;
                Guard(HideCulledWeapons, HandsCulling.Sweep);
                Guard(SimplifyDistant, DistantDetail.Sweep);
                Guard(HotPathFixes, HotPaths.ResyncMenuVideos);
            }

            Guard(KeepCleanupPaused, Diagnostics.KeepCleanupPaused);
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
            OwnTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
        }
    }

    private void DrawOverlays()
    {
        var top = 10f;
        if (On(ShowCosts)) top = Overlay.Draw(10, top) + 6;
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
}
