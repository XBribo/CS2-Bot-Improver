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
    private void ProcessReplayGrenades(ReplaySession session, float elapsed)
    {
        if (!_config.ThrowGrenades || session.Grenades.Count == 0)
        {
            return;
        }

        while (session.NextGrenadeIndex < session.Grenades.Count)
        {
            var grenade = session.Grenades[session.NextGrenadeIndex];
            if (grenade.Time > elapsed + 0.015f)
            {
                break;
            }

            session.NextGrenadeIndex++;
            SpawnReplayGrenadeProjectile(session.Player, grenade);
        }
    }

    private void KillNativeReplayGrenadeProjectiles()
    {
        if (!_config.ThrowGrenades || !_config.KillNativeReplayGrenadeProjectiles || _sessions.Count == 0)
        {
            return;
        }

        PruneManifestReplayProjectileProtection();

        HashSet<uint>? replayPawnHandles = null;
        foreach (var session in _sessions)
        {
            if (!session.NativeReplayActive || !session.Player.IsValid || !session.Player.PawnIsAlive)
            {
                continue;
            }

            var pawn = session.Player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
            {
                continue;
            }

            replayPawnHandles ??= [];
            replayPawnHandles.Add(pawn.EntityHandle.Raw);
        }

        if (replayPawnHandles == null || replayPawnHandles.Count == 0)
        {
            return;
        }

        foreach (var designerName in ReplayProjectileDesignerNames)
        {
            foreach (var projectile in Utilities.FindAllEntitiesByDesignerName<CBaseCSGrenadeProjectile>(designerName))
            {
                if (projectile == null || !projectile.IsValid)
                {
                    continue;
                }

                var raw = projectile.EntityHandle.Raw;
                if (_manifestReplayProjectiles.ContainsKey(raw))
                {
                    continue;
                }

                if (replayPawnHandles.Contains(projectile.Thrower.Raw)
                    || replayPawnHandles.Contains(projectile.OriginalThrower.Raw)
                    || replayPawnHandles.Contains(projectile.OwnerEntity.Raw))
                {
                    projectile.AcceptInput("Kill");
                }
            }
        }
    }

    private void PruneManifestReplayProjectileProtection()
    {
        if (_manifestReplayProjectiles.Count == 0)
        {
            return;
        }

        var now = Server.CurrentTime;
        foreach (var (raw, expiresAt) in _manifestReplayProjectiles.ToArray())
        {
            if (now >= expiresAt)
            {
                _manifestReplayProjectiles.Remove(raw);
            }
        }
    }

    private void ProtectManifestReplayProjectile(CBaseCSGrenadeProjectile projectile)
    {
        if (projectile.IsValid)
        {
            _manifestReplayProjectiles[projectile.EntityHandle.Raw] = Server.CurrentTime + ManifestProjectileProtectSeconds;
        }
    }

    private bool SpawnReplayGrenadeProjectile(CCSPlayerController player, ReplayGrenade grenade)
    {
        if (!player.IsValid || !player.PawnIsAlive)
        {
            return false;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
        {
            return false;
        }

        var normalized = NormalizeGrenadeType(grenade.Type);
        var origin = new Vector(grenade.X, grenade.Y, grenade.Z);
        var velocity = new Vector(grenade.VelocityX, grenade.VelocityY, grenade.VelocityZ);
        var angles = ReplayGrenadeAngles(grenade, velocity);
        var teamNum = player.TeamNum;

        try
        {
            CBaseCSGrenadeProjectile? projectile = normalized switch
            {
                "weapon_flashbang" => SpawnReplayFlashProjectile(pawn, teamNum, origin, angles, velocity),
                "weapon_decoy" => SpawnReplayDecoyProjectile(pawn, teamNum, origin, angles, velocity),
                "weapon_smokegrenade" => SmokeProjectileCreate.Invoke(
                    origin.Handle,
                    origin.Handle,
                    velocity.Handle,
                    velocity.Handle,
                    pawn.Handle,
                    45,
                    teamNum),
                "weapon_hegrenade" => HeProjectileCreate.Invoke(
                    origin.Handle,
                    origin.Handle,
                    velocity.Handle,
                    velocity.Handle,
                    pawn.Handle,
                    44),
                "weapon_molotov" => MolotovProjectileCreate.Invoke(
                    origin.Handle,
                    origin.Handle,
                    velocity.Handle,
                    velocity.Handle,
                    pawn.Handle,
                    46),
                "weapon_incgrenade" => MolotovProjectileCreate.Invoke(
                    origin.Handle,
                    origin.Handle,
                    velocity.Handle,
                    velocity.Handle,
                    pawn.Handle,
                    48),
                _ => null
            };

            if (projectile == null || !projectile.IsValid)
            {
                return false;
            }

            AssignReplayProjectileOwner(projectile, pawn, teamNum);
            ProtectManifestReplayProjectile(projectile);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[ProReplay] replay grenade spawn failed: {ex.Message}");
            return false;
        }
    }

    private static QAngle ReplayGrenadeAngles(ReplayGrenade grenade, Vector velocity)
    {
        if (float.IsFinite(grenade.Pitch) || float.IsFinite(grenade.Yaw))
        {
            return new QAngle(
                float.IsFinite(grenade.Pitch) ? grenade.Pitch : 0f,
                float.IsFinite(grenade.Yaw) ? grenade.Yaw : 0f,
                0f);
        }

        var yaw = MathF.Atan2(velocity.Y, velocity.X) * (180f / MathF.PI);
        var horizontal = MathF.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y);
        var pitch = -MathF.Atan2(velocity.Z, horizontal) * (180f / MathF.PI);
        return new QAngle(pitch, yaw, 0f);
    }

    private static CFlashbangProjectile? SpawnReplayFlashProjectile(
        CCSPlayerPawn pawn,
        int teamNum,
        Vector origin,
        QAngle angles,
        Vector velocity)
    {
        var flash = Utilities.CreateEntityByName<CFlashbangProjectile>("flashbang_projectile");
        if (flash == null)
        {
            return null;
        }

        AssignReplayProjectileOwner(flash, pawn, teamNum);
        SetReplayProjectileInitialKinematics(flash, origin, velocity);
        flash.Elasticity = 0.33f;
        flash.Teleport(origin, angles, velocity);
        flash.DispatchSpawn();
        flash.Teleport(origin, angles, velocity);
        return flash;
    }

    private static CDecoyProjectile? SpawnReplayDecoyProjectile(
        CCSPlayerPawn pawn,
        int teamNum,
        Vector origin,
        QAngle angles,
        Vector velocity)
    {
        var decoy = Utilities.CreateEntityByName<CDecoyProjectile>("decoy_projectile");
        if (decoy == null)
        {
            return null;
        }

        AssignReplayProjectileOwner(decoy, pawn, teamNum);
        SetReplayProjectileInitialKinematics(decoy, origin, velocity);
        decoy.Elasticity = 0.33f;
        decoy.Teleport(origin, angles, velocity);
        decoy.DispatchSpawn();
        decoy.Teleport(origin, angles, velocity);
        return decoy;
    }

    private static void AssignReplayProjectileOwner(
        CBaseCSGrenadeProjectile projectile,
        CCSPlayerPawn pawn,
        int teamNum)
    {
        projectile.TeamNum = (byte)teamNum;
        projectile.Thrower.Raw = pawn.EntityHandle.Raw;
        projectile.OriginalThrower.Raw = pawn.EntityHandle.Raw;
        projectile.OwnerEntity.Raw = pawn.EntityHandle.Raw;
    }

    private static void SetReplayProjectileInitialKinematics(
        CBaseCSGrenadeProjectile projectile,
        Vector origin,
        Vector velocity)
    {
        projectile.InitialPosition.X = origin.X;
        projectile.InitialPosition.Y = origin.Y;
        projectile.InitialPosition.Z = origin.Z;
        projectile.InitialVelocity.X = velocity.X;
        projectile.InitialVelocity.Y = velocity.Y;
        projectile.InitialVelocity.Z = velocity.Z;
    }

}
