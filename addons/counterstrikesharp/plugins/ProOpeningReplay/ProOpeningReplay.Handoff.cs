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
    private bool TryGetRetakeObjectiveHandoffReason(CCSPlayerController player, out string reason)
    {
        reason = string.Empty;
        if (player.Team != CsTeam.CounterTerrorist)
        {
            return false;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn?.AbsOrigin == null || _bombPos == null)
        {
            return false;
        }

        const float retakeC4Radius = 158f;
        var dx = pawn.AbsOrigin.X - _bombPos.X;
        var dy = pawn.AbsOrigin.Y - _bombPos.Y;
        var dz = pawn.AbsOrigin.Z - _bombPos.Z;
        if (dx * dx + dy * dy + dz * dz < retakeC4Radius * retakeC4Radius)
        {
            reason = "atc4";
            return true;
        }

        const float siteProximityRadius = 315f;
        if (dx * dx + dy * dy < siteProximityRadius * siteProximityRadius)
        {
            reason = "atsite";
            return true;
        }

        return false;
    }

    private static void TrackNativeReplayProgress(ReplaySession session, int cursor)
    {
        if (cursor > session.NativeReplayLastCursor)
        {
            session.NativeReplayStallTicks = 0;
            session.NativeReplayLastCursor = cursor;
            return;
        }

        session.NativeReplayStallTicks++;
        session.NativeReplayLastCursor = cursor;
        if (session.NativeReplayDiagnosticLogged || session.NativeReplayStallTicks < 64)
        {
            return;
        }

        session.NativeReplayDiagnosticLogged = true;
    }

    private void EndSession(int sessionIndex, string reason)
    {
        var session = _sessions[sessionIndex];
        _sessions.RemoveAt(sessionIndex);
        Logger.LogInformation(
            "[ProReplay] {Kind} replay ended player={PlayerName} slot={Slot} reason={Reason} cursor={Cursor}/{Total}",
            session.Kind,
            session.Player.PlayerName,
            session.Player.Slot,
            reason,
            session.NativeReplayActive ? BotController.GetReplayCursor(session.NativeReplaySlot) : -1,
            session.NativeReplayTickCount);
        _enemyWatchStates.Remove(PlayerKey(session.Player));
        StopNativeReplay(session);
        ReleaseBotToNativeAi(session.Player);
    }

    private void ReleaseBotToNativeAi(CCSPlayerController player)
    {
        ReleaseNativeControllerState(player.Slot);
        if (player.IsValid && player.PawnIsAlive)
        {
            var pawn = player.PlayerPawn.Value;
            var bot = pawn?.Bot;
            if (bot != null)
            {
                bot.AllowActive = true;
                bot.IsSleeping = false;
                bot.IsCrouching = false;
                bot.EyeAnglesUnderPathFinderControl = false;
                bot.InhibitLookAroundTimestamp = 0f;
                bot.FireWeaponTimestamp = 0f;
                ClearReplayAttackCooldown(pawn);

                CountdownTimer ignoreEnemiesTimer = bot.IgnoreEnemiesTimer;
                ref float ignoreDuration = ref ignoreEnemiesTimer.Duration;
                ignoreDuration = 0f;
                ref float ignoreTimestamp = ref ignoreEnemiesTimer.Timestamp;
                ignoreTimestamp = 0f;

                // Replay ends with the bot likely off-route (we drove its position artificially).
                // The native nav system flags IsStuck almost immediately, and the BotState plugin's
                // unstuck-recovery aggressively triggers JumpTimestamp / StuckJumpTimer -- which is
                // why bots looked like they were bunny-hopping their way back to combat. Clear the
                // stuck flag, suppress jumps briefly, and force a repath so the AI picks a fresh
                // path from where the replay left them instead of treating them as stuck.
                ref bool isStuck = ref bot.IsStuck;
                isStuck = false;
                ref float jumpTimestamp = ref bot.JumpTimestamp;
                jumpTimestamp = Server.CurrentTime + 2.0f;

                CountdownTimer stuckJumpTimer = bot.StuckJumpTimer;
                ref float stuckJumpDur = ref stuckJumpTimer.Duration;
                stuckJumpDur = 2.0f;
                ref float stuckJumpTs = ref stuckJumpTimer.Timestamp;
                stuckJumpTs = Server.CurrentTime + 2.0f;

                CountdownTimer repathTimer = bot.RepathTimer;
                ref float repathDur = ref repathTimer.Duration;
                repathDur = 0f;
                ref float repathTs = ref repathTimer.Timestamp;
                repathTs = Server.CurrentTime;
            }
            // Also force the duck state machine off so the bot doesn't keep crouch-walking after handoff.
            ForceUnduck(pawn);
            SwitchToBestGunForHandoff(player);
            // Register a grace period: suppress IsStuck for 3 seconds so BotState's
            // unstuck-jump logic doesn't fire while the pathfinder recomputes a valid route.
            _handoffGraceExpiry[player] = Server.CurrentTime + 3.0f;
        }

    }

    private void ReleaseNativeControllerState(int slot)
    {
        if (slot < 0 || slot >= 64)
        {
            return;
        }

        BotController.Unlock(slot, LockKind.All);
        BotController.Unlock(slot, LockKind.Aim);
        BotController.Unlock(slot, LockKind.Jump);
        ClearNativeWeaponState(slot);
        BotController.SetBotIdle(slot);
    }

    private const int FL_DUCKING = 1 << 2;

    private static void ForceUnduck(CCSPlayerPawn? pawn)
    {
        if (pawn == null || !pawn.IsValid) return;
        var movement = pawn.MovementServices as CCSPlayer_MovementServices;
        if (movement != null)
        {
            movement.Ducked = false;
            movement.Ducking = false;
            movement.DuckAmount = 0f;
            Utilities.SetStateChanged(pawn, "CCSPlayer_MovementServices", "m_bDucked");
            Utilities.SetStateChanged(pawn, "CCSPlayer_MovementServices", "m_bDucking");
            Utilities.SetStateChanged(pawn, "CCSPlayer_MovementServices", "m_flDuckAmount");
        }
        pawn.Flags &= ~(uint)FL_DUCKING;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_fFlags");
    }

    private static void ClearReplayAttackCooldown(CCSPlayerPawn? pawn)
    {
        var weaponServices = pawn?.WeaponServices as CCSPlayer_WeaponServices;
        if (weaponServices != null)
        {
            weaponServices.NextAttack = 0f;
        }
    }

    private void EndSessionForPlayer(CCSPlayerController player, string reason)
    {
        var playerKey = PlayerKey(player);
        for (var sessionIndex = _sessions.Count - 1; sessionIndex >= 0; sessionIndex--)
        {
            if (PlayerKey(_sessions[sessionIndex].Player) == playerKey)
            {
                EndSession(sessionIndex, reason);
            }
        }
    }

    private void EndSessionsThatHear(CCSPlayerController source, float radius, string reason)
    {
        if (!source.PawnIsAlive)
        {
            return;
        }

        var sourceOrigin = source.PlayerPawn.Value?.AbsOrigin;
        if (sourceOrigin == null)
        {
            return;
        }

        var radiusSquared = radius * radius;
        for (var sessionIndex = _sessions.Count - 1; sessionIndex >= 0; sessionIndex--)
        {
            var listener = _sessions[sessionIndex].Player;
            if (!IsLiveEnemy(source, listener))
            {
                continue;
            }

            var listenerOrigin = listener.PlayerPawn.Value?.AbsOrigin;
            if (listenerOrigin == null || DistanceSquared(sourceOrigin, listenerOrigin) > radiusSquared)
            {
                continue;
            }

            PrimeBotForKnownEnemy(listener, source, markVisible: false);
            EndSession(sessionIndex, reason);
        }
    }

    private static bool HasC4(CCSPlayerPawn? pawn)
    {
        if (pawn == null || !pawn.IsValid) return false;
        var ws = pawn.WeaponServices;
        if (ws == null) return false;
        foreach (var weaponHandle in ws.MyWeapons)
        {
            var weapon = weaponHandle.Value;
            if (weapon != null && weapon.IsValid
                && weapon.DesignerName.Equals("weapon_c4", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private bool IsInBombsite(CCSPlayerController player, out int siteIndex)
    {
        siteIndex = -1;
        var pawn = player.PlayerPawn?.Value;
        if (pawn == null || pawn.AbsOrigin == null || _bombSiteCenters.Count == 0) return false;
        for (var i = 0; i < _bombSiteCenters.Count; i++)
        {
            var site = _bombSiteCenters[i];
            var dx = pawn.AbsOrigin.X - site.X;
            var dy = pawn.AbsOrigin.Y - site.Y;
            if (dx * dx + dy * dy < BombSiteRadius * BombSiteRadius)
            {
                siteIndex = i;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns true if the given bombsite index is the one where the bomb was actually planted.
    /// Compares the bombsite center against _bombPos to find the closest match.
    /// </summary>
    private bool IsPlantedSite(int siteIndex)
    {
        if (_bombPos == null || siteIndex < 0 || siteIndex >= _bombSiteCenters.Count)
            return false;
        // Find which bombsite center is closest to the planted C4 position.
        var bestIdx = -1;
        var bestDistSq = float.MaxValue;
        for (var i = 0; i < _bombSiteCenters.Count; i++)
        {
            var s = _bombSiteCenters[i];
            var dx = _bombPos.X - s.X;
            var dy = _bombPos.Y - s.Y;
            var distSq = dx * dx + dy * dy;
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestIdx = i;
            }
        }
        return siteIndex == bestIdx;
    }

    private static void PrimeBotForKnownEnemy(CCSPlayerController listener, CCSPlayerController enemy, bool markVisible)
    {
        var enemyPawn = enemy.PlayerPawn.Value;
        var enemyOrigin = enemyPawn?.AbsOrigin;
        if (enemyPawn == null || !enemyPawn.IsValid || enemyOrigin == null)
        {
            return;
        }

        var viewZ = enemyPawn.ViewOffset?.Z ?? 64f;
        var target = new Vector(enemyOrigin.X, enemyOrigin.Y, enemyOrigin.Z + (viewZ * 0.72f));
        PrimeBotForKnownEnemy(listener, enemyPawn, target, markVisible);
    }

    private static void PrimeBotForKnownEnemy(
        CCSPlayerController listener,
        CCSPlayerPawn enemyPawn,
        Vector enemyPosition,
        bool markVisible)
    {
        var pawn = listener.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
        {
            return;
        }

        AimPawnAtPosition(pawn, enemyPosition);

        var bot = pawn.Bot;
        if (bot == null)
        {
            return;
        }

        WriteBotKnownEnemy(bot.Handle, enemyPawn, enemyPosition, markVisible);

        bot.IsSleeping = false;
        bot.AllowActive = true;
        bot.EyeAnglesUnderPathFinderControl = false;
        bot.InhibitLookAroundTimestamp = Server.CurrentTime + 0.5f;

        CountdownTimer ignoreEnemiesTimer = bot.IgnoreEnemiesTimer;
        ref float ignoreDuration = ref ignoreEnemiesTimer.Duration;
        ignoreDuration = 0f;
        ref float ignoreTimestamp = ref ignoreEnemiesTimer.Timestamp;
        ignoreTimestamp = 0f;
    }

    private static void WriteBotKnownEnemy(nint botHandle, CCSPlayerPawn enemyPawn, Vector enemyPosition, bool markVisible)
    {
        if (botHandle == nint.Zero)
        {
            return;
        }

        var offsets = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? LinuxBotEnemyMemoryOffsets
            : WindowsBotEnemyMemoryOffsets;

        try
        {
            Marshal.WriteInt32(botHandle + offsets.Enemy, unchecked((int)enemyPawn.EntityHandle.Raw));
            WriteFloat(botHandle + offsets.TargetSpot, enemyPosition.X);
            WriteFloat(botHandle + offsets.TargetSpot + sizeof(float), enemyPosition.Y);
            WriteFloat(botHandle + offsets.TargetSpot + (sizeof(float) * 2), enemyPosition.Z);
            Marshal.WriteByte(botHandle + offsets.IsVisible, markVisible ? (byte)1 : (byte)0);
        }
        catch
        {
            // CCSBot memory layout is version-sensitive. If writing fails, keep the safer eye-angle/AI wakeup path.
        }
    }

    private static void WriteFloat(nint address, float value)
    {
        Marshal.WriteInt32(address, BitConverter.SingleToInt32Bits(value));
    }

    private static void AimPawnAtPosition(CCSPlayerPawn pawn, Vector target)
    {
        var origin = pawn.AbsOrigin;
        if (origin == null)
        {
            return;
        }

        var deltaX = target.X - origin.X;
        var deltaY = target.Y - origin.Y;
        var deltaZ = target.Z - origin.Z;
        var horizontalDistance = MathF.Max(1f, MathF.Sqrt((deltaX * deltaX) + (deltaY * deltaY)));
        pawn.EyeAngles.X = Math.Clamp(-MathF.Atan2(deltaZ, horizontalDistance) * 180f / MathF.PI, -89f, 89f);
        pawn.EyeAngles.Y = MathF.Atan2(deltaY, deltaX) * 180f / MathF.PI;
        pawn.EyeAngles.Z = 0f;
        Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_angEyeAngles");
    }

    private static float DistanceSquared(Vector left, Vector right)
    {
        var deltaX = left.X - right.X;
        var deltaY = left.Y - right.Y;
        var deltaZ = left.Z - right.Z;
        return (deltaX * deltaX) + (deltaY * deltaY) + (deltaZ * deltaZ);
    }
}
