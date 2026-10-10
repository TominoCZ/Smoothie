using System;
using System.Diagnostics;
using Random = UnityEngine.Random;
using System.Runtime.CompilerServices;
using AnimationSystem;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using EFT.Settings;
using HarmonyLib;
using UnityEngine;

namespace Smoothie;

// Steps far-away and off-screen bots' animators at a lower rate instead of every frame.
//
// Same trick the game uses itself (Player.FastForwardToStationaryWeapon, ObserverPlayerCulling):
// keep the controller state on disable, switch the Animator off so Unity skips it, and call
// Animator.Update with the time that has built up since the last step.
internal static class AnimatorThrottle
{
    private sealed class State
    {
        // Seconds the animators are behind.
        public float Pending;

        // Bots that start throttling on the same frame (the camera turning away from a
        // group) would otherwise all step on the same later frames.
        public readonly float Jitter = Random.Range(0.8f, 1f);

        // Animators we switched off, so they can be handed back.
        public IAnimator? Body;
        public IAnimator? Arms;

        // Arms the game steps by hand every frame (Fika's observed bots). Their update is
        // held back in Player.ArmsUpdate and stepped here with the body instead.
        public bool DeferArms;
    }

    private static readonly ConditionalWeakTable<Player, State> States = new();

    private static readonly AccessTools.FieldRef<Player, BetterSource> StepSource =
        AccessTools.FieldRefAccess<Player, BetterSource>("NestedStepSoundSource");

    // Box around a standing bot, padded so bots at the edge of the screen count as on it.
    private static readonly Vector3 BotBox = new(2f, 2.5f, 2f);

    // Sanity cap on a learned footstep range.
    private const float MaxAudibleRange = 200f;

    private static readonly Plane[] MainPlanes = new Plane[6];
    private static readonly Plane[] OpticPlanes = new Plane[6];
    private static int _frame = -1;
    private static bool _hasCamera;
    private static bool _hasOptic;
    private static Vector3 _cameraPosition;
    private static float _mainZoom = 1f;
    private static float _opticZoom = 1f;

    // Loudest footstep range seen this raid. Every bot is held to it, which also covers bots
    // that haven't stepped yet and so still carry a stale range on their source.
    private static float _audibleRange;

    // Last full frame's numbers, for the stats overlay.
    internal static int Throttled;
    internal static int Steps;
    internal static double StepMs;
    private static int _throttled;
    private static int _steps;
    private static long _stepTicks;

    internal static void OnRaidStart() => _audibleRange = 0f;

    internal static float AudibleRange => Mathf.Max(_audibleRange, Plugin.FootstepRange.Value);

    // Runs right before the player's own LateUpdate, which is where the game reads the
    // animator's curves and applies the root motion it produced. Unity's own animator
    // update sits at the same point in the frame, after Update and before LateUpdate.
    // Frame each player was last handled. Normally once per frame anyway; this keeps it
    // once if a Fika update ever starts calling Player.LateUpdate from its own override
    // (both patches would then run).
    private static readonly ConditionalWeakTable<Player, int[]> HandledFrame = new();

    internal static void BeforeLateUpdateSafe(Player player)
    {
        var handled = HandledFrame.GetValue(player, _ => new[] { -1 });
        if (handled[0] == Time.frameCount) return;
        handled[0] = Time.frameCount;

        try
        {
            HandsCulling.Check(player);
        }
        catch (Exception ex)
        {
            Plugin.Fail(Plugin.HideCulledWeapons, ex);
        }

        try
        {
            if (!player.IsYourPlayer && IsBot(player)) DistantDetail.Update(player);
        }
        catch (Exception ex)
        {
            Plugin.Fail(Plugin.SimplifyDistant, ex);
        }

        try
        {
            BeforeLateUpdate(player);
        }
        catch (Exception ex)
        {
            Plugin.Fail(Plugin.ThrottleAnimators, ex);
        }
    }

    private static void BeforeLateUpdate(Player player)
    {
        if (player.IsYourPlayer) return;

        States.TryGetValue(player, out var state);

        if (player.HealthController?.IsAlive != true)
        {
            // Player.OnDead has already switched the animators off for the ragdoll.
            if (state != null) States.Remove(player);
            return;
        }

        NextFrame();

        var interval = Plugin.On(Plugin.ThrottleAnimators) && _hasCamera && IsBot(player) ? PickInterval(player) : 0f;
        var dt = Time.deltaTime;

        if (interval <= dt * 1.5f)
        {
            if (state != null)
            {
                Release(player, state);
                States.Remove(player);
            }

            return;
        }

        _throttled++;

        if (state == null)
        {
            // Unity has already animated this bot for this frame, so nothing is pending yet.
            state = new State();
            States.Add(player, state);
        }
        else
        {
            state.Pending += dt;
        }

        Take(ref state.Body, player.BodyUpdateMode == Player.EUpdateMode.Auto ? player.BodyAnimatorCommon : null);
        Take(ref state.Arms, player.ArmsUpdateMode == Player.EUpdateMode.Auto ? player.ArmsAnimatorCommon : null);
        // Only hold back arms we can step ourselves, or they would freeze.
        state.DeferArms = player.ArmsUpdateMode == Player.EUpdateMode.Manual && IsAlive(player.ArmsAnimatorCommon);

        if (state.Pending >= interval * state.Jitter)
        {
            Step(player, state);
        }
    }

    // Player.ArmsUpdate in Manual mode does three things: the hands controller update, the
    // arms animator update, and ArmsLateUpdate. For a throttled bot only the animator update
    // should be held back, so the mode is set to None for the call (which skips both Manual
    // blocks) and ArmsLateUpdate is run afterwards, as the original would have.
    internal static bool HoldArms(Player player)
    {
        if (player.ArmsUpdateMode != Player.EUpdateMode.Manual
            || !States.TryGetValue(player, out var state)
            || !state.DeferArms)
        {
            return false;
        }

        player._armsUpdateMode = Player.EUpdateMode.None;
        return true;
    }

    internal static void ReleaseArms(Player player, float deltaTime, bool ranCleanly)
    {
        player._armsUpdateMode = Player.EUpdateMode.Manual;
        if (ranCleanly) player.ArmsLateUpdate(deltaTime);
    }

    private static float PickInterval(Player player)
    {
        var position = player.Position;
        var distance = Vector3.Distance(position, _cameraPosition);

        // The game runs IK on top of the animator's pose here. A held pose would get IK
        // applied a second time, so these animate every frame like vanilla.
        if (player.IsVisible && distance <= EFTHardSettings.Instance.CULL_GROUNDER) return 0f;

        // Local bots in a fight: semi-auto shots and reloads wait on arms-animator events,
        // and the muzzle position comes from the animated skeleton.
        var memory = player.AIData?.BotOwner?.Memory;
        if (memory != null && (memory.HaveEnemy || memory.IsUnderFire)) return 0f;

        // A bot right in front of you but behind a wall counts as off screen.
        var onScreen = InView(position, out var inOptic) && !Occlusion.IsHidden(player);

        // The step source's range is set from the bot's movement, gear and the player's
        // hearing each time it plays a step.
        var source = StepSource(player);
        if (source != null && source.MaxDistance > _audibleRange)
        {
            _audibleRange = Mathf.Min(source.MaxDistance, MaxAudibleRange);
        }

        return ThrottleMath.Interval(
            distance,
            onScreen,
            inOptic ? _opticZoom : _mainZoom,
            AudibleRange * 1.1f,
            Plugin.FullRateDistance.Value,
            Plugin.LowestRateDistance.Value,
            Plugin.LowestVisibleRate.Value,
            Plugin.OffscreenRate.Value,
            Plugin.FootstepRate.Value);
    }

    internal static bool HasCamera => _hasCamera;
    internal static Vector3 CameraPosition => _cameraPosition;

    // In the main camera's view, or the scope's. Only valid after NextFrame.
    internal static bool InView(Vector3 position, out bool inOptic)
    {
        var bounds = new Bounds(position + Vector3.up, BotBox);
        inOptic = _hasOptic && GeometryUtility.TestPlanesAABB(OpticPlanes, bounds);
        return inOptic || GeometryUtility.TestPlanesAABB(MainPlanes, bounds);
    }

    internal static float ZoomAt(Vector3 position) => InView(position, out var inOptic) && inOptic ? _opticZoom : _mainZoom;

    internal static bool IsBot(Player player)
    {
        // Local bots, when this game is the host or offline.
        if (player.IsAI) return true;

        // Fika client: bots arrive as ObservedPlayer without a BotOwner. Same test Fika
        // uses (FikaGlobals.IsPlayerProfile): a human profile has a pet id, a registration
        // date, or a main-profile nickname (player scavs).
        var profile = player.Profile;
        return profile != null
               && string.IsNullOrEmpty(profile.PetId?.ToString())
               && profile.Info.RegistrationDate <= 0
               && string.IsNullOrEmpty(profile.Info.MainProfileNickname);
    }

    private static void Take(ref IAnimator? owned, IAnimator? current)
    {
        if (!ReferenceEquals(owned, current))
        {
            // The weapon (and with it the arms animator) changed.
            GiveBack(owned);
            owned = null;
        }

        if (!IsAlive(current)) return;

        // Disabled by the game, not by us: leave it be.
        if (owned == null && !current!.enabled) return;

        current!.keepAnimatorControllerStateOnDisable = true;
        if (current.enabled) current.enabled = false;
        owned = current;
    }

    private static void Step(Player player, State state)
    {
        var dt = state.Pending;
        state.Pending = 0f;
        if (dt <= 0f) return;

        var start = Stopwatch.GetTimestamp();

        // Movement is root motion: MovementState.OnStateMove runs inside the animator update
        // and passes the animator's deltaPosition to the character controller together with
        // MovementContext.LastDeltaTime, which then caps speed at displacement / dt. Left at
        // one frame's dt, a step covering several frames would be clamped and the bot would
        // walk slower.
        var movement = player.MovementContext;
        var saved = movement != null ? movement.LastDeltaTime : 0f;
        if (movement != null) movement.LastDeltaTime = dt;

        try
        {
            if (IsAlive(state.Body)) state.Body!.Update(dt);
            if (IsAlive(state.Arms)) state.Arms!.Update(dt);

            if (state.DeferArms)
            {
                if (IsAlive(player.ArmsAnimatorCommon)) player.ArmsAnimatorCommon.Update(dt);
                player.UnderbarrelWeaponArmsAnimator?.Update(dt);
            }
        }
        finally
        {
            if (movement != null) movement.LastDeltaTime = saved;
        }

        _steps++;
        _stepTicks += Stopwatch.GetTimestamp() - start;
    }

    private static void Release(Player player, State state)
    {
        // Catch up first so the pose is current when Unity takes over again next frame.
        state.Pending += Time.deltaTime;
        Step(player, state);
        GiveBack(state.Body);
        GiveBack(state.Arms);
        state.Body = null;
        state.Arms = null;
    }

    private static void GiveBack(IAnimator? animator)
    {
        if (IsAlive(animator)) animator!.enabled = true;
    }

    // Only plain Unity animators. The fast-animator and spirit paths are driven differently.
    private static bool IsAlive(IAnimator? animator) =>
        animator is UnityAnimatorWrapper wrapper && wrapper.Animator != null;

    internal static void NextFrame()
    {
        if (_frame == Time.frameCount) return;
        _frame = Time.frameCount;

        Throttled = _throttled;
        Steps = _steps;
        StepMs = _stepTicks * 1000.0 / Stopwatch.Frequency;
        _throttled = 0;
        _steps = 0;
        _stepTicks = 0;

        var manager = CameraManager.Instance;
        var camera = manager.Camera;
        _hasCamera = camera != null;
        if (camera == null) return;

        _cameraPosition = camera.transform.position;
        GeometryUtility.CalculateFrustumPlanes(camera, MainPlanes);

        var referenceFov = Singleton<SettingsManager>.Instantiated
            ? Singleton<SettingsManager>.Instance.Game.Settings.FieldOfView.Value
            : 75f;

        // Aiming narrows the main camera, and so does a scope with PiP-Disabler installed.
        _mainZoom = ThrottleMath.Zoom(referenceFov, camera.fieldOfView);

        // A magnified scope renders through its own camera onto the lens.
        var optic = manager.OpticCameraManager;
        var opticCamera = optic?.Camera;
        _hasOptic = optic != null && optic.IsAnyOpticCameraRendering && opticCamera != null && opticCamera.isActiveAndEnabled;
        if (_hasOptic && opticCamera != null)
        {
            GeometryUtility.CalculateFrustumPlanes(opticCamera, OpticPlanes);
            _opticZoom = ThrottleMath.Zoom(referenceFov, opticCamera.fieldOfView);
        }
    }
}
