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

[BepInPlugin("com.tominocz.smoothie", "Smoothie", "1.0.0")]
public class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log = null!;

    internal static ConfigEntry<bool> ThrottleAnimators = null!;
    internal static ConfigEntry<float> FullRateDistance = null!;
    internal static ConfigEntry<float> LowestRateDistance = null!;
    internal static ConfigEntry<float> LowestVisibleRate = null!;
    internal static ConfigEntry<float> OffscreenRate = null!;
    internal static ConfigEntry<float> FootstepRate = null!;
    internal static ConfigEntry<float> FootstepRange = null!;
    internal static ConfigEntry<bool> ShowStats = null!;
    internal static ConfigEntry<bool> HideCulledWeapons = null!;
    internal static ConfigEntry<bool> SkipHiddenIk = null!;
    internal static ConfigEntry<bool> SimplifyDistant = null!;
    internal static ConfigEntry<float> SimplifyDistance = null!;
    internal static ConfigEntry<float> CorpseSettleTime = null!;
    internal static ConfigEntry<bool> HotPathFixes = null!;
    internal static ConfigEntry<bool> StaggerSpawns = null!;
    internal static ConfigEntry<bool> KeepBackendConnection = null!;
    internal static ConfigEntry<bool> ProfileScripts = null!;
    internal static ConfigEntry<bool> LogDrops = null!;
    internal static ConfigEntry<bool> KeepGcOff = null!;

    private static ISender? _vanillaSender;
    private static BackendSender? _sender;

    private void Awake()
    {
        Log = Logger;

        const string anim = "1. Bot animation";
        ThrottleAnimators = Config.Bind(anim, "Throttle bot animators", true,
            "Animate far-away and off-screen bots at a lower rate. Bots that are close, in a fight, or within footstep hearing range keep animating smoothly.");
        FullRateDistance = Config.Bind(anim, "Full-rate distance", 40f, new ConfigDescription(
            "On-screen bots closer than this animate every frame. Scope zoom counts: through a 4x scope, a bot at 160 m counts as 40 m.",
            new AcceptableValueRange<float>(10f, 300f)));
        LowestRateDistance = Config.Bind(anim, "Lowest-rate distance", 200f, new ConfigDescription(
            "On-screen bots this far away (after scope zoom) animate at the lowest on-screen rate. In between, the rate falls off gradually.",
            new AcceptableValueRange<float>(20f, 1000f)));
        LowestVisibleRate = Config.Bind(anim, "Lowest on-screen rate", 15f, new ConfigDescription(
            "Animation updates per second for the most distant bots you can see.",
            new AcceptableValueRange<float>(5f, 60f)));
        OffscreenRate = Config.Bind(anim, "Off-screen rate", 10f, new ConfigDescription(
            "Animation updates per second for bots outside your view and out of footstep range.",
            new AcceptableValueRange<float>(5f, 60f)));
        FootstepRate = Config.Bind(anim, "Footstep rate", 30f, new ConfigDescription(
            "Minimum animation updates per second for bots you could hear walking. Footstep sounds are timed off the animation, so this keeps them on the beat.",
            new AcceptableValueRange<float>(10f, 60f)));
        FootstepRange = Config.Bind(anim, "Footstep range", 60f, new ConfigDescription(
            "Distance within which bots are treated as audible. The mod also learns the loudest real footstep range during the raid and uses that if it is larger.",
            new AcceptableValueRange<float>(0f, 200f)));
        HideCulledWeapons = Config.Bind("2. Culling", "Hide culled bot weapons", true,
            "When the game culls a bot it can't see, also hide the weapon in its hands (and its shadow). The game already does this online; offline it only hid the body.");
        HideCulledWeapons.SettingChanged += (_, _) =>
        {
            if (!HideCulledWeapons.Value) HandsCulling.RestoreAll();
        };

        SkipHiddenIk = Config.Bind("2. Culling", "Skip IK for hidden bots", true,
            "Bots near you that are behind you, or completely behind walls (no line from your eyes reaches their head, body, hands, legs or gun muzzle), skip the hand/head IK and animate like off-screen bots. Rendering, shadows and sounds are unchanged. Glass, fences, grates and grass don't count as walls.");

        SimplifyDistant = Config.Bind("2. Culling", "Simplify distant bots", true,
            "Far-away bots stop drawing their small pieces (weapon attachments, pouches, patches): fewer draw calls when looking at groups. Laser beams and lights are unaffected. Everything comes back when they're closer, and dead bots always show everything.");
        SimplifyDistant.SettingChanged += (_, _) =>
        {
            if (!SimplifyDistant.Value) DistantDetail.RestoreAll();
        };
        SimplifyDistance = Config.Bind("2. Culling", "Simplify distance", 60f, new ConfigDescription(
            "Distance beyond which bots are simplified. Scope zoom counts: through a 4x scope a bot at 240 m counts as 60 m.",
            new AcceptableValueRange<float>(20f, 500f)));

        CorpseSettleTime = Config.Bind("3. Hot paths", "Corpse settle time", 5f, new ConfigDescription(
            "Seconds after which a falling corpse is treated as settled and its ragdoll stops simulating. Vanilla: 15 s (30 s for Fika's network corpses). Cuts physics cost in big fights.",
            new AcceptableValueRange<float>(2f, 30f)));

        HotPathFixes = Config.Bind("3. Hot paths", "Hot-path fixes", true,
            "Removes per-frame waste in vanilla code: a coroutine started for every bot every frame, dead bots' brains kept in the AI update list, and muzzle-smoke lighting recomputed once per gun per frame.");

        StaggerSpawns = Config.Bind("3. Hot paths", "Stagger bot spawns", true,
            "When bots run on this PC (hosting or solo): build at most one new bot per frame, and switch at most one new bot's AI on per frame, instead of a whole group at once. Spreads the spawn freeze over several shorter frames.");

        KeepBackendConnection = Config.Bind("4. Loading", "Keep backend connection open", true,
            "Reuse one connection to the SPT server instead of opening a new one (with a full TLS handshake) for every request. Mostly helps the menu load when the server is on another machine. Applies to the next request.");
        KeepBackendConnection.SettingChanged += (_, _) => ApplySender();

        KeepGcOff = Config.Bind("4. Loading", "Keep GC off in raid", true,
            "The game switches the garbage collector off for a raid so it can't freeze the game mid-fight. SPT switches it on with every inventory open to free memory. Let that cleanup finish, then switch it off again; also clean up once 4 GB of garbage has piled up. Uses the game's own switch, which still requires the game's minimum RAM.");

        ShowStats = Config.Bind("5. Diagnostics", "Show stats", false,
            "Overlay with frame timings, how many bots are throttled or hidden, and (with \"Profile scripts\" on) the most expensive scripts.");
        ProfileScripts = Config.Bind("5. Diagnostics", "Profile scripts", false,
            "Time every running script's Update/LateUpdate/FixedUpdate (the game's and every mod's), grouped by type, and every stage of Unity's frame (animation, physics, rendering, ...). Writes a snapshot to the log every 2 minutes in raid. Also logs each slow step of a bot spawn. Costs frame time while on, so frame rates measured with it on are lower than real. For tracking down what eats the CPU, not for normal play.");
        ProfileScripts.SettingChanged += (_, _) =>
        {
            if (ProfileScripts.Value) Diagnostics.StartProfiling();
            else Diagnostics.StopProfiling();
        };
        LogDrops = Config.Bind("5. Diagnostics", "Log frame rate drops", false,
            "When the frame rate falls well below its recent average and stays there, write a snapshot (timings, memory, and the script breakdown if profiling) to the BepInEx log.");

        // So a toggle mid-raid can be lined up with the frame times around it.
        Config.SettingChanged += (_, e) =>
            Log.LogInfo($"{DateTime.Now:HH:mm:ss.fff} setting changed: {e.ChangedSetting.Definition.Key} = {e.ChangedSetting.BoxedValue}");

        // Each feature on its own: one that can't start (a game or Fika update moved
        // something) is logged and skipped instead of taking the whole plugin down.
        Safely("backend connection", ApplySender);
        Safely("animation throttling", () =>
        {
            new PlayerLateUpdatePatch().Enable();
            new ArmsUpdatePatch().Enable();
        });

        Safely("raid start", () => new RaidStartPatch().Enable());
        Safely("weapon culling", HandsCulling.Enable);
        Safely("hot-path fixes", HotPaths.Enable);
        Safely("bot spawns", BotSpawns.Enable);
        Safely("hidden-bot IK", Occlusion.Enable);
        Safely("Fika hidden-bot IK", () =>
        {
            if (Occlusion.ObservedIkVisibilityPatch.Target() != null) new Occlusion.ObservedIkVisibilityPatch().Enable();
        });
        Safely("diagnostics", () =>
        {
            Diagnostics.Start();
            if (ProfileScripts.Value) Diagnostics.StartProfiling();
        });
        Safely("forced-GC watch", () => Diagnostics.WatchForcedCollections(new Harmony("com.tominocz.smoothie.gcwatch")));
    }

    // Our code runs inside game methods called every frame. If it throws, switch that one
    // feature off for the rest of the session and let the game carry on as vanilla, rather
    // than breaking the method. Not by changing the setting: BepInEx would save that, and a
    // fixed version would then start with the feature off.
    private static readonly HashSet<ConfigEntryBase> Broken = new();

    internal static bool On(ConfigEntry<bool> feature) => feature.Value && !Broken.Contains(feature);

    internal static void Fail(ConfigEntryBase feature, Exception ex)
    {
        if (!Broken.Add(feature)) return;
        Log.LogError($"\"{feature.Definition.Key}\" switched off for this session after an error: {ex}");
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
        if (KeepBackendConnection.Value)
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
            }

            _wasInRaid = inRaid;
            if (inRaid && Time.unscaledTime - _lastSweep > 5f)
            {
                _lastSweep = Time.unscaledTime;
                HandsCulling.Sweep();
                DistantDetail.Sweep();
            }

            Diagnostics.Tick();
        }
        catch (Exception ex)
        {
            Diagnostics.StopProfiling();
            Fail(LogDrops, ex);
        }
    }

    private string _overlay = "";
    private float _overlayAt;

    private void OnGUI()
    {
        if (!ShowStats.Value) return;

        // Rebuilt twice a second; formatting it every frame would show up in the profile.
        if (Time.unscaledTime - _overlayAt > 0.5f)
        {
            _overlayAt = Time.unscaledTime;
            _overlay = $"Smoothie: {AnimatorThrottle.Throttled} bots throttled, {AnimatorThrottle.Steps} steps " +
                       $"({AnimatorThrottle.StepMs:0.00} ms), {Occlusion.HiddenCount} close bots hidden, " +
                       $"footstep range {AnimatorThrottle.AudibleRange:0} m\n" + Diagnostics.Snapshot(12);
        }

        GUI.Box(new Rect(10, 10, 900, 330), "");
        GUI.Label(new Rect(16, 14, 890, 320), _overlay);
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
