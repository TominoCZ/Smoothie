using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.AssetsManager;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Smoothie;

// The game's offline bot culler (OfflinePlayerCulling, also used for bots on a Fika client)
// switches off a culled bot's body and gear renderers but leaves the weapon in its hands
// drawing, shadows included. The online culler (ObserverPlayerCulling) hides the weapon
// too. This does the same offline.
internal static class HandsCulling
{
    private sealed class Hidden
    {
        public Player Player = null!;
        public GameObject Root = null!;

        // Only the renderers we switched off, so we never switch on one the game hid.
        public readonly List<Renderer> Renderers = new();

        // Pooled objects in and above the weapon (the weapon, its attachments).
        public readonly List<AssetPoolObject> Pools = new();
    }

    // ponytail: linear scans over one entry per culled bot (a few dozen at most).
    private static readonly List<Hidden> All = new();
    private static readonly List<Renderer> Scratch = new();
    private static readonly List<AssetPoolObject> PoolScratch = new();
    private static readonly Dictionary<AssetPoolObject, Hidden> ByPool = new();

    internal static void Enable()
    {
        new VisibleStatePatch().Enable();
        new PoolReturnPatch().Enable();
    }

    private static readonly AccessTools.FieldRef<LocalPlayer, OfflinePlayerCulling> Culler =
        AccessTools.FieldRefAccess<LocalPlayer, OfflinePlayerCulling>("botPlayerCulling");

    // Have the game's culler apply its state again (it hides a culled bot's renderers).
    internal static void Reapply(LocalPlayer player)
    {
        var culler = Culler(player);
        if (culler != null) culler.ApplyVisibleState();
    }

    internal static void RestoreAll()
    {
        for (var i = All.Count - 1; i >= 0; i--) Restore(i);
        ByPool.Clear();
    }

    // Every few seconds in raid: drop entries whose bot or weapon is gone (despawned without
    // the culler being told), so they can't hold a dead player in memory.
    internal static void Sweep()
    {
        for (var i = All.Count - 1; i >= 0; i--)
        {
            var entry = All[i];
            if (entry.Player == null || entry.Root == null || entry.Player.HealthController?.IsAlive != true) Restore(i);
        }
    }

    // Per frame from the bot's LateUpdate: a culled bot that swaps weapons gets the new
    // one hidden and the old one handed back.
    internal static void Check(Player player)
    {
        if (All.Count == 0) return;

        var index = IndexOf(player);
        if (index < 0) return;

        if (player.HealthController?.IsAlive != true) Restore(index);
        else if (All[index].Root != HandsObject(player)) Apply(player, hide: true);
    }

    private static void Apply(Player player, bool hide)
    {
        var hands = hide && Plugin.On(Plugin.HideCulledWeapons) ? HandsObject(player) : null;

        var index = IndexOf(player);
        if (index >= 0)
        {
            if (All[index].Root == hands) return;
            Restore(index);
        }

        if (hands == null) return;

        var entry = new Hidden { Player = player, Root = hands };
        hands.GetComponentsInChildren(true, Scratch);
        foreach (var renderer in Scratch)
        {
            if (renderer == null || renderer.forceRenderingOff) continue;
            renderer.forceRenderingOff = true;
            entry.Renderers.Add(renderer);
        }

        Scratch.Clear();

        hands.GetComponentsInChildren(true, PoolScratch);
        var parentPool = hands.GetComponentInParent<AssetPoolObject>();
        if (parentPool != null) PoolScratch.Add(parentPool);
        foreach (var pool in PoolScratch)
        {
            if (ByPool.ContainsKey(pool)) continue;
            ByPool[pool] = entry;
            entry.Pools.Add(pool);
        }

        PoolScratch.Clear();
        All.Add(entry);
    }

    private static GameObject? HandsObject(Player player)
    {
        var controller = player.HandsController;
        if (controller == null) return null;

        var go = controller.ControllerGameObject;
        return go != null ? go : null;
    }

    private static int IndexOf(Player player)
    {
        for (var i = 0; i < All.Count; i++)
        {
            if (ReferenceEquals(All[i].Player, player)) return i;
        }

        return -1;
    }

    private static void Restore(int index)
    {
        var entry = All[index];
        foreach (var renderer in entry.Renderers)
        {
            if (renderer != null) renderer.forceRenderingOff = false;
        }

        foreach (var pool in entry.Pools) ByPool.Remove(pool);
        All.RemoveAt(index);
    }

    // Runs whenever the culler's state flips, including DisableCullingOnDead when the bot
    // dies, so a corpse's weapon is always visible again.
    private class VisibleStatePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(OfflinePlayerCulling), nameof(OfflinePlayerCulling.ApplyVisibleState));

        [PatchPostfix]
        private static void Postfix(OfflinePlayerCulling __instance)
        {
            try
            {
                if (__instance._player != null) Apply(__instance._player, !__instance.IsVisible);
                if (__instance._player != null) DistantDetail.AfterCullerChange(__instance._player);
            }
            catch (System.Exception ex)
            {
                Plugin.Fail(Plugin.HideCulledWeapons, ex);
            }
        }
    }

    // Weapons are pooled. A hidden one must never go back to the pool, or the next player
    // to draw it (possibly you) would get an invisible gun. Matched by reference rather
    // than by hierarchy: WeaponPrefab unparents itself before it gets here, and shell
    // casings come through on every shot.
    private class PoolReturnPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(AssetPoolObject), nameof(AssetPoolObject.ReturnToPool), new System.Type[0]);

        [PatchPrefix]
        private static void Prefix(AssetPoolObject __instance)
        {
            try
            {
                DistantDetail.OnPoolReturn(__instance);
                if (ByPool.Count == 0 || !ByPool.TryGetValue(__instance, out var entry)) return;

                var index = All.IndexOf(entry);
                if (index >= 0) Restore(index);
            }
            catch (System.Exception ex)
            {
                Plugin.Fail(Plugin.HideCulledWeapons, ex);
            }
        }
    }
}
