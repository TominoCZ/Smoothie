using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using EFT.CameraControl;
using HarmonyLib;
using Koenigz.PerfectCulling;
using Koenigz.PerfectCulling.EFT;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Smoothie;

// Takes LOD groups nothing can be seen of out of Unity's per-frame LOD work (Plugin.SkipOutOfView).
// Unity's LOD pass recomputes every LOD group on the map (~134k on Streets) for every camera each
// frame, wherever it is and whether anything of it is drawn; forced to a level it doesn't have
// (SceneSkip), a group draws nothing and that pass returns from it at once. A byte each way, so
// nothing about it costs much (switching objects off and on with enabled cost ~1 µs each and
// hitched on every turn). Measured on Streets standing still: ~95k groups, ~0.7-1 ms a frame.
// Two reasons a group has nothing to show:
// - Out of view and beyond shadow range: every frame right before the main camera culls, cells
//   of groups are tested against that frame's own view, and a cell coming into view is put back
//   before Unity culls it, so nothing shows up late. Within shadow range everything stays:
//   what's behind you can cast shadows into view.
// - All its meshes switched off by the game's baked visibility: put back in the game's switch
//   queue right before it switches any of them back on (SwitchPatch), so never a frame without.
// - Too far to draw (Plugin.SkipTooFar): past the distance where its last level culls, Unity
//   works out every frame that it draws nothing. Its cull distance is half its world size over
//   the last level's screen height, times LOD bias / tan(fov / 2), Unity's own formula. Taken out
//   once the nearest point of its cell is 35% past it, put back within 25%, before Unity culls.
//   On Streets ~90% of the groups in view and in play (~72k). A scope's camera has a far narrower
//   view, so its LOD reaches much further: while one renders this pauses, and the groups in its
//   view are put back in its own pre-cull, before it culls.
// Only static level groups (nothing that moves or can be used, no players or loot).
internal static class LodCulling
{
    private const float CellSize = 32f;
    private const float KeepBeyondShadows = 50f;
    // Put back once within this of the view (m); taken out once past it by this plus a share of
    // its distance, so a cell at the edge doesn't flip every frame.
    private const float ShowMargin = 2f, HideMargin = 6f, HideShare = 0.1f;
    private const int TakePerFrame = 3000, ChecksPerFrame = 2000;
    private const byte OutOfView = 1, GameOff = 2, TooFar = 4;
    private const float FarTake = 1.35f, FarKeep = 1.25f;
    private const int FarPerFrame = 5000;

    // Per group, sorted by cell: the group, its bounding sphere, why it's forced (0: it isn't),
    // and the slot and native object it was forced through (0: not forced).
    private static LODGroup[] _groups = Array.Empty<LODGroup>();
    private static byte[] _reason = Array.Empty<byte>();
    private static uint[] _slot = Array.Empty<uint>();
    private static IntPtr[] _native = Array.Empty<IntPtr>();
    // Per group whose meshes are all the baked visibility's: its meshes (else null).
    private static Renderer[]?[] _bakedParts = Array.Empty<Renderer[]?>();
    private static readonly Dictionary<Renderer, int> GroupOf = new();
    // Cells: groups CellStart[c] to CellStart[c + 1] - 1, a sphere around all of them, out now.
    private static int[] _cellStart = Array.Empty<int>();
    private static Vector3[] _cellCenter = Array.Empty<Vector3>();
    private static float[] _cellRadius = Array.Empty<float>();
    private static bool[] _cellOut = Array.Empty<bool>();
    // Groups whose meshes the game may just have switched all off: checked on the main thread.
    private static readonly List<int> ToCheck = new();
    // Per group: its cull distance for LOD bias / tan(fov / 2) = 1 (infinity: never culled). Per
    // cell: its groups by that distance, nearest first, and how many of them are out as too far.
    private static float[] _farAt = Array.Empty<float>();
    private static int[] _byFar = Array.Empty<int>();
    private static int[] _farTaken = Array.Empty<int>();
    private static float _farResumeAt;
#if DEBUG
    // Check: groups taken out as too far while Unity still drew one of their meshes (must stay 0).
    private static Renderer[][] _parts = Array.Empty<Renderer[]>();
    internal static int FarWhileDrawn;
#endif
    private static int _checkCursor;

    private static IEnumerator<bool>? _build;
    private static bool _built;
    private static float _buildAt = -1f;
    private static int _mainThread;
    private static Camera? _camera;
    private static Vector4 _side0, _side1, _side2, _side3;

    internal static int Groups, Forced;
    internal static double Ms;

    internal static void Enable()
    {
        _mainThread = Thread.CurrentThread.ManagedThreadId;
        Camera.onPreCull += PreCull;
        new SwitchPatch().Enable();
    }

    private static bool On => Plugin.On(Plugin.SkipOutOfView) && SceneSkip.Check();

    // Plugin.Update in a raid: the build, a slice per frame, 10 s in.
    internal static void Tick()
    {
        if (!On)
        {
            if (Forced > 0) ShowAll();
            return;
        }

        if (_built) return;
        if (_buildAt < 0f) _buildAt = Time.time + 10f;
        if (Time.time < _buildAt) return;
        _build ??= Build();
        if (!_build.MoveNext())
        {
            _build = null;
            _built = true;
        }
    }

    // The main camera, right before culling: cells in and out of view, then the groups the game
    // may have switched all off.
    private static void PreCull(Camera camera)
    {
        if (!_built || !Diagnostics.InRaid) return;
        if (!ReferenceEquals(camera, _camera))
        {
            if (ReferenceEquals(camera, CameraManager.Instance?.OpticCameraManager?.Camera))
            {
                ScopeFar(camera);
                return;
            }

            if (CameraManager.Instance?.Camera is not { } main || !ReferenceEquals(camera, main)) return;
            _camera = camera;
        }

        if (!On) return;
        var start = Diagnostics.Measuring ? Stopwatch.GetTimestamp() : 0;
        try
        {
            TestCells(camera);
            CheckGameOff();
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"LOD culling: {ex}");
            Plugin.SkipOutOfView.Value = false;
        }

        if (start != 0) Ms = Ms * 0.95 + (Stopwatch.GetTimestamp() - start) * Diagnostics.TickMs * 0.05;
    }

    private static void TestCells(Camera camera)
    {
        var m = camera.cullingMatrix;
        _side0 = Normalized(new Vector4(m.m30 + m.m00, m.m31 + m.m01, m.m32 + m.m02, m.m33 + m.m03));
        _side1 = Normalized(new Vector4(m.m30 - m.m00, m.m31 - m.m01, m.m32 - m.m02, m.m33 - m.m03));
        _side2 = Normalized(new Vector4(m.m30 + m.m10, m.m31 + m.m11, m.m32 + m.m12, m.m33 + m.m13));
        _side3 = Normalized(new Vector4(m.m30 - m.m10, m.m31 - m.m11, m.m32 - m.m12, m.m33 - m.m13));
        var eye = camera.transform.position;
        var keep = QualitySettings.shadowDistance + KeepBeyondShadows;
        var budget = TakePerFrame;
        // While a scope renders: no new groups taken out as too far, the rest put back a share per frame.
        if (CameraManager.Instance?.OpticCameraManager is { IsAnyOpticCameraRendering: true }) _farResumeAt = Time.unscaledTime + 1f;
        var far = Plugin.On(Plugin.SkipTooFar) && Time.unscaledTime >= _farResumeAt;
        var farScale = QualitySettings.lodBias / Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
        var farBudget = FarPerFrame;
        for (var c = 0; c < _cellOut.Length; c++)
        {
            var center = _cellCenter[c];
            var radius = _cellRadius[c];
            float dx = center.x - eye.x, dy = center.y - eye.y, dz = center.z - eye.z;
            var distance = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            var near = distance - radius;
            // Out of view and staying so: its groups are left out anyway, so their too-far state
            // waits until the cell comes back (FarCell then puts back what's due, unbudgeted,
            // before this frame culls). While paused for a scope every cell goes on as before.
            if (far && _cellOut[c] && near > keep && !InView(center, radius + ShowMargin)) continue;
            if (far) FarCell(c, near, farScale, ref farBudget);
            else if (_farTaken[c] > 0 && farBudget > 0) farBudget -= PutBackFar(c);
            if (_cellOut[c])
            {
                if (near > keep && !InView(center, radius + ShowMargin)) continue;
                _cellOut[c] = false;
                for (var g = _cellStart[c]; g < _cellStart[c + 1]; g++) Clear(g, OutOfView);
            }
            else if (budget > 0 && near > keep + HideMargin && !InView(center, radius + HideMargin + HideShare * distance))
            {
                _cellOut[c] = true;
                budget -= _cellStart[c + 1] - _cellStart[c];
                for (var g = _cellStart[c]; g < _cellStart[c + 1]; g++) Set(g, OutOfView);
            }
        }
    }

    // A cell's groups too far to draw: put back first (no budget: nothing may show late), then
    // taken out nearest first while the nearest point of the cell is 35% past their distance.
    private static void FarCell(int c, float near, float scale, ref int budget)
    {
        int start = _cellStart[c], count = _cellStart[c + 1] - start, taken = _farTaken[c];
        while (taken > 0 && _farAt[_byFar[start + taken - 1]] * scale * FarKeep >= near) Clear(_byFar[start + --taken], TooFar);
        while (budget > 0 && taken < count && _farAt[_byFar[start + taken]] * scale * FarTake < near)
        {
            var g = _byFar[start + taken++];
#if DEBUG
            if (_reason[g] == 0)
                foreach (var renderer in _parts[g])
                    if (renderer != null && renderer.isVisible)
                    {
                        FarWhileDrawn++;
                        break;
                    }
#endif
            Set(g, TooFar);
            budget--;
        }

        _farTaken[c] = taken;
    }

    private static int PutBackFar(int c)
    {
        var taken = _farTaken[c];
        for (var k = 0; k < taken; k++) Clear(_byFar[_cellStart[c] + k], TooFar);
        _farTaken[c] = 0;
        return taken;
    }

    // A scope's camera, right before it culls: every cell in its view gets its too-far groups back.
    private static void ScopeFar(Camera camera)
    {
        _farResumeAt = Time.unscaledTime + 1f;
        var m = camera.cullingMatrix;
        Vector4 s0 = Normalized(new Vector4(m.m30 + m.m00, m.m31 + m.m01, m.m32 + m.m02, m.m33 + m.m03)),
            s1 = Normalized(new Vector4(m.m30 - m.m00, m.m31 - m.m01, m.m32 - m.m02, m.m33 - m.m03)),
            s2 = Normalized(new Vector4(m.m30 + m.m10, m.m31 + m.m11, m.m32 + m.m12, m.m33 + m.m13)),
            s3 = Normalized(new Vector4(m.m30 - m.m10, m.m31 - m.m11, m.m32 - m.m12, m.m33 - m.m13));
        for (var c = 0; c < _farTaken.Length; c++)
        {
            if (_farTaken[c] == 0) continue;
            Vector3 p = _cellCenter[c];
            var r = -_cellRadius[c] - ShowMargin;
            if (Vector4.Dot(s0, new Vector4(p.x, p.y, p.z, 1f)) >= r && Vector4.Dot(s1, new Vector4(p.x, p.y, p.z, 1f)) >= r
                && Vector4.Dot(s2, new Vector4(p.x, p.y, p.z, 1f)) >= r && Vector4.Dot(s3, new Vector4(p.x, p.y, p.z, 1f)) >= r)
                PutBackFar(c);
        }
    }

    // Groups the game may have switched all off (queued by SwitchPatch, and every baked group
    // once after the build), a budget per frame.
    private static void CheckGameOff()
    {
        var end = Math.Min(ToCheck.Count, _checkCursor + ChecksPerFrame);
        for (; _checkCursor < end; _checkCursor++)
        {
            var g = ToCheck[_checkCursor];
            if (_bakedParts[g] is not { } parts || (_reason[g] & GameOff) != 0) continue;
            var anyOn = false;
            foreach (var renderer in parts)
                if (renderer != null && renderer.enabled) anyOn = true;
            if (!anyOn) Set(g, GameOff);
        }

        if (_checkCursor < ToCheck.Count) return;
        ToCheck.Clear();
        _checkCursor = 0;
    }

    private static void Set(int g, byte reason)
    {
        var was = _reason[g];
        _reason[g] = (byte)(was | reason);
        if (was != 0) return;
        _slot[g] = SceneSkip.ForceNothing(_groups[g], out _native[g]);
        if (_slot[g] != 0) Forced++;
    }

    private static void Clear(int g, byte reason)
    {
        var was = _reason[g];
        if ((was & reason) == 0) return;
        _reason[g] = (byte)(was & ~reason);
        if (_reason[g] != 0 || _slot[g] == 0) return;
        SceneSkip.Unforce(_groups[g], _slot[g], _native[g]);
        _slot[g] = 0;
        Forced--;
    }

    // Everything back (setting off, A/B, an error, leaving the raid).
    internal static void ShowAll()
    {
        for (var g = 0; g < _reason.Length; g++)
        {
            if (_slot[g] != 0) SceneSkip.Unforce(_groups[g], _slot[g], _native[g]);
            _reason[g] = 0;
            _slot[g] = 0;
        }

        Array.Clear(_cellOut, 0, _cellOut.Length);
        Array.Clear(_farTaken, 0, _farTaken.Length);
        Forced = 0;
        // Back on: everything is taken out again, game-off groups too.
        ToCheck.Clear();
        _checkCursor = 0;
        for (var g = 0; g < _bakedParts.Length; g++)
            if (_bakedParts[g] != null) ToCheck.Add(g);
    }

    internal static void Clear()
    {
        ShowAll();
        _groups = Array.Empty<LODGroup>();
        _reason = Array.Empty<byte>();
        _slot = Array.Empty<uint>();
        _native = Array.Empty<IntPtr>();
        _bakedParts = Array.Empty<Renderer[]?>();
        GroupOf.Clear();
        _cellStart = Array.Empty<int>();
        _cellCenter = Array.Empty<Vector3>();
        _cellRadius = Array.Empty<float>();
        _cellOut = Array.Empty<bool>();
        _farAt = Array.Empty<float>();
        _byFar = Array.Empty<int>();
        _farTaken = Array.Empty<int>();
#if DEBUG
        _parts = Array.Empty<Renderer[]>();
        FarWhileDrawn = 0;
#endif
        ToCheck.Clear();
        _checkCursor = 0;
        _build = null;
        _built = false;
        _buildAt = -1f;
        _camera = null;
        Groups = 0;
    }

    // Every static LOD group: sphere, cell, and whether all its meshes are the baked visibility's.
    // A slice per frame (~2 ms).
    private static IEnumerator<bool> Build()
    {
        var slice = (long)(2.0 / Diagnostics.TickMs);
        var start = Stopwatch.GetTimestamp();
        var baked = new HashSet<Renderer>();
        foreach (var crossScene in UnityEngine.Object.FindObjectsByType<PerfectCullingCrossSceneGroup>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (crossScene.bakeGroups == null) continue;
            foreach (var group in crossScene.bakeGroups)
                if (group?.renderers != null) baked.UnionWith(group.renderers);
        }

        var skipLayers = 0;
        foreach (var layer in new[] { "Loot", "Interactive", "PlayerRenderers", "Player", "Deadbody" })
        {
            var index = LayerMask.NameToLayer(layer);
            if (index >= 0) skipLayers |= 1 << index;
        }

        var moving = new Dictionary<Transform, bool>();
        int grown = 0, alone = 0;
        var found = new List<(long Key, LODGroup Group, Vector3 Center, float Radius, Renderer[]? Baked, float FarAt, Renderer[] Parts)>();
        var all = UnityEngine.Object.FindObjectsByType<LODGroup>(FindObjectsSortMode.None);
        for (var n = 0; n < all.Length; n++)
        {
            if ((n & 63) == 0 && Stopwatch.GetTimestamp() - start > slice)
            {
                yield return true;
                start = Stopwatch.GetTimestamp();
            }

            var group = all[n];
            if (group == null || !group.enabled || group.lodCount >= 8 || (skipLayers & (1 << group.gameObject.layer)) != 0) continue;
            var t = group.transform;
            if (DepthCulling.Moves(t, moving)) continue;
            var scale = t.lossyScale;
            var center = t.TransformPoint(group.localReferencePoint);
            var size = group.size * Math.Max(Math.Abs(scale.x), Math.Max(Math.Abs(scale.y), Math.Abs(scale.z)));
            var radius = size * 0.87f;

            Renderer[]? bakedParts = null;
            var parts = new List<Renderer>();
            var allBaked = true;
            var grew = false;
            var lods = group.GetLODs();
            // Unity: relative height = size / 2 / (distance * tan(fov / 2)) * LOD bias; culled
            // below the last level's height.
            var last = lods.Length > 0 ? lods[lods.Length - 1].screenRelativeTransitionHeight : 0f;
            var farAt = last > 0f ? size * 0.5f / last : float.PositiveInfinity;
            foreach (var lod in lods)
            foreach (var renderer in lod.renderers)
            {
                if (renderer == null) continue;
                parts.Add(renderer);
                if (!baked.Contains(renderer)) allBaked = false;
                // The group's size only picks its level (Unity culls by the meshes' own bounds), so
                // it can be smaller than its meshes, and a long road counted as out of view while
                // part of it was on screen. The sphere is grown to cover every mesh.
                var b = renderer.bounds;
                if (b.extents == Vector3.zero) continue;
                var reach = Vector3.Distance(center, b.center) + b.extents.magnitude;
                if (reach > radius)
                {
                    radius = reach;
                    grew = true;
                }
            }

            if (!(radius < 5000f)) continue;
            if (grew) grown++;
            if (allBaked && parts.Count > 0) bakedParts = parts.ToArray();
            // A group bigger than a cell gets a cell of its own: in a shared one its sphere would
            // keep every small neighbour in view with it.
            var big = radius > CellSize;
            if (big) alone++;
            long key = big ? long.MinValue + n
                : ((long)Mathf.FloorToInt(center.x / CellSize) & 0x1FFFFF) << 42 | ((long)Mathf.FloorToInt(center.y / CellSize) & 0x1FFFFF) << 21 | ((long)Mathf.FloorToInt(center.z / CellSize) & 0x1FFFFF);
            // Every part, only for the Debug check of FarWhileDrawn (~1 MB on Streets).
#if DEBUG
            found.Add((key, group, center, radius, bakedParts, farAt, parts.ToArray()));
#else
            found.Add((key, group, center, radius, bakedParts, farAt, Array.Empty<Renderer>()));
#endif
        }

        found.Sort((a, b) => a.Key.CompareTo(b.Key));
        var count = found.Count;
        _groups = new LODGroup[count];
        _reason = new byte[count];
        _slot = new uint[count];
        _native = new IntPtr[count];
        _bakedParts = new Renderer[count][];
        _farAt = new float[count];
#if DEBUG
        _parts = new Renderer[count][];
#endif
        var starts = new List<int>();
        var mins = new List<Vector3>();
        var maxs = new List<Vector3>();
        for (var g = 0; g < count; g++)
        {
            var (key, group, center, radius, parts, farAt, allParts) = found[g];
            _groups[g] = group;
            _bakedParts[g] = parts;
            _farAt[g] = farAt;
#if DEBUG
            _parts[g] = allParts;
#endif
            if (parts != null)
            {
                foreach (var renderer in parts) GroupOf[renderer] = g;
                ToCheck.Add(g);
            }

            var min = center - Vector3.one * radius;
            var max = center + Vector3.one * radius;
            if (g == 0 || key != found[g - 1].Key)
            {
                starts.Add(g);
                mins.Add(min);
                maxs.Add(max);
            }
            else
            {
                var last = mins.Count - 1;
                mins[last] = Vector3.Min(mins[last], min);
                maxs[last] = Vector3.Max(maxs[last], max);
            }
        }

        starts.Add(count);
        _cellStart = starts.ToArray();
        var cells = mins.Count;
        _cellCenter = new Vector3[cells];
        _cellRadius = new float[cells];
        _cellOut = new bool[cells];
        _farTaken = new int[cells];
        _byFar = new int[count];
        var nearestFirst = Comparer<int>.Create((a, b) => _farAt[a].CompareTo(_farAt[b]));
        for (var c = 0; c < cells; c++)
        {
            int from = _cellStart[c], to = _cellStart[c + 1];
            for (var g = from; g < to; g++) _byFar[g] = g;
            Array.Sort(_byFar, from, to - from, nearestFirst);
        }

        for (var c = 0; c < cells; c++)
        {
            _cellCenter[c] = (mins[c] + maxs[c]) * 0.5f;
            _cellRadius[c] = (maxs[c] - mins[c]).magnitude * 0.5f;
        }

        Groups = count;
        Plugin.Log.LogInfo($"LOD culling ({SceneSkip.Note}): {count} static LOD groups in {cells} cells, {ToCheck.Count} of them all the game's baked visibility, {grown} grown to cover their meshes, {alone} in a cell of their own");
    }

    // For the A/B log: forced groups by reason, and how many slots really hold the forced level.
    internal static string Summary()
    {
        int outOfView = 0, gameOff = 0, tooFar = 0, holding = 0;
        for (var g = 0; g < _reason.Length; g++)
        {
            if (_slot[g] == 0) continue;
            if ((_reason[g] & OutOfView) != 0) outOfView++;
            if ((_reason[g] & GameOff) != 0) gameOff++;
            if ((_reason[g] & TooFar) != 0) tooFar++;
            if (SceneSkip.IsForced(_slot[g], _native[g])) holding++;
        }

        var check = "";
#if DEBUG
        check = $", check: {FarWhileDrawn} taken out as too far while drawn";
#endif
        return $"{Forced} of {Groups} LOD groups forced ({outOfView} out of view, {tooFar} too far to draw, {gameOff} switched off by the game; {holding} slots holding it), {Ms:0.00} ms a frame{check}";
    }

    // The sphere within the view's four sides (they meet at the eye, so behind it is outside).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool InView(Vector3 c, float radius) =>
        _side0.x * c.x + _side0.y * c.y + _side0.z * c.z + _side0.w >= -radius
        && _side1.x * c.x + _side1.y * c.y + _side1.z * c.z + _side1.w >= -radius
        && _side2.x * c.x + _side2.y * c.y + _side2.z * c.z + _side2.w >= -radius
        && _side3.x * c.x + _side3.y * c.y + _side3.z * c.z + _side3.w >= -radius;

    private static Vector4 Normalized(Vector4 q)
    {
        var length = (float)Math.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z);
        return length > 0f ? q / length : q;
    }

    // The game's baked visibility switching groups this frame (UpdateSwitchQueues takes from the
    // end of both queues, deactivations first): a group about to get a mesh back is put back
    // first; one losing meshes is checked afterwards (main thread) for whether any is left.
    private class SwitchPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(PerfectCullingCrossSceneGroup), nameof(PerfectCullingCrossSceneGroup.UpdateSwitchQueues));

        [PatchPrefix]
        private static void Prefix(PerfectCullingCrossSceneGroup __instance, FastArray<ushort> ____queueActivate, FastArray<ushort> ____queueDeactivate)
        {
            if (Forced == 0 && ToCheck.Count == 0 && GroupOf.Count == 0) return;
            try
            {
                var take = PerfectCullingSettings.Instance.numActivationsPerVolume / 2;
                var groups = __instance.bakeGroups;
                var main = Thread.CurrentThread.ManagedThreadId == _mainThread;
                Visit(groups, ____queueActivate, take, true, main);
                if (main) Visit(groups, ____queueDeactivate, take, false, true);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"LOD culling, switch queue: {ex}");
            }
        }

        private static void Visit(PerfectCullingBakeGroup[] groups, FastArray<ushort>? queue, int take, bool activate, bool main)
        {
            if (queue == null) return;
            var count = queue.Count;
            var buffer = queue.Buffer;
            for (var k = 1; k <= Math.Min(count, take); k++)
            {
                var b = buffer[count - k];
                if (b >= groups.Length || groups[b]?.renderers is not { } renderers) continue;
                foreach (var renderer in renderers)
                {
                    if (renderer is null || !GroupOf.TryGetValue(renderer, out var g)) continue;
                    if (activate) Clear(g, GameOff);
                    else if (main) ToCheck.Add(g);
                }
            }
        }
    }
}
