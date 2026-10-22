using System.Collections.Generic;
using EFT;
using EFT.AssetsManager;
using UnityEngine;

namespace Smoothie;

// Bots far away (after scope zoom) stop drawing their small pieces: weapon attachments,
// pouches, patches. Each is a separate draw call, plus one per shadow cascade, and a group
// of bots is thousands of them. Laser beams and lights aren't renderers and keep working.
internal static class DistantDetail
{
    private sealed class Entry
    {
        public readonly HashSet<Renderer> Hidden = new(Diagnostics.ReferenceComparer.Instance);
        public bool Simplified;
        public float NextRefresh;
    }

    // Diagonal of a renderer's bounds below which it counts as a small piece. Inside the
    // weapon in hands only tiny parts (sights, mounts, rails, lasers, lights) qualify, so a
    // far bot's pistol never disappears.
    private const float SmallGear = 0.35f;
    private const float SmallWeaponPart = 0.15f;

    // ponytail: one entry per bot; cleared on raid end and on toggle-off.
    private static readonly Dictionary<Player, Entry> Entries = new(Diagnostics.ReferenceComparer.Instance);

    // Every pooled object above each hidden renderer, so nothing hidden ever goes back to
    // the pool (the next user, possibly you, would get invisible parts).
    private static readonly Dictionary<AssetPoolObject, HashSet<Renderer>> ByPool = new(Diagnostics.ReferenceComparer.Instance);

    private static readonly List<Renderer> Scratch = new();

    internal static int SimplifiedBots;
    internal static int HiddenRenderers;

    // Per frame per bot, from its LateUpdate.
    internal static void Update(Player player)
    {
        Entries.TryGetValue(player, out var entry);

        if (!Plugin.On(Plugin.SimplifyDistant) || player.HealthController?.IsAlive != true)
        {
            // Dead bots' gear has to be fully visible for looting.
            if (entry != null) Forget(player, entry);
            return;
        }

        AnimatorThrottle.NextFrame();
        if (!AnimatorThrottle.HasCamera) return;

        var position = player.Position;
        var apparent = Vector3.Distance(position, AnimatorThrottle.CameraPosition) / AnimatorThrottle.ZoomAt(position);
        var far = Plugin.SimplifyDistance.Value;

        if (entry == null || !entry.Simplified)
        {
            if (apparent < far) return;

            entry ??= new Entry();
            Entries[player] = entry;
            entry.Simplified = true;
            entry.NextRefresh = Time.unscaledTime + Random.Range(0.5f, 1.5f);
            Hide(entry, player);
            return;
        }

        // A little closer than the threshold before showing again, so a bot walking along
        // the edge doesn't flicker.
        if (apparent < far * 0.9f)
        {
            Forget(player, entry);
            return;
        }

        // Weapon swaps and the game's own culler can bring pieces back; catch them.
        if (Time.unscaledTime >= entry.NextRefresh)
        {
            entry.NextRefresh = Time.unscaledTime + 1f;
            Hide(entry, player);
        }
    }

    // The game's culler just set its renderers' state; hide our pieces again right away.
    internal static void AfterCullerChange(Player player)
    {
        if (!Entries.TryGetValue(player, out var entry)) return;

        // DisableCullingOnDead lands here: a corpse shows everything, for looting.
        if (player.HealthController?.IsAlive != true) Forget(player, entry);
        else if (entry.Simplified) Hide(entry, player);
    }

    // Every few seconds in raid: drop bots that died or despawned without us noticing.
    internal static void Sweep()
    {
        // Pooled objects destroyed outright (never returned to the pool).
        if (ByPool.Count > 0)
        {
            List<AssetPoolObject>? destroyed = null;
            foreach (var pool in ByPool.Keys)
            {
                if (pool == null) (destroyed ??= new List<AssetPoolObject>()).Add(pool!);
            }

            if (destroyed != null)
            {
                foreach (var pool in destroyed) ByPool.Remove(pool);
            }
        }

        if (Entries.Count == 0) return;
        List<Player>? gone = null;
        foreach (var pair in Entries)
        {
            if (pair.Key == null || pair.Key.HealthController?.IsAlive != true) (gone ??= new List<Player>()).Add(pair.Key!);
        }

        if (gone == null) return;
        foreach (var player in gone) Forget(player, Entries[player]);
    }

    private static void Hide(Entry entry, Player player)
    {
        var hands = player.HandsController != null ? player.HandsController.ControllerGameObject : null;
        var handsRoot = hands != null ? hands.transform : null;

        Scratch.AddRange(player.GetComponentsInChildren<Renderer>(true));
        foreach (var renderer in Scratch)
        {
            if (renderer == null || renderer.forceRenderingOff) continue;
            if (renderer is not MeshRenderer && renderer is not SkinnedMeshRenderer) continue;

            var limit = handsRoot != null && renderer.transform.IsChildOf(handsRoot) ? SmallWeaponPart : SmallGear;
            if (renderer.bounds.size.magnitude >= limit) continue;

            renderer.forceRenderingOff = true;
            if (!entry.Hidden.Add(renderer)) continue;

            for (var pool = renderer.GetComponentInParent<AssetPoolObject>(); pool != null; pool = Above(pool))
            {
                if (!ByPool.TryGetValue(pool, out var set)) ByPool[pool] = set = new HashSet<Renderer>(Diagnostics.ReferenceComparer.Instance);
                set.Add(renderer);
            }
        }

        Scratch.Clear();
        Count();
    }

    private static AssetPoolObject? Above(AssetPoolObject pool) =>
        pool.transform.parent != null ? pool.transform.parent.GetComponentInParent<AssetPoolObject>() : null;

    private static void Forget(Player player, Entry entry)
    {
        foreach (var renderer in entry.Hidden)
        {
            if (renderer == null) continue;
            renderer.forceRenderingOff = false;

            // Shown again: no longer anything to restore on a pool return. Without this, a
            // bot walking back and forth across the distance kept piling up entries.
            for (var pool = renderer.GetComponentInParent<AssetPoolObject>(); pool != null; pool = Above(pool))
            {
                if (ByPool.TryGetValue(pool, out var set) && set.Remove(renderer) && set.Count == 0) ByPool.Remove(pool);
            }
        }

        entry.Hidden.Clear();
        entry.Simplified = false;
        Entries.Remove(player);

        // Let the game's culler put back what it wants hidden (a culled bot's body).
        if (player != null && player.HealthController?.IsAlive == true && player is LocalPlayer local)
        {
            HandsCulling.Reapply(local);
        }

        Count();
    }

    internal static void OnPoolReturn(AssetPoolObject pool)
    {
        if (ByPool.Count == 0 || !ByPool.TryGetValue(pool, out var renderers)) return;
        ByPool.Remove(pool);

        foreach (var renderer in renderers)
        {
            if (renderer == null) continue;
            renderer.forceRenderingOff = false;
            foreach (var entry in Entries.Values) entry.Hidden.Remove(renderer);
        }

        Count();
    }

    internal static void RestoreAll()
    {
        foreach (var entry in Entries.Values)
        {
            foreach (var renderer in entry.Hidden)
            {
                if (renderer != null) renderer.forceRenderingOff = false;
            }
        }

        Entries.Clear();
        ByPool.Clear();
        Count();
    }

    private static void Count()
    {
        SimplifiedBots = Entries.Count;
        var hidden = 0;
        foreach (var entry in Entries.Values) hidden += entry.Hidden.Count;
        HiddenRenderers = hidden;
    }
}
