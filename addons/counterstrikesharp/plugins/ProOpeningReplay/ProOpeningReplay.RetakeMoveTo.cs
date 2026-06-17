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
    private void ProcessRetakeMoveTos(List<CCSPlayerController> allPlayersThisTick)
    {
        for (var i = _retakeMoveTos.Count - 1; i >= 0; i--)
        {
            var moveTo = _retakeMoveTos[i];
            if (!moveTo.Player.IsValid || !moveTo.Player.PawnIsAlive)
            {
                _retakeMoveTos.RemoveAt(i);
                continue;
            }

            if (_lastHurtTime.TryGetValue(PlayerKey(moveTo.Player), out var hurtAt)
                && hurtAt > moveTo.StartTime)
            {
                EndRetakeMoveTo(i);
                continue;
            }

            if (TryGetHandOffEnemy(moveTo.Player, allPlayersThisTick, out var moveToEnemy))
            {
                EndRetakeMoveTo(i, moveToEnemy);
                continue;
            }

            if (TryGetRetakeObjectiveHandoffReason(moveTo.Player, out _))
            {
                EndRetakeMoveTo(i);
                continue;
            }

            if (IsAtRetakeMoveTarget(moveTo.Player, moveTo))
            {
                _retakeMoveTos.RemoveAt(i);
                if (!StartRetakeReplayFromMoveTo(moveTo))
                {
                    ReleaseBotToNativeAi(moveTo.Player);
                }
                continue;
            }

            if (Server.CurrentTime - moveTo.StartTime > _config.RetakeMoveToTimeout)
            {
                EndRetakeMoveTo(i);
                continue;
            }

            if (Server.CurrentTime >= moveTo.NextIssueTime && !TryIssueRetakeMoveTo(moveTo))
            {
                EndRetakeMoveTo(i);
                continue;
            }

            ApplyReplayControlSideEffects(moveTo.Player, moveTo.StartTime, allowReplayAttack: false);
        }
    }

    private void EndRetakeMoveTo(int index, CCSPlayerController? knownEnemy = null)
    {
        var moveTo = _retakeMoveTos[index];
        _retakeMoveTos.RemoveAt(index);
        if (knownEnemy != null)
        {
            PrimeBotForKnownEnemy(moveTo.Player, knownEnemy, markVisible: true);
        }
        ReleaseBotToNativeAi(moveTo.Player);
        BotController.SetBotIdle(moveTo.Player.Slot);
    }

    private bool StartRetakeReplayFromMoveTo(RetakeMoveToSession moveTo)
    {
        var startTime = Server.CurrentTime;
        var session = new ReplaySession(
            moveTo.Player, moveTo.Round, moveTo.ReplayPlayer, moveTo.Frames,
            grenades: BuildSessionGrenades(moveTo.ReplayPlayer, ReplaySessionKind.Retake),
            startTime: startTime,
            kind: ReplaySessionKind.Retake,
            nativeReplayStartTick: NativeReplayStartTick(moveTo.ReplayPlayer, ReplaySessionKind.Retake));

        if (!TryStartNativeReplay(session))
        {
            return false;
        }
        ApplyReplaySideEffects(session);

        var botCtrl = moveTo.Player.PlayerPawn.Value?.Bot;
        if (botCtrl != null)
        {
            botCtrl.InhibitLookAroundTimestamp = startTime + 130f;
        }

        _sessions.Add(session);
        return true;
    }

    private bool TryIssueRetakeMoveTo(RetakeMoveToSession moveTo)
    {
        if (!TryIssueBotMoveTo(moveTo.Player, moveTo.Target, BotMoveRouteType.Fastest))
        {
            return false;
        }

        moveTo.NextIssueTime = Server.CurrentTime + _config.RetakeMoveToRefreshInterval;
        return true;
    }

    private bool TryIssueBotMoveTo(CCSPlayerController player, Vector target, BotMoveRouteType route)
    {
        if (!_moveToAvailable)
        {
            return false;
        }

        var pawn = player.PlayerPawn.Value;
        var bot = pawn?.Bot;
        if (pawn == null || !pawn.IsValid || bot == null)
        {
            return false;
        }

        bot.AllowActive = true;
        bot.IsSleeping = false;

        _moveToArgVec.X = target.X;
        _moveToArgVec.Y = target.Y;
        _moveToArgVec.Z = target.Z;

        try
        {
            NativeSignatures.CCSBotMoveTo.Invoke(bot.Handle, _moveToArgVec.Handle, (int)route);
            return true;
        }
        catch (Exception ex)
        {
            if (_moveToAvailable)
            {
                Logger.LogError($"[ProReplay] CCSBot::MoveTo unavailable: {ex.Message}");
            }
            _moveToAvailable = false;
            return false;
        }
    }

    private bool IsAtRetakeMoveTarget(CCSPlayerController player, RetakeMoveToSession moveTo)
    {
        var origin = player.PlayerPawn.Value?.AbsOrigin;
        if (origin == null)
        {
            return false;
        }

        var dx = origin.X - moveTo.Target.X;
        var dy = origin.Y - moveTo.Target.Y;
        var dz = Math.Abs(origin.Z - moveTo.Target.Z);
        var threshold = Math.Max(16f, _config.RetakeMoveToReachThreshold);
        return dx * dx + dy * dy <= threshold * threshold
            && dz <= Math.Max(96f, threshold);
    }

}
