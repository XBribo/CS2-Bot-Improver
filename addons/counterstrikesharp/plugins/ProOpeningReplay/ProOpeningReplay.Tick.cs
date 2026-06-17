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
    private void OnTick()
    {
        if (!_freezeEnded && !IsWarmupPeriod() && CanUseDataset())
        {
            ApplyNativeBuySuppressionForCurrentBots();
        }

        // Suppress IsStuck for bots in the handoff grace period. BotState's unstuck logic
        // fires every tick and will override our EndSession cleanup otherwise.
        if (_handoffGraceExpiry.Count > 0)
        {
            var now = Server.CurrentTime;
            List<CCSPlayerController>? expired = null;
            foreach (var (player, expiry) in _handoffGraceExpiry)
            {
                if (now >= expiry)
                {
                    expired ??= [];
                    expired.Add(player);
                    continue;
                }
                if (!player.IsValid || !player.PawnIsAlive) { expired ??= []; expired.Add(player); continue; }
                var pawn = player.PlayerPawn?.Value;
                var bot = pawn?.Bot;
                if (bot != null)
                {
                    ref bool isStuck = ref bot.IsStuck;
                    isStuck = false;
                }
            }
            if (expired != null)
                foreach (var p in expired)
                    _handoffGraceExpiry.Remove(p);
        }

        if (_sessions.Count == 0 && _retakeMoveTos.Count == 0)
        {
            return;
        }

        // Cache live player snapshot once per tick. ShouldHandOff used to call Utilities.GetPlayers per session,
        // which is O(sessions * total players) every tick.
        var allPlayersThisTick = Utilities.GetPlayers();

        ProcessRetakeMoveTos(allPlayersThisTick);
        KillNativeReplayGrenadeProjectiles();

        for (var sessionIndex = _sessions.Count - 1; sessionIndex >= 0; sessionIndex--)
        {
            var session = _sessions[sessionIndex];
            if (!session.Player.IsValid || !session.Player.PawnIsAlive)
            {
                EndSession(sessionIndex, "dead");
                continue;
            }

            // End on hurt only if the damage event happened AFTER this session started.
            // Without the timestamp gate, a retake session inherits the bot's prior opening-replay
            // hurt event and ends instantly with "after 0.0s (hurt)".
            if (_lastHurtTime.TryGetValue(PlayerKey(session.Player), out var hurtAt)
                && hurtAt > session.StartTime)
            {
                EndSession(sessionIndex, "hurt");
                continue;
            }

            if (ShouldHandOff(session.Player, allPlayersThisTick))
            {
                EndSession(sessionIndex, "contact");
                continue;
            }

            if (TryGetEnemyWatchingOpeningReplay(session, allPlayersThisTick, out var watcher)
                && TrackEnemyWatchingReplayBot(session, watcher))
            {
                PrimeBotForKnownEnemy(session.Player, watcher, markVisible: true);
                EndSession(sessionIndex, "seen_by_enemy");
                continue;
            }

            // Opening T with bomb: end replay when entering bombsite so bot AI plants.
            if (session.Kind == ReplaySessionKind.Opening
                && session.Player.Team == CsTeam.Terrorist
                && _bombSiteCenters.Count > 0
                && IsInBombsite(session.Player, out _)
                && HasC4(session.Player.PlayerPawn.Value))
            {
                EndSession(sessionIndex, "plant");
                continue;
            }

            // CT: end replay when entering the PLANTED bombsite after bomb is planted.
            // Only trigger for the bombsite where the bomb actually is, so CTs rotating through
            // a non-target site keep following their replay path.
            if (_bombPlantTime > 0f
                && session.Player.Team == CsTeam.CounterTerrorist
                && _bombSiteCenters.Count > 0
                && _bombPos != null
                && IsInBombsite(session.Player, out var enteredSiteIdx)
                && IsPlantedSite(enteredSiteIdx))
            {
                EndSession(sessionIndex, "retake_site");
                continue;
            }

            if (session.Kind == ReplaySessionKind.Retake
                && TryGetRetakeObjectiveHandoffReason(session.Player, out var objectiveReason))
            {
                EndSession(sessionIndex, objectiveReason);
                continue;
            }

            // Frames exhausted: hand back to the AI. We previously held the pose forever, but that left
            // bots standing motionless or twitching at the pre-aim spot if no enemy ever showed up,
            // when in practice they should now go play normal Counter-Strike.
            var elapsed = Server.CurrentTime - session.StartTime;
            if (elapsed > session.LastFrameTime + 0.25f)
            {
                EndSession(sessionIndex, "complete");
                continue;
            }

            if (!session.NativeReplayActive)
            {
                EndSession(sessionIndex, "native_inactive");
                continue;
            }

            var nativeCursor = BotController.GetReplayCursor(session.NativeReplaySlot);
            if (nativeCursor < 0)
            {
                EndSession(sessionIndex, "native_complete");
                continue;
            }
            TrackNativeReplayProgress(session, nativeCursor);

            ProcessReplayGrenades(session, elapsed);
            ApplyReplaySideEffects(session);
        }
    }

}
