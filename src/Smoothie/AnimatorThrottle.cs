using System;
using System.Collections.Generic;
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

        // Stepped by Unity: the animators are switched back on for one frame with their speed
        // scaled up, so Unity's own (multi-threaded) update covers the time they're behind.
        // The scale for that frame, 0 when not stepping; the game's speeds to put back.
        public float StepScale;
        public float BodySpeed, ArmsSpeed;

        // Handed back to Unity after this last step.
        public bool Releasing;
    }

    private static readonly ConditionalWeakTable<Player, State> States = new();

    private static readonly AccessTools.FieldRef<MovementContext, Player> MovementPlayer =
        AccessTools.FieldRefAccess<MovementContext, Player>("_player");

    private static readonly AccessTools.FieldRef<Player, BetterSource> StepSource =
        AccessTools.FieldRefAccess<Player, BetterSource>("NestedStepSoundSource");

    // Box around a standing bot, padded so bots at the edge of the screen count as on it.
    private static readonly Vector3 BotBox = new(2f, 2.5f, 2f);

    // Lowest animation rate for a bot in a fight that you can't see.
    private const float FightingRate = 30f;

    // Sanity cap on a learned footstep range.
    private const float MaxAudibleRange = 200f;

    private static readonly Plane[] MainPlanes = new Plane[6];
    private static readonly Plane[] OpticPlanes = new Plane[6];
    private static int _frame = -1;
    private static bool _hasCamera;
    private static bool _hasOptic;
    private static Vector3 _cameraPosition;

    // Other human players in the raid (Fika), refreshed once a frame. A bot this PC runs
    // (hosting) has its walk move it through its animator, and the host sends that position
    // to every client: a bot throttled for the host's camera moves in steps on a client
    // standing next to it. So bots near another player are animated for that player too.
    private static readonly List<Vector3> OtherPlayers = new();
    private static float _mainZoom = 1f;
    private static float _opticZoom = 1f;

    // Loudest footstep range seen this raid. Every bot is held to it, which also covers bots
    // that haven't stepped yet and so still carry a stale range on their source.
    private static float _audibleRange;

    // Last full frame's numbers, for the stats overlay.
    internal static int Throttled;

    // Why the others weren't, last full frame: in view within IK range, in a fight, or
    // close/audible enough that their rate is about the frame rate anyway.
    internal static int FullInView, FullFighting, FullOther;
    private static int _fullInView, _fullFighting, _fullOther;

    // Why PickInterval kept a bot at full rate, for the overlay's counts.
    private enum FullRate { Other, InView, Fighting }

    // Frame the last Unity step started; its movement update runs the frame after.
    private static int _lastStepStart = -10;
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

    // For the snapshot: did the animation step see this player in the last frame or this one.
    internal static bool HandledRecently(Player player) =>
        HandledFrame.TryGetValue(player, out var handled) && handled[0] >= Time.frameCount - 1;

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
        var dt = Time.deltaTime;

        // Unity stepped this bot this frame (switched on at the end of the last one).
        var stepped = state != null && state.StepScale > 0f;
        if (stepped) FinishUnityStep(state!, dt);
        if (state != null && state.Releasing)
        {
            States.Remove(player);
            return;
        }

        // Switched off this frame, so Unity didn't animate it: this frame's time is owed too.
        if (state != null && !stepped) state.Pending += dt;

        var throttling = Plugin.On(Plugin.ThrottleAnimators) && _hasCamera && IsBot(player);
        var reason = FullRate.Other;
        var interval = throttling ? PickInterval(player, out reason) : 0f;

        if (interval <= dt * 1.5f)
        {
            if (throttling)
            {
                if (reason == FullRate.InView) _fullInView++;
                else if (reason == FullRate.Fighting) _fullFighting++;
                else _fullOther++;
            }

            if (state != null && !Release(player, state)) States.Remove(player);
            return;
        }

        _throttled++;

        if (state == null)
        {
            // Unity has already animated this bot for this frame, so nothing is pending yet.
            state = new State();
            States.Add(player, state);
        }

        Take(ref state.Body, player.BodyUpdateMode == Player.EUpdateMode.Auto ? player.BodyAnimatorCommon : null);
        Take(ref state.Arms, player.ArmsUpdateMode == Player.EUpdateMode.Auto ? player.ArmsAnimatorCommon : null);
        // Only hold back arms we can step ourselves, or they would freeze.
        state.DeferArms = player.ArmsUpdateMode == Player.EUpdateMode.Manual && IsAlive(player.ArmsAnimatorCommon);

        if (state.Pending >= interval * state.Jitter)
        {
            if (!StartUnityStep(player, state, dt)) Step(player, state);
        }
    }

    // Manual Animator.Update runs on the main thread, one animator at a time (0.2-0.4 ms each
    // was measured); Unity's own update batches every animator on worker threads. So a step
    // switches the animators back on for one frame, sped up to cover the time they're behind,
    // and lets Unity do it. False when there's nothing Unity can step (only hand-stepped arms).
    private static bool StartUnityStep(Player player, State state, float dt)
    {
        if (!Plugin.On(Plugin.UnityStepping) || (!IsAlive(state.Body) && !IsAlive(state.Arms))) return false;

        // Unity will add next frame's time times this; roughly what's owed. The difference is
        // carried over in FinishUnityStep.
        var scale = Mathf.Min(1f + state.Pending / Mathf.Max(dt, 0.001f), MaxStepScale);
        if (IsAlive(state.Body))
        {
            state.BodySpeed = state.Body!.speed;
            state.Body.speed = state.BodySpeed * scale;
            state.Body.enabled = true;
        }

        if (IsAlive(state.Arms))
        {
            state.ArmsSpeed = state.Arms!.speed;
            state.Arms.speed = state.ArmsSpeed * scale;
            state.Arms.enabled = true;
        }

        state.StepScale = scale;
        _lastStepStart = Time.frameCount;

        // Arms the game steps by hand have no Unity update to ride on: stepped here, as before.
        if (state.DeferArms) StepDeferredArms(player, state.Pending);
        return true;
    }

    // Right after Unity's update on the step frame: speeds back, switched off again unless
    // being handed back.
    private static void FinishUnityStep(State state, float dt)
    {
        var scale = state.StepScale;
        state.StepScale = 0f;

        // Owed: what was behind, plus this frame. Applied: this frame times the scale.
        state.Pending = Mathf.Max(0f, state.Pending + dt - dt * scale);

        RestoreSpeed(state.Body, state.BodySpeed, scale);
        RestoreSpeed(state.Arms, state.ArmsSpeed, scale);
        if (state.Releasing)
        {
            state.Body = null;
            state.Arms = null;
            return;
        }

        if (IsAlive(state.Body)) state.Body!.enabled = false;
        if (IsAlive(state.Arms)) state.Arms!.enabled = false;
    }

    // A bot switched off mid-step gets no LateUpdate, so nothing would end the step: its
    // animators would keep the sped-up speed, and the movement hook would keep stretching its
    // time step (the game still ticks a switched-off body's movement) until it woke. Ended as
    // if no time had passed, so what it's owed is caught up once it's back.
    internal static void EndStepIfAsleep(Player player)
    {
        if (!States.TryGetValue(player, out var state) || state.StepScale <= 0f) return;
        FinishUnityStep(state, 0f);
        if (state.Releasing) States.Remove(player);
    }

    // The game only sets a speed for sprint-to-idle; if it did in between, its value stays.
    private static void RestoreSpeed(IAnimator? animator, float speed, float scale)
    {
        if (IsAlive(animator) && Mathf.Approximately(animator!.speed, speed * scale)) animator.speed = speed;
    }

    private const float MaxStepScale = 20f;

    // The bot walks by root motion, capped at displacement / MovementContext.LastDeltaTime
    // (see Step). The game sets LastDeltaTime in MovementContext.ManualUpdate, before Unity's
    // animation; on a step frame it has to cover the whole stretch.
    internal static void AfterMovementUpdate(MovementContext movement, float deltaTime)
    {
        // Called for every player every frame; nearly always no step is running.
        if (Time.frameCount - _lastStepStart > 1) return;
        var player = MovementPlayer(movement);
        if (player != null && States.TryGetValue(player, out var state) && state.StepScale > 0f)
        {
            movement.LastDeltaTime = deltaTime * state.StepScale;
        }
    }

    private static void StepDeferredArms(Player player, float dt)
    {
        if (dt <= 0f) return;
        var start = Stopwatch.GetTimestamp();
        UpdateDeferredArms(player, dt);
        _steps++;
        _stepTicks += Stopwatch.GetTimestamp() - start;
    }

    private static void UpdateDeferredArms(Player player, float dt)
    {
        if (IsAlive(player.ArmsAnimatorCommon)) player.ArmsAnimatorCommon.Update(dt);
        player.UnderbarrelWeaponArmsAnimator?.Update(dt);
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

    // How far the nearest other human player is from a bot this PC runs; infinity for
    // anyone else's bots (a Fika client's) and with no other players around.
    internal static float OtherPlayerDistance(Player player)
    {
        if (!player.IsAI || OtherPlayers.Count == 0) return float.PositiveInfinity;
        var position = player.Position;
        var nearest = float.PositiveInfinity;
        foreach (var other in OtherPlayers) nearest = Mathf.Min(nearest, Vector3.Distance(position, other));
        return nearest;
    }

    // Hosting: no slower than this for a bot another player is near, so the positions sent
    // to them (Fika's send rate is 10-30 per second) move smoothly.
    private const float OtherPlayerRate = 30f;

    private static float PickInterval(Player player, out FullRate reason)
    {
        reason = FullRate.Other;
        var position = player.Position;
        var distance = Vector3.Distance(position, _cameraPosition);

        // The game runs IK on top of the animator's pose here. A held pose would get IK
        // applied a second time, so these animate every frame like vanilla. The same test the
        // IK uses: close bots behind walls skip the IK (Occlusion), so they can be throttled.
        // Fika's observed bots run their IK in their own pass, which Smoothie leaves alone, so
        // for them only the game's own visibility counts.
        if (distance <= EFTHardSettings.Instance.CULL_GROUNDER
            && (Occlusion.OwnIkPass(player) ? player.IsVisible : Occlusion.VisibleForIk(player)))
        {
            reason = FullRate.InView;
            return 0f;
        }

        var otherPlayer = OtherPlayerDistance(player);
        if (otherPlayer <= Plugin.FullRateDistance.Value)
        {
            reason = FullRate.InView;
            return 0f;
        }

        // A bot right in front of you but behind a wall counts as off screen.
        var onScreen = InView(position, out var inOptic) && !Occlusion.IsHidden(player);

        // Local bots in a fight: semi-auto shots and reloads wait on arms-animator events,
        // and the muzzle position comes from the animated skeleton. Events are fired for the
        // whole stretch an update covers (AnimationEventsContainer), so a slower animator
        // fires them late, never skips them: one you can see animates every frame, one you
        // can't at least FightingRate, which keeps them at most 33 ms late.
        var memory = player.AIData?.BotOwner?.Memory;
        var fighting = memory != null && (memory.HaveEnemy || memory.IsUnderFire);
        if (fighting && onScreen)
        {
            reason = FullRate.Fighting;
            return 0f;
        }

        // The step source's range is set from the bot's movement, gear and the player's
        // hearing each time it plays a step.
        var source = StepSource(player);
        if (source != null && source.MaxDistance > _audibleRange)
        {
            _audibleRange = Mathf.Min(source.MaxDistance, MaxAudibleRange);
        }

        var interval = ThrottleMath.Interval(
            distance,
            onScreen,
            inOptic ? _opticZoom : _mainZoom,
            AudibleRange * 1.1f,
            Plugin.FullRateDistance.Value,
            Plugin.LowestRateDistance.Value,
            Plugin.LowestVisibleRate.Value,
            Plugin.OffscreenRate.Value,
            Plugin.FootstepRate.Value);
        if (fighting) interval = Mathf.Min(interval, 1f / FightingRate);
        if (otherPlayer <= Plugin.LowestRateDistance.Value) interval = Mathf.Min(interval, 1f / OtherPlayerRate);
        return interval;
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

    internal static bool InView(Bounds bounds) =>
        _hasOptic && GeometryUtility.TestPlanesAABB(OpticPlanes, bounds) || GeometryUtility.TestPlanesAABB(MainPlanes, bounds);

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

            if (state.DeferArms) UpdateDeferredArms(player, dt);
        }
        finally
        {
            if (movement != null) movement.LastDeltaTime = saved;
        }

        _steps++;
        _stepTicks += Stopwatch.GetTimestamp() - start;
    }

    // Hands the animators back to Unity. True when that takes one more frame (a last Unity
    // step catching up); the state is dropped after it.
    private static bool Release(Player player, State state)
    {
        // Catch up first so the pose is current when Unity takes over again.
        if (StartUnityStep(player, state, Time.deltaTime))
        {
            state.Releasing = true;
            return true;
        }

        Step(player, state);
        GiveBack(state.Body);
        GiveBack(state.Arms);
        state.Body = null;
        state.Arms = null;
        return false;
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
        FullInView = _fullInView;
        FullFighting = _fullFighting;
        FullOther = _fullOther;
        _fullInView = _fullFighting = _fullOther = 0;
        Steps = _steps;
        StepMs = _stepTicks * Diagnostics.TickMs;
        _throttled = 0;
        _steps = 0;
        _stepTicks = 0;

        var manager = CameraManager.Instance;
        var camera = manager.Camera;
        _hasCamera = camera != null;
        if (camera == null) return;

        _cameraPosition = camera.transform.position;
        GeometryUtility.CalculateFrustumPlanes(camera, MainPlanes);

        OtherPlayers.Clear();
        if (Singleton<GameWorld>.Instantiated)
        {
            foreach (var other in Singleton<GameWorld>.Instance.AllAlivePlayersList)
            {
                if (other != null && !other.IsAI && !other.IsYourPlayer) OtherPlayers.Add(other.Position);
            }
        }

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
