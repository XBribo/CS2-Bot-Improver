using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BotControllerApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using RayTraceAPI;

namespace ProOpeningReplay;

public sealed partial class ProOpeningReplayPlugin
{
    private bool ShouldHandOff(CCSPlayerController player, IEnumerable<CCSPlayerController> allPlayers)
        => TryGetHandOffEnemy(player, allPlayers, out _);

    private bool TryGetHandOffEnemy(
        CCSPlayerController player,
        IEnumerable<CCSPlayerController> allPlayers,
        out CCSPlayerController? contactEnemy)
    {
        contactEnemy = null;
        if (!_config.StopOnEnemyContact)
        {
            return false;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
        {
            return true;
        }

        var players = allPlayers as IReadOnlyList<CCSPlayerController> ?? allPlayers.ToArray();
        if (TryGetRayTraceVisibleEnemy(player, pawn, players, out contactEnemy))
        {
            return true;
        }

        // Fast path: trust the bot's own perception. Bot AI runs during replay (AllowActive=true),
        // so the engine's sensor loop populates IsEnemyVisible / LastSawEnemyTimestamp normally.
        var bot = pawn.Bot;
        if (bot != null)
        {
            if (bot.IsEnemyVisible)
            {
                return true;
            }
            // LastSawEnemyTimestamp updates the moment the sensor sees the enemy, even on the same
            // tick visibility transitions to true. Treat anything within the last 0.2s as "just saw".
            if (bot.LastSawEnemyTimestamp > 0f &&
                Server.CurrentTime - bot.LastSawEnemyTimestamp < 0.2f)
            {
                return true;
            }
        }

        // Fallback: engine's spotted-state bitmask (radar/minimap red-dot data). Slower than the
        // bot's own perception but covers the case where pawn.Bot is unavailable or perception was
        // disabled by config. Still strict LOS+FOV from the engine -- no wallhack.
        var slot = player.Slot;
        if (slot < 0 || slot >= 64)
        {
            return false;
        }

        var slotIndex = slot >> 5;
        var slotBit = 1u << (slot & 31);

        foreach (var enemy in players)
        {
            if (!IsLiveEnemy(player, enemy))
            {
                continue;
            }

            var enemyPawn = enemy.PlayerPawn.Value;
            if (enemyPawn == null || !enemyPawn.IsValid)
            {
                continue;
            }

            var mask = enemyPawn.EntitySpottedState.SpottedByMask;
            if (mask.Length > slotIndex && (mask[slotIndex] & slotBit) != 0)
            {
                contactEnemy = enemy;
                return true;
            }
        }

        return false;
    }

    private bool TryGetEnemyWatchingOpeningReplay(
        ReplaySession session,
        IReadOnlyList<CCSPlayerController> allPlayers,
        out CCSPlayerController watcher)
    {
        watcher = null!;
        if (!_config.StopOpeningReplayWhenSeenByEnemy || session.Kind != ReplaySessionKind.Opening)
        {
            return false;
        }

        var targetPawn = session.Player.PlayerPawn.Value;
        if (targetPawn == null || !targetPawn.IsValid || targetPawn.AbsOrigin == null)
        {
            return false;
        }

        var rayTrace = TryGetRayTrace();
        if (rayTrace == null)
        {
            return false;
        }

        var playerKey = PlayerKey(session.Player);
        if (_enemyWatchStates.TryGetValue(playerKey, out var current))
        {
            foreach (var candidate in allPlayers)
            {
                if (!IsLiveEnemy(session.Player, candidate)
                    || PlayerKey(candidate) != current.EnemyKey
                    || !IsEnemyWatchingReplayBot(rayTrace, session.Player, targetPawn, candidate))
                {
                    continue;
                }

                watcher = candidate;
                return true;
            }
        }

        var bestDistance = float.MaxValue;
        CCSPlayerController? best = null;
        foreach (var enemy in allPlayers)
        {
            if (!IsEnemyWatchingReplayBot(rayTrace, session.Player, targetPawn, enemy))
            {
                continue;
            }

            var enemyOrigin = enemy.PlayerPawn.Value?.AbsOrigin;
            if (enemyOrigin == null)
            {
                continue;
            }

            var distance = DistanceSquared(enemyOrigin, targetPawn.AbsOrigin);
            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            best = enemy;
        }

        if (best == null)
        {
            return false;
        }

        watcher = best;
        return true;
    }

    private bool IsEnemyWatchingReplayBot(
        CRayTraceInterface rayTrace,
        CCSPlayerController replayBot,
        CCSPlayerPawn replayPawn,
        CCSPlayerController enemy)
    {
        if (!IsLiveEnemy(replayBot, enemy))
        {
            return false;
        }

        var enemyPawn = enemy.PlayerPawn.Value;
        return enemyPawn != null
            && enemyPawn.IsValid
            && TryGetEyePosition(enemyPawn, out var enemyEye)
            && RayTraceSeesEnemy(rayTrace, enemyPawn, enemyEye, replayPawn);
    }

    private bool TrackEnemyWatchingReplayBot(ReplaySession session, CCSPlayerController watcher)
    {
        var playerKey = PlayerKey(session.Player);
        var watcherKey = PlayerKey(watcher);
        var now = Server.CurrentTime;

        if (!_enemyWatchStates.TryGetValue(playerKey, out var state)
            || state.EnemyKey != watcherKey
            || now - state.LastSeenTime > 0.15f)
        {
            state = new EnemyWatchState(watcherKey, now);
            _enemyWatchStates[playerKey] = state;
        }

        state.LastSeenTime = now;
        return now - state.VisibleSince >= Math.Max(0.05f, _config.EnemySeenHandoffSeconds);
    }

    private bool RayTraceSeesAnyEnemy(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        IEnumerable<CCSPlayerController> allPlayers)
        => TryGetRayTraceVisibleEnemy(player, pawn, allPlayers, out _);

    private bool TryGetRayTraceVisibleEnemy(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        IEnumerable<CCSPlayerController> allPlayers,
        out CCSPlayerController? visibleEnemy)
    {
        visibleEnemy = null;
        var rayTrace = TryGetRayTrace();
        if (rayTrace == null || !TryGetEyePosition(pawn, out var eye))
        {
            return false;
        }

        var bestDistance = float.MaxValue;
        foreach (var enemy in allPlayers)
        {
            if (!IsLiveEnemy(player, enemy))
            {
                continue;
            }

            var enemyPawn = enemy.PlayerPawn.Value;
            if (enemyPawn == null || !enemyPawn.IsValid || enemyPawn.AbsOrigin == null)
            {
                continue;
            }

            if (RayTraceSeesEnemy(rayTrace, pawn, eye, enemyPawn))
            {
                var distance = pawn.AbsOrigin == null
                    ? 0f
                    : DistanceSquared(pawn.AbsOrigin, enemyPawn.AbsOrigin);
                if (visibleEnemy == null || distance < bestDistance)
                {
                    bestDistance = distance;
                    visibleEnemy = enemy;
                }
            }
        }

        return visibleEnemy != null;
    }

    private static bool RayTraceSeesEnemy(
        CRayTraceInterface rayTrace,
        CCSPlayerPawn observerPawn,
        Vector observerEye,
        CCSPlayerPawn targetPawn)
    {
        var targetOrigin = targetPawn.AbsOrigin;
        if (targetOrigin == null)
        {
            return false;
        }

        var targetViewZ = targetPawn.ViewOffset?.Z ?? 64f;
        return RayTraceSeesPoint(rayTrace, observerPawn, observerEye,
                new Vector(targetOrigin.X, targetOrigin.Y, targetOrigin.Z + targetViewZ))
            || RayTraceSeesPoint(rayTrace, observerPawn, observerEye,
                new Vector(targetOrigin.X, targetOrigin.Y, targetOrigin.Z + (targetViewZ * 0.72f)))
            || RayTraceSeesPoint(rayTrace, observerPawn, observerEye,
                new Vector(targetOrigin.X, targetOrigin.Y, targetOrigin.Z + (targetViewZ * 0.45f)));
    }

    private static bool RayTraceSeesPoint(
        CRayTraceInterface rayTrace,
        CCSPlayerPawn observerPawn,
        Vector observerEye,
        Vector target)
    {
        if (!IsPointInReplayFov(observerPawn, observerEye, target, 110f, 90f))
        {
            return false;
        }

        var options = new TraceOptions(InteractionLayers.MASK_WORLD_ONLY);
        return rayTrace.TraceEndShape(observerEye, target, observerPawn, options, out var result)
            && !result.IsAllSolid
            && result.Fraction >= 0.995f;
    }

    private CRayTraceInterface? TryGetRayTrace()
    {
        if (_rayTrace != null)
        {
            return _rayTrace;
        }

        try
        {
            _rayTrace = _rayTraceCapability.Get();
        }
        catch
        {
            _rayTrace = null;
        }

        return _rayTrace;
    }

    private static bool TryGetEyePosition(CCSPlayerPawn pawn, out Vector eye)
    {
        eye = new Vector(0f, 0f, 0f);
        var origin = pawn.AbsOrigin;
        if (origin == null)
        {
            return false;
        }

        var viewZ = pawn.ViewOffset?.Z ?? 64f;
        eye = new Vector(origin.X, origin.Y, origin.Z + viewZ);
        return true;
    }

    private static bool IsPointInReplayFov(
        CCSPlayerPawn pawn,
        Vector eye,
        Vector target,
        float horizontalDegrees,
        float verticalDegrees)
    {
        var eyeAngles = pawn.EyeAngles;
        if (eyeAngles == null)
        {
            return false;
        }

        var dx = target.X - eye.X;
        var dy = target.Y - eye.Y;
        var dz = target.Z - eye.Z;
        var horizontalDistance = Math.Sqrt((dx * dx) + (dy * dy));
        if (horizontalDistance < 1e-3 && Math.Abs(dz) < 1e-3)
        {
            return true;
        }

        var yawToTarget = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        var pitchToTarget = -Math.Atan2(dz, horizontalDistance) * 180.0 / Math.PI;
        var yawDelta = NormalizeAngleDeg(yawToTarget - eyeAngles.Y);
        var pitchDelta = NormalizeAngleDeg(pitchToTarget - eyeAngles.X);

        return Math.Abs(yawDelta) <= horizontalDegrees * 0.5
            && Math.Abs(pitchDelta) <= verticalDegrees * 0.5;
    }

    private static double NormalizeAngleDeg(double angle)
    {
        angle %= 360.0;
        if (angle > 180.0) angle -= 360.0;
        if (angle < -180.0) angle += 360.0;
        return angle;
    }

}
