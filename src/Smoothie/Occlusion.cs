using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.Ballistics;
using HarmonyLib;
using SPTushonka.Reflection.Patching;
using UnityEngine;

namespace Smoothie;

// The game's bot culler counts every bot within 30 m as visible, walls or not, and runs
// full IK (hands on the gun, head and weapon aim) for every visible bot within 40 m. Up
// close (Factory) that is most of the bots, most of the time. This works out which of them
// you actually can't see: outside the view, or with every line from the camera to the head,
// chest, hands, shoulders, hips, legs and gun muzzle blocked by walls. Those skip the IK and
// animate like off-screen bots. Rendering is left alone, so shadows still show. Lines are
// cast a few times a second per bot (see Sight), never for bots outside the view.
internal static class Occlusion
{
    private sealed class State
    {
        public readonly Sight Sight;
        public int Frame = -1;
        public bool Hidden;

        public State(Player player) => Sight = new Sight(() => AnyLineClear(player), Random.Range(0.8f, 1.2f));
    }

    // See-through hits passed before giving up on a line.
    private const int MaxHops = 4;

    // IL2CPP: by the player's wrapper, held strongly (see AnimatorThrottle.States); cleared at raid start and end.
    private static readonly Dictionary<Player, State> States = new(Diagnostics.ReferenceComparer.Instance);

    // What bots use for line of sight, minus grass and foliage, which you can see through.
    internal static readonly int SightMask =
        (1 << LayerMask.NameToLayer("Terrain")) | (1 << LayerMask.NameToLayer("HighPolyCollider"));

    // Colliders on the sight layers that don't block the view.
    private static readonly Dictionary<Collider, bool> SeeThroughCache = new(Diagnostics.ReferenceComparer.Instance);
    private static readonly Vector3[] Points = new Vector3[10];

    internal static int HiddenCount;
    private static int _hiddenCount;
    private static int _countFrame = -1;

    // Own cost, last full frame: for the profiler snapshot.
    internal static int Rays;
    internal static double Ms;
    private static int _rays;
    private static long _ticks;

    internal static void Enable() => new IkVisibilityPatch().Enable();

    internal static void OnRaidStart()
    {
        SeeThroughCache.Clear();
        States.Clear();
    }

    // Every few seconds, with AnimatorThrottle.Prune: dead or destroyed bots are dropped.
    internal static void Prune()
    {
        foreach (var player in States.Keys.Where(p => p == null || p.HealthController?.IsAlive != true).ToList()) States.Remove(player);
    }

    // Replaces Player.IsVisible in the game's "run IK this frame?" test.
    public static bool VisibleForIk(Player player)
    {
        if (!player.IsVisible) return false;
        try
        {
            return !IsHidden(player);
        }
        catch (System.Exception ex)
        {
            Plugin.Fail(Plugin.SkipHiddenIk, ex);
            return true;
        }
    }

    internal static bool IsHidden(Player player)
    {
        if (!Plugin.On(Plugin.SkipHiddenIk) || player.IsYourPlayer) return false;

        AnimatorThrottle.NextFrame();
        if (!AnimatorThrottle.HasCamera || !AnimatorThrottle.IsBot(player)) return false;
        if (player.HealthController?.IsAlive != true) return false;

        if (!States.TryGetValue(player, out var state)) States[player] = state = new State(player);
        if (state.Frame == Time.frameCount) return state.Hidden;
        state.Frame = Time.frameCount;

        if (_countFrame != Time.frameCount)
        {
            _countFrame = Time.frameCount;
            HiddenCount = _hiddenCount;
            _hiddenCount = 0;
            Rays = _rays;
            Ms = _ticks * Diagnostics.TickMs;
            _rays = 0;
            _ticks = 0;
        }

        // Hosting: another player near this bot sees its gun, and its shots start from it.
        if (AnimatorThrottle.OtherPlayerDistance(player) <= EFTHardSettings.Instance.CULL_GROUNDER)
        {
            state.Hidden = false;
            return false;
        }

        // Past the IK distance nothing here changes anything, so no rays.
        var position = player.Position;
        if (Vector3.Distance(position, AnimatorThrottle.CameraPosition) > EFTHardSettings.Instance.CULL_GROUNDER)
        {
            state.Hidden = false;
            return false;
        }

        var start = Diagnostics.Measuring ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        state.Hidden = state.Sight.Hidden(Time.time, AnimatorThrottle.InView(position, out _));
        if (start != 0) _ticks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
        if (state.Hidden) _hiddenCount++;
        return state.Hidden;
    }

    private static bool AnyLineClear(Player player)
    {
        var bones = player.PlayerBones;
        if (bones == null) return true;

        var count = 0;
        // Most likely to be seen first, so a visible bot usually costs one line.
        if (bones.Head != null) Points[count++] = bones.Head.position;
        if (bones.Ribcage != null) Points[count++] = bones.Ribcage.position;
        if (player.HandsController is Player.FirearmController firearm && firearm.CurrentFireport != null)
        {
            Points[count++] = firearm.CurrentFireport.position;
        }

        if (bones.RightPalm != null) Points[count++] = bones.RightPalm.position;
        if (bones.LeftPalm != null) Points[count++] = bones.LeftPalm.position;
        if (bones.LeftShoulder != null) Points[count++] = bones.LeftShoulder.position;
        if (bones.RightShoulder != null) Points[count++] = bones.RightShoulder.position;
        if (bones.Pelvis != null) Points[count++] = bones.Pelvis.position;
        if (bones.LeftThigh2 != null) Points[count++] = bones.LeftThigh2.position;
        if (bones.RightThigh2 != null) Points[count++] = bones.RightThigh2.position;

        // Nothing to test against: don't claim it's hidden.
        if (count == 0) return true;

        var from = AnimatorThrottle.CameraPosition;
        for (var i = 0; i < count; i++)
        {
            if (LineClear(from, Points[i])) return true;
        }

        return false;
    }

    private static bool LineClear(Vector3 from, Vector3 to)
    {
        for (var hop = 0; hop < MaxHops; hop++)
        {
            _rays++;
            if (!Physics.Linecast(from, to, out var hit, SightMask, QueryTriggerInteraction.Ignore)) return true;
            if (!SeeThrough(hit.collider)) return false;

            // Step just past the glass (or grate, or body) and keep going.
            from = hit.point + (to - from).normalized * 0.02f;
        }

        return false;
    }

    internal static bool SeeThrough(Collider collider)
    {
        if (SeeThroughCache.TryGetValue(collider, out var cached)) return cached;

        var ballistic = collider.GetComponent<BallisticCollider>();
        var seeThrough = ballistic != null && ballistic.TypeOfMaterial switch
        {
            MaterialType.Glass or MaterialType.GlassShattered or MaterialType.GlassVisor
                or MaterialType.Chainfence or MaterialType.Grate
                or MaterialType.GrassHigh or MaterialType.GrassLow
                // Another player in the way hides only part of a bot.
                or MaterialType.Body or MaterialType.BodyArmor or MaterialType.Helmet => true,
            _ => false,
        };

        // Bots' colliders come and go all raid; a full cache starts over instead of growing.
        if (SeeThroughCache.Count >= 4096) SeeThroughCache.Clear();
        SeeThroughCache[collider] = seeThrough;
        return seeThrough;
    }

    // Player.VisualPass decides whether to run IK with
    // "IsVisible && distance <= CULL_GROUNDER". IL2CPP has no IL to swap the IsVisible read in,
    // so for a hidden bot the distance limit is set below zero for just this call: same result,
    // and nothing else reads CULL_GROUNDER (only VisualPass and Fika-style observed views).
    // Nothing else in the game (muzzle effects, AI, sounds) sees a different visibility.
    private class IkVisibilityPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Player), nameof(Player.VisualPass));

        [PatchPrefix]
        private static void Prefix(Player __instance, out float __state)
        {
            __state = float.NaN;
            if (VisibleForIk(__instance) || !__instance.IsVisible) return;
            var settings = EFTHardSettings.Instance;
            __state = settings.CULL_GROUNDER;
            settings.CULL_GROUNDER = -1f;
        }

        // A finalizer so the limit is put back even if the original throws.
        [PatchFinalizer]
        private static void Finalizer(float __state)
        {
            if (!float.IsNaN(__state)) EFTHardSettings.Instance.CULL_GROUNDER = __state;
        }
    }

    // Whether the player's IK runs in Fika's ObservedVisualPass instead of Player.VisualPass,
    // where Smoothie's hidden-bot check doesn't apply.
    private static System.Type? _observedType;
    private static bool _observedLooked;

    internal static bool OwnIkPass(Player player)
    {
        if (!_observedLooked)
        {
            _observedLooked = true;
            _observedType = ObservedVisualPass()?.DeclaringType;
        }

        return _observedType != null && _observedType.IsInstanceOfType(player);
    }

    // Fika's ObservedVisualPass, when Fika is installed (found by name, no reference). Only the
    // cost overlay times it; Smoothie doesn't change it.
    internal static MethodBase? ObservedVisualPass() =>
        Plugin.FindType("Fika.Core.Main.Players.ObservedPlayer") is { } type && typeof(Player).IsAssignableFrom(type)
            ? AccessTools.DeclaredMethod(type, "ObservedVisualPass") is { IsStatic: false } method ? method : null
            : null;

    // Disabled: skipping IK for hidden bots on a Fika client meant changing Fika's own code,
    // and fixing another mod isn't Smoothie's place.
    // internal class ObservedIkVisibilityPatch : ModulePatch
    // {
    //     protected override MethodBase GetTargetMethod() => ObservedVisualPass()!;
    //
    //     [PatchTranspiler]
    //     private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code, MethodBase __originalMethod) =>
    //         IkVisibilityPatch.SwapIsVisible(code, __originalMethod);
    // }
}
