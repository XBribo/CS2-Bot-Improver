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
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using RayTraceAPI;

namespace ProOpeningReplay;

[MinimumApiVersion(304)]
public sealed class ProOpeningReplayPlugin : BasePlugin
{
    public override string ModuleName => "Pro Opening Replay";
    public override string ModuleVersion => "0.1.0";
    public override string ModuleAuthor => "GitHub Copilot";
    public override string ModuleDescription => "Replays extracted pro opening defaults for bots before first contact.";

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private ReplayConfig _config = new();
    private ReplayDataset? _dataset;
    private bool _nativeReplayAvailable;
    private static readonly PluginCapability<CRayTraceInterface> _rayTraceCapability = new("raytrace:craytraceinterface");
    private CRayTraceInterface? _rayTrace;
    private readonly List<ReplaySession> _sessions = [];
    private readonly List<RetakeMoveToSession> _retakeMoveTos = [];
    private readonly Vector _moveToArgVec = new(0f, 0f, 0f);
    private bool _moveToAvailable = true;
    private readonly Dictionary<int, int> _lastEnsuredWeaponDef = [];
    private readonly Dictionary<int, int> _lastReplayWeaponDef = [];
    private readonly Dictionary<int, LockTarget> _lastLockedWeaponTarget = [];
    private readonly HashSet<(int Slot, int DefIndex)> _preloadedReplayWeapons = [];
    // Bots that recently exited replay — suppress IsStuck for a grace period to prevent BotState's
    // unstuck logic from making them jump/spin while the pathfinder recalculates a valid route.
    private readonly Dictionary<CCSPlayerController, float> _handoffGraceExpiry = [];
    private readonly Dictionary<int, ReplayAssignment> _pendingAssignments = [];
    private readonly Dictionary<int, PreparedOpeningSession> _preparedOpeningSessions = [];
    private readonly Dictionary<int, string> _nativeReplayPreloadKeys = [];
    private readonly HashSet<int> _loadoutAppliedKeys = [];
    private readonly Dictionary<int, int> _roundLoadoutBudgets = [];
    private readonly Dictionary<CsTeam, RoundEconomyIndex> _roundIndexes = [];
    private readonly Dictionary<CsTeam, SpawnReplayIndex> _spawnIndexes = [];
    private readonly Dictionary<int, float> _lastHurtTime = [];
    // Precomputed per-team retake candidate pools, populated in BuildRoundIndexes after the dataset
    // loads. Built once instead of per-bomb-plant so OnBombPlanted -> StartRetakeSessions stays cheap
    // (otherwise scanning 600+ rounds * ~10 players * ~9000 frames each on the main thread on every
    // plant causes a multi-frame server hitch).
    private readonly List<RetakeCandidate> _ctRetakeCandidates = [];
    private readonly List<RetakeCandidate> _tRetakeCandidates = [];
    private int _retakeCandidateRoundsWithPlant;

    // Dataset-derived site centroids computed in BuildRoundIndexes via k-means on PlantPos values.
    // Used for retake site classification instead of func_bomb_target (which is unreliable for brush entities).
    private readonly List<Vector> _datasetSiteCentroids = [];
    private readonly Random _random = new();
    private bool _roundPrepared;
    private bool _freezeEnded;
    private CancellationTokenSource? _replayBundlePrewarmCancellation;
    private int _replayBundlePrewarmGeneration;
    private int _replayBundlePrewarmTotal;
    private int _replayBundlePrewarmCompleted;
    private int _replayBundlePrewarmFailed;
    // Bomb state for retake replay end conditions. Set on OnBombPlanted, cleared on OnRoundEnd.
    private float _bombPlantTime = -1f;
    private float _bombDetonationTime = -1f;
    private Vector? _bombPos;
    // Bombsite centers cached on round start. Used to end T opening replay when bot enters a site.
    private readonly List<Vector> _bombSiteCenters = [];
    // Approximate bombsite radius (CS units). Most sites span 200-400u; 250 is generous and matches
    // "foot inside the painted A/B area" intuition without firing while still in the choke leading in.
    private const float BombSiteRadius = 150f;
    // Bomb timer in seconds; competitive default is 40. Updated from the planted_c4 entity if available.
    private const float DefaultBombTimerSeconds = 40f;
    private static readonly HashSet<string> PrimaryWeapons = new(StringComparer.OrdinalIgnoreCase)
    {
        "weapon_ak47", "weapon_aug", "weapon_awp", "weapon_famas", "weapon_g3sg1", "weapon_galilar",
        "weapon_m4a1", "weapon_m4a1_silencer", "weapon_sg556", "weapon_ssg08", "weapon_scar20",
        "weapon_mac10", "weapon_mp5sd", "weapon_mp7", "weapon_mp9", "weapon_bizon", "weapon_p90", "weapon_ump45",
        "weapon_mag7", "weapon_nova", "weapon_sawedoff", "weapon_xm1014", "weapon_m249", "weapon_negev"
    };

    // Weapons that should never be given to bots (auto-snipers and LMGs are unrealistic for normal play).
    private static readonly HashSet<string> BannedWeapons = new(StringComparer.OrdinalIgnoreCase)
    {
        "weapon_scar20", "weapon_g3sg1", "weapon_m249", "weapon_negev"
    };

    private static readonly HashSet<string> UtilityItems = new(StringComparer.OrdinalIgnoreCase)
    {
        "weapon_flashbang", "weapon_hegrenade", "weapon_smokegrenade", "weapon_molotov", "weapon_incgrenade", "weapon_decoy", "weapon_taser"
    };

    private static readonly HashSet<string> ThrowableUtilityItems = new(StringComparer.OrdinalIgnoreCase)
    {
        "weapon_flashbang", "weapon_hegrenade", "weapon_smokegrenade", "weapon_molotov", "weapon_incgrenade", "weapon_decoy"
    };

    private static readonly HashSet<string> SecondaryWeapons = new(StringComparer.OrdinalIgnoreCase)
    {
        "weapon_glock", "weapon_hkp2000", "weapon_usp_silencer", "weapon_elite", "weapon_p250", "weapon_tec9",
        "weapon_fiveseven", "weapon_deagle", "weapon_cz75a", "weapon_revolver"
    };

    private static readonly HashSet<string> DefaultPistols = new(StringComparer.OrdinalIgnoreCase)
    {
        "weapon_glock", "weapon_hkp2000", "weapon_usp_silencer"
    };

    private static readonly Dictionary<string, string> GrenadeTypeAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["smokegrenade_projectile"] = "weapon_smokegrenade",
        ["CSmokeGrenade"] = "weapon_smokegrenade",
        ["CSmokeGrenadeProjectile"] = "weapon_smokegrenade",
        ["SmokeGrenade"] = "weapon_smokegrenade",
        ["weapon_smokegrenade"] = "weapon_smokegrenade",
        ["molotov_projectile"] = "weapon_molotov",
        ["CMolotovGrenade"] = "weapon_molotov",
        ["CMolotovProjectile"] = "weapon_molotov",
        ["Molotov"] = "weapon_molotov",
        ["weapon_molotov"] = "weapon_molotov",
        ["incendiary_projectile"] = "weapon_incgrenade",
        ["CIncendiaryGrenade"] = "weapon_incgrenade",
        ["CIncendiaryGrenadeProjectile"] = "weapon_incgrenade",
        ["IncendiaryGrenade"] = "weapon_incgrenade",
        ["weapon_incgrenade"] = "weapon_incgrenade",
        ["hegrenade_projectile"] = "weapon_hegrenade",
        ["CHEGrenade"] = "weapon_hegrenade",
        ["CHEGrenadeProjectile"] = "weapon_hegrenade",
        ["HeGrenade"] = "weapon_hegrenade",
        ["weapon_hegrenade"] = "weapon_hegrenade",
        ["decoy_projectile"] = "weapon_decoy",
        ["CDecoyGrenade"] = "weapon_decoy",
        ["CDecoyProjectile"] = "weapon_decoy",
        ["DecoyGrenade"] = "weapon_decoy",
        ["weapon_decoy"] = "weapon_decoy",
        ["flashbang_projectile"] = "weapon_flashbang",
        ["CFlashbang"] = "weapon_flashbang",
        ["CFlashbangProjectile"] = "weapon_flashbang",
        ["Flashbang"] = "weapon_flashbang",
        ["weapon_flashbang"] = "weapon_flashbang"
    };

    private static readonly HashSet<string> RifleLikeWeapons = new(StringComparer.OrdinalIgnoreCase)
    {
        "weapon_ak47", "weapon_aug", "weapon_famas", "weapon_galilar", "weapon_m4a1", "weapon_m4a1_silencer", "weapon_sg556"
    };

    private static readonly Dictionary<string, int> ItemPrices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["weapon_glock"] = 0,
        ["weapon_hkp2000"] = 0,
        ["weapon_usp_silencer"] = 0,
        ["item_kevlar"] = 650,
        ["item_assaultsuit"] = 1_000,
        ["item_defuser"] = 400,
        ["weapon_taser"] = 200,
        ["weapon_elite"] = 300,
        ["weapon_p250"] = 300,
        ["weapon_tec9"] = 500,
        ["weapon_fiveseven"] = 500,
        ["weapon_deagle"] = 700,
        ["weapon_cz75a"] = 500,
        ["weapon_revolver"] = 600,
        ["weapon_mac10"] = 1_050,
        ["weapon_mp9"] = 1_250,
        ["weapon_mp7"] = 1_500,
        ["weapon_mp5sd"] = 1_500,
        ["weapon_ump45"] = 1_200,
        ["weapon_bizon"] = 1_400,
        ["weapon_p90"] = 2_350,
        ["weapon_nova"] = 1_050,
        ["weapon_xm1014"] = 2_000,
        ["weapon_sawedoff"] = 1_100,
        ["weapon_mag7"] = 1_300,
        ["weapon_galilar"] = 1_800,
        ["weapon_ak47"] = 2_700,
        ["weapon_sg556"] = 3_000,
        ["weapon_famas"] = 1_950,
        ["weapon_m4a1"] = 2_900,
        ["weapon_m4a1_silencer"] = 2_900,
        ["weapon_aug"] = 3_300,
        ["weapon_ssg08"] = 1_700,
        ["weapon_awp"] = 4_750,
        ["weapon_scar20"] = 5_000,
        ["weapon_g3sg1"] = 5_000,
        ["weapon_negev"] = 1_700,
        ["weapon_m249"] = 5_200,
        ["weapon_flashbang"] = 200,
        ["weapon_hegrenade"] = 300,
        ["weapon_smokegrenade"] = 300,
        ["weapon_molotov"] = 400,
        ["weapon_incgrenade"] = 500,
        ["weapon_decoy"] = 50
    };

    private static readonly Dictionary<string, int> WeaponDefIndexes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["weapon_deagle"] = 1,
        ["weapon_elite"] = 2,
        ["weapon_fiveseven"] = 3,
        ["weapon_glock"] = 4,
        ["weapon_ak47"] = 7,
        ["weapon_aug"] = 8,
        ["weapon_awp"] = 9,
        ["weapon_famas"] = 10,
        ["weapon_g3sg1"] = 11,
        ["weapon_galilar"] = 13,
        ["weapon_m249"] = 14,
        ["weapon_m4a1"] = 16,
        ["weapon_mac10"] = 17,
        ["weapon_p90"] = 19,
        ["weapon_mp5sd"] = 23,
        ["weapon_ump45"] = 24,
        ["weapon_xm1014"] = 25,
        ["weapon_bizon"] = 26,
        ["weapon_mag7"] = 27,
        ["weapon_negev"] = 28,
        ["weapon_sawedoff"] = 29,
        ["weapon_tec9"] = 30,
        ["weapon_taser"] = 31,
        ["weapon_hkp2000"] = 32,
        ["weapon_mp7"] = 33,
        ["weapon_mp9"] = 34,
        ["weapon_nova"] = 35,
        ["weapon_p250"] = 36,
        ["weapon_scar20"] = 38,
        ["weapon_sg556"] = 39,
        ["weapon_ssg08"] = 40,
        ["weapon_knife"] = 42,
        ["weapon_knife_t"] = 42,
        ["weapon_bayonet"] = 42,
        ["weapon_m9_bayonet"] = 42,
        ["weapon_karambit"] = 42,
        ["weapon_butterfly"] = 42,
        ["weapon_flip"] = 42,
        ["weapon_gut"] = 42,
        ["weapon_tactical"] = 42,
        ["weapon_falchion"] = 42,
        ["weapon_push"] = 42,
        ["weapon_survival_bowie"] = 42,
        ["weapon_ursus"] = 42,
        ["weapon_gypsy_jackknife"] = 42,
        ["weapon_stiletto"] = 42,
        ["weapon_widowmaker"] = 42,
        ["weapon_skeleton"] = 42,
        ["weapon_kukri"] = 42,
        ["weapon_flashbang"] = 43,
        ["weapon_hegrenade"] = 44,
        ["weapon_smokegrenade"] = 45,
        ["weapon_molotov"] = 46,
        ["weapon_decoy"] = 47,
        ["weapon_incgrenade"] = 48,
        ["weapon_c4"] = 49,
        ["weapon_m4a1_silencer"] = 60,
        ["weapon_usp_silencer"] = 61,
        ["weapon_cz75a"] = 63,
        ["weapon_revolver"] = 64,
    };

    private static readonly HashSet<int> PrimaryWeaponDefIndexes = new(
        PrimaryWeapons
            .Select(itemName => WeaponDefIndexes.GetValueOrDefault(itemName, -1))
            .Where(defIndex => defIndex >= 0));

    public override void Load(bool hotReload)
    {
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventBombPlanted>(OnBombPlanted);
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        RegisterEventHandler<EventPlayerBlind>(OnPlayerBlind);
        RegisterEventHandler<EventPlayerFootstep>(OnPlayerFootstep);
        RegisterEventHandler<EventPlayerSound>(OnPlayerSound);
        RegisterListener<Listeners.OnTick>(OnTick);
        // Reload the per-map dataset on every map change so de_dust2 -> de_inferno swaps in the right openings.
        RegisterListener<Listeners.OnMapStart>(_ => LoadDataset());

        LoadConfig();
        _nativeReplayAvailable = BotController.IsCompatible();
        LoadDataset();

        if (hotReload && !string.IsNullOrWhiteSpace(Server.MapName))
        {
            PrepareRound();
        }
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _rayTrace = TryGetRayTrace();
    }

    public override void Unload(bool hotReload)
    {
        StopAllNativeReplays();
        ClearRetakeMoveTos(releaseBots: true);
        CancelReplayBundlePrewarm();
        _sessions.Clear();
        _pendingAssignments.Clear();
        _preparedOpeningSessions.Clear();
        _nativeReplayPreloadKeys.Clear();
        _loadoutAppliedKeys.Clear();
        _roundLoadoutBudgets.Clear();
        _lastHurtTime.Clear();
        ClearNativeWeaponState();
        _roundPrepared = false;
    }

    [ConsoleCommand("css_proreplay_reload", "Reloads the pro opening replay config and dataset.")]
    public void ReloadCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        LoadConfig();
        BotController.ResetCompatibility();
        _nativeReplayAvailable = BotController.IsCompatible();
        _nativeReplayPreloadKeys.Clear();
        _preparedOpeningSessions.Clear();
        LoadDataset();
        Reply(player, commandInfo, $"loaded {_dataset?.Rounds.Count ?? 0} rounds for {_dataset?.MapName ?? "no dataset"}");
    }

    [ConsoleCommand("css_proreplay_status", "Prints the current pro opening replay status.")]
    public void StatusCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        Reply(player, commandInfo,
            $"enabled={_config.Enabled}, native={(_nativeReplayAvailable ? "on" : BotController.Status)}, map={Server.MapName}, rounds={_dataset?.Rounds.Count ?? 0}, pending={_pendingAssignments.Count}, moveTo={_retakeMoveTos.Count}, active={_sessions.Count}, prewarm={Volatile.Read(ref _replayBundlePrewarmCompleted)}/{Volatile.Read(ref _replayBundlePrewarmTotal)} fail={Volatile.Read(ref _replayBundlePrewarmFailed)}");
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        StopAllNativeReplays();
        ClearRetakeMoveTos(releaseBots: true);
        _sessions.Clear();
        _pendingAssignments.Clear();
        _preparedOpeningSessions.Clear();
        _nativeReplayPreloadKeys.Clear();
        _loadoutAppliedKeys.Clear();
        _roundLoadoutBudgets.Clear();
        _lastHurtTime.Clear();
        _handoffGraceExpiry.Clear();
        ClearNativeWeaponState();
        _roundPrepared = false;
        _freezeEnded = false;
        CaptureRoundLoadoutBudgets();

        // Cache bombsite centers for the "T entered the bombsite" opening end-condition. func_bomb_target
        // entities exist on every defusal map and have an AbsOrigin at the painted-area centroid.
        _bombSiteCenters.Clear();
        foreach (var site in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("func_bomb_target"))
        {
            if (site == null || !site.IsValid || site.AbsOrigin == null) continue;
            _bombSiteCenters.Add(new Vector(site.AbsOrigin.X, site.AbsOrigin.Y, site.AbsOrigin.Z));
        }

        if (!CanUseDataset())
        {
            return HookResult.Continue;
        }

        ScheduleFreezePrepareAttempts();
        return HookResult.Continue;
    }

    private HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        _freezeEnded = true;
        if (!CanUseDataset())
        {
            return HookResult.Continue;
        }

        if (_loadoutAppliedKeys.Count == 0
            && (!_roundPrepared || _pendingAssignments.Count == 0 || !AssignmentsCoverCurrentBots()))
        {
            PrepareRound(scheduleLoadout: false);
        }

        if (_config.ApplyLoadouts)
        {
            ApplyLoadoutsForPendingAssignments();
        }

        StartReplaySessions();
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        StopAllNativeReplays();
        ClearRetakeMoveTos(releaseBots: true);
        _sessions.Clear();
        _pendingAssignments.Clear();
        _preparedOpeningSessions.Clear();
        _nativeReplayPreloadKeys.Clear();
        _loadoutAppliedKeys.Clear();
        _roundLoadoutBudgets.Clear();
        _lastHurtTime.Clear();
        ClearNativeWeaponState();
        _roundPrepared = false;
        _freezeEnded = false;
        _bombPlantTime = -1f;
        _bombDetonationTime = -1f;
        _bombPos = null;
        return HookResult.Continue;
    }

    private void ScheduleFreezePrepareAttempts()
    {
        var firstDelay = Math.Max(0f, _config.MatchSelectionDelay);
        float[] extraDelays = [0.75f, 1.5f, 2.25f, 3.0f];

        AddTimer(firstDelay, () => PrepareRound());
        foreach (var extraDelay in extraDelays)
        {
            AddTimer(firstDelay + extraDelay, () =>
            {
                if (_freezeEnded || _loadoutAppliedKeys.Count > 0 || AssignmentsCoverCurrentBots())
                {
                    return;
                }

                PrepareRound();
            });
        }
    }

    private HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        // End any opening sessions still running -- the execute phase is over.
        for (var sessionIndex = _sessions.Count - 1; sessionIndex >= 0; sessionIndex--)
        {
            EndSession(sessionIndex, "planted");
        }

        // Capture bomb state for retake end-conditions.
        _bombPlantTime = Server.CurrentTime;
        var c4 = Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4").FirstOrDefault();
        if (c4 != null && c4.IsValid)
        {
            _bombPos = c4.AbsOrigin == null ? null : new Vector(c4.AbsOrigin.X, c4.AbsOrigin.Y, c4.AbsOrigin.Z);
            // CPlantedC4 exposes m_flTimerLength via TimerLength schema; fall back to mp_c4timer default
            // when the schema lookup fails for any reason.
            float timerLen;
            try { timerLen = c4.TimerLength > 0 ? c4.TimerLength : DefaultBombTimerSeconds; }
            catch { timerLen = DefaultBombTimerSeconds; }
            _bombDetonationTime = _bombPlantTime + timerLen;
        }
        else
        {
            // Fallback: use planter pos.
            var planter = @event.Userid;
            var planterPawn = planter?.PlayerPawn?.Value;
            if (planterPawn != null && planterPawn.AbsOrigin != null)
            {
                _bombPos = new Vector(planterPawn.AbsOrigin.X, planterPawn.AbsOrigin.Y, planterPawn.AbsOrigin.Z);
            }
            _bombDetonationTime = _bombPlantTime + DefaultBombTimerSeconds;
        }

        StartRetakeSessions();
        return HookResult.Continue;
    }

    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        // Strict definition of "taking damage": only count damage dealt by a live enemy player.
        // Skip fall damage / world damage (attacker null or == victim) and friendly fire (same team).
        var victim = @event.Userid;
        var attacker = @event.Attacker;
        if (victim == null || !victim.IsValid)
        {
            return HookResult.Continue;
        }
        if (attacker == null || !attacker.IsValid || attacker == victim)
        {
            return HookResult.Continue;
        }
        if (attacker.Team == victim.Team)
        {
            return HookResult.Continue;
        }

        _lastHurtTime[PlayerKey(victim)] = Server.CurrentTime;
        return HookResult.Continue;
    }

    private HookResult OnPlayerBlind(EventPlayerBlind @event, GameEventInfo info)
    {
        // Intentionally a no-op now. Strict end conditions: only "saw an enemy" or "hit by an enemy" end
        // a session. Being flashed does not end the replay -- pros routinely run through their own
        // pop flashes, and we don't want yellow "flashed" hand-offs polluting the log.
        return HookResult.Continue;
    }

    private HookResult OnPlayerFootstep(EventPlayerFootstep @event, GameEventInfo info)
    {
        TryEndReplayOnEnemySound(@event.Userid);
        return HookResult.Continue;
    }

    private HookResult OnPlayerSound(EventPlayerSound @event, GameEventInfo info)
    {
        TryEndReplayOnEnemySound(@event.Userid);
        return HookResult.Continue;
    }

    // Close-range threshold: only end replay when enemy is within this distance.
    // Farther sounds still register in bot's AI perception (IgnoreEnemiesTimer cleared after 20s)
    // but don't interrupt the replay route.
    private const float SoundEndReplayRange = 600f;

    /// <summary>
    /// After 20s of replay, if a replaying bot hears an enemy sound at close range, end the
    /// replay session so the bot's AI takes over for combat.
    /// </summary>
    private void TryEndReplayOnEnemySound(CCSPlayerController? soundSource)
    {
        if (soundSource == null || !soundSource.IsValid || !soundSource.PawnIsAlive) return;
        var sourcePawn = soundSource.PlayerPawn.Value;
        if (sourcePawn?.AbsOrigin == null) return;

        var rangeSq = SoundEndReplayRange * SoundEndReplayRange;
        for (var i = _sessions.Count - 1; i >= 0; i--)
        {
            var session = _sessions[i];
            if (session.Kind != ReplaySessionKind.Opening) continue;

            var elapsed = Server.CurrentTime - session.StartTime;
            if (elapsed < 20f) continue;

            // Must be enemy of the replaying bot
            if (session.Player.Team == soundSource.Team) continue;

            var botPawn = session.Player.PlayerPawn.Value;
            if (botPawn?.AbsOrigin == null) continue;

            var dx = botPawn.AbsOrigin.X - sourcePawn.AbsOrigin.X;
            var dy = botPawn.AbsOrigin.Y - sourcePawn.AbsOrigin.Y;
            var dz = botPawn.AbsOrigin.Z - sourcePawn.AbsOrigin.Z;
            if (dx * dx + dy * dy + dz * dz <= rangeSq)
            {
                EndSession(i, "heard_enemy");
            }
        }
    }

    private void PrepareRound(bool scheduleLoadout = true)
    {
        if (!CanUseDataset() || _dataset == null)
        {
            return;
        }

        if (_loadoutAppliedKeys.Count == 0)
        {
            CaptureRoundLoadoutBudgets();
        }

        var playersByTeam = Utilities.GetPlayers()
            .Where(IsUsableBot)
            .GroupBy(player => player.Team)
            .ToDictionary(group => group.Key, group => group.OrderBy(PlayerKey).ToList());

        _pendingAssignments.Clear();

        foreach (var team in new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist })
        {
            if (!playersByTeam.TryGetValue(team, out var bots) || bots.Count == 0)
            {
                continue;
            }

            var assignments = BuildAssignmentsForTeam(team, bots);
            foreach (var botAssignment in assignments)
            {
                var assignment = new ReplayAssignment(botAssignment.Round, botAssignment.Player, botAssignment.Budget);
                _pendingAssignments[PlayerKey(botAssignment.Bot)] = assignment;
            }
        }

        _roundPrepared = _pendingAssignments.Count > 0;
        PrepareOpeningSessionsForPendingAssignments();

        // Apply the target loadout shortly after matching. MatchSelectionDelay is intentionally after
        // BotBuy's buy/drop timers, so the budget snapshot includes default buy behavior.
        if (scheduleLoadout && _roundPrepared && _config.ApplyLoadouts)
        {
            var delay = Math.Max(0f, _config.LoadoutApplyDelay);
            if (delay <= 0f)
            {
                ApplyLoadoutsForPendingAssignments();
            }
            else
            {
                AddTimer(delay, ApplyLoadoutsForPendingAssignments);
            }
        }
        else if (_roundPrepared && !_config.ApplyLoadouts)
        {
            PreloadPreparedOpeningReplayWeapons();
        }
    }

    private bool AssignmentsCoverCurrentBots()
    {
        var keys = Utilities.GetPlayers()
            .Where(IsUsableBot)
            .Select(PlayerKey)
            .ToList();

        return keys.Count > 0 && keys.All(key => _pendingAssignments.ContainsKey(key));
    }

    private void ApplyLoadoutsForPendingAssignments()
    {
        if (_pendingAssignments.Count == 0)
        {
            return;
        }

        var assignmentsByTeam = new Dictionary<CsTeam, List<BotReplayAssignment>>();
        foreach (var player in Utilities.GetPlayers().Where(IsUsableBot))
        {
            var key = PlayerKey(player);
            if (_loadoutAppliedKeys.Contains(key))
            {
                continue;
            }

            if (!_pendingAssignments.TryGetValue(key, out var assignment))
            {
                continue;
            }

            if (!assignmentsByTeam.TryGetValue(player.Team, out var list))
            {
                list = [];
                assignmentsByTeam[player.Team] = list;
            }

            list.Add(new BotReplayAssignment(player, assignment.Round, assignment.Player, assignment.Budget));
        }

        var appliedAny = false;
        foreach (var (_, assignments) in assignmentsByTeam)
        {
            appliedAny |= ApplyTeamLoadouts(assignments) > 0;
        }

        if (appliedAny)
        {
            RemoveUnownedReplayWeapons();
        }

        PreloadPreparedOpeningReplayWeapons();
    }

    private void CaptureRoundLoadoutBudgets()
    {
        _roundLoadoutBudgets.Clear();
        var budgetOwners = Utilities.GetPlayers()
            .Where(IsRoundBudgetOwner)
            .ToList();

        foreach (var player in budgetOwners.Where(player => player.IsBot))
        {
            var money = player.InGameMoneyServices?.Account ?? 0;
            _roundLoadoutBudgets[PlayerKey(player)] = RoundMoneyDown(money + EstimateCurrentBudgetEquipment(player).TotalValue);
        }

        AddNearestGroundWeaponBudgets(budgetOwners);
    }

    private void AddNearestGroundWeaponBudgets(List<CCSPlayerController> budgetOwners)
    {
        if (budgetOwners.Count == 0)
        {
            return;
        }

        foreach (var itemName in ItemPrices.Keys.Where(name => name.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase)))
        {
            if (!IsBudgetWeapon(itemName))
            {
                continue;
            }

            foreach (var weapon in Utilities.FindAllEntitiesByDesignerName<CBasePlayerWeapon>(itemName))
            {
                if (weapon == null || !weapon.IsValid || weapon.OwnerEntity.IsValid || weapon.AbsOrigin == null)
                {
                    continue;
                }

                var nearest = FindNearestBudgetOwner(weapon.AbsOrigin, budgetOwners);
                if (nearest is not { IsValid: true, IsBot: true })
                {
                    continue;
                }

                var key = PlayerKey(nearest);
                if (!_roundLoadoutBudgets.ContainsKey(key))
                {
                    continue;
                }

                _roundLoadoutBudgets[key] = RoundMoneyDown(_roundLoadoutBudgets[key] + BudgetItemValue(itemName));
            }
        }
    }

    private static CCSPlayerController? FindNearestBudgetOwner(Vector origin, List<CCSPlayerController> players)
    {
        CCSPlayerController? nearest = null;
        var bestDistance = float.MaxValue;
        foreach (var player in players)
        {
            var pawnOrigin = player.PlayerPawn.Value?.AbsOrigin;
            if (pawnOrigin == null)
            {
                continue;
            }

            var distance = DistanceSquared(origin, pawnOrigin);
            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            nearest = player;
        }

        return nearest;
    }

    private List<BotReplayAssignment> BuildAssignmentsForTeam(CsTeam team, List<CCSPlayerController> bots)
    {
        if (!_spawnIndexes.TryGetValue(team, out var index))
        {
            return [];
        }

        var botSpawns = GetBotSpawns(bots);
        if (botSpawns.Count != bots.Count)
        {
            return [];
        }

        var humanSpawns = GetHumanOccupiedSpawns();
        var currentEconomy = GetCurrentTeamEconomy(bots);
        var currentIsPistolRound = IsPistolRoundEconomy(currentEconomy);
        var assignments = index.SelectTeamAssignments(
            botSpawns,
            humanSpawns,
            _config.HumanSpawnBlockRadius,
            _config.EnforcePistolRoundMatching,
            currentIsPistolRound,
            _random);

        return assignments ?? [];
    }

    private static bool IsPistolRoundEconomy(TeamEconomyState state)
    {
        // Pistol round signature: starting balance ~$800/player, no primary weapons, no armor pre-bought.
        // We check the per-bot averages so this works whether we read mid-buy or post-buy.
        if (state.PlayerCount == 0) return false;
        return state.AverageCash + (state.TotalEquipmentValue / state.PlayerCount) <= 1100
            && state.TotalPrimaryValue == 0;
    }

    private List<BotSpawn> GetBotSpawns(List<CCSPlayerController> bots)
    {
        var botSpawns = new List<BotSpawn>(bots.Count);
        foreach (var bot in bots)
        {
            var origin = bot.PlayerPawn.Value?.AbsOrigin;
            if (origin == null)
            {
                continue;
            }

            var budget = RoundMoneyDown(_roundLoadoutBudgets.GetValueOrDefault(PlayerKey(bot), bot.InGameMoneyServices?.Account ?? 0));
            botSpawns.Add(new BotSpawn(bot, new SpawnPosition(origin.X, origin.Y, origin.Z), budget));
        }

        return botSpawns;
    }

    private static List<SpawnPosition> GetHumanOccupiedSpawns()
    {
        return Utilities.GetPlayers()
            .Where(player => player.IsValid
                && !player.IsBot
                && !player.IsHLTV
                && player.PawnIsAlive
                && (player.Team == CsTeam.Terrorist || player.Team == CsTeam.CounterTerrorist)
                && player.PlayerPawn.Value is { IsValid: true }
                && player.PlayerPawn.Value.AbsOrigin != null)
            .Select(player =>
            {
                var origin = player.PlayerPawn.Value!.AbsOrigin!;
                return new SpawnPosition(origin.X, origin.Y, origin.Z);
            })
            .ToList();
    }

    private void StartReplaySessions()
    {
        StopAllNativeReplays();
        _sessions.Clear();
        var startTime = Server.CurrentTime;

        foreach (var player in Utilities.GetPlayers().Where(IsUsableBot))
        {
            var key = PlayerKey(player);
            if (!_pendingAssignments.TryGetValue(key, out var assignment))
            {
                continue;
            }

            var prepared = _preparedOpeningSessions.TryGetValue(key, out var preparedSession)
                && IsPreparedForAssignment(preparedSession, assignment)
                ? preparedSession
                : null;
            var frames = prepared?.Frames ?? BuildSessionFrames(assignment.Player, ReplaySessionKind.Opening);

            if (frames.Count == 0)
            {
                continue;
            }

            var grenades = prepared?.Grenades ?? BuildSessionGrenades(assignment.Player, ReplaySessionKind.Opening);

            var session = new ReplaySession(player, assignment.Round, assignment.Player, frames, grenades, startTime);
            if (prepared != null)
            {
                session.NativeReplayPreloaded = prepared.NativeReplayPreloaded;
                session.ReplayWeaponsPreloaded = prepared.ReplayWeaponsPreloaded;
            }
            if (!TryStartNativeReplay(session))
            {
                continue;
            }
            ApplyReplaySideEffects(session);

            // Signal to other plugins (NadeSystem, BotState) that this bot is under replay control.
            // Written once per session start (not per tick) to avoid the client crash.
            var botCtrl = player.PlayerPawn.Value?.Bot;
            if (botCtrl != null)
                botCtrl.InhibitLookAroundTimestamp = startTime + 130f;

            _sessions.Add(session);
        }

        // Radio callouts: announce T-side opening target to teammates
        if (_sessions.Count > 0 && _config.RadioCallouts)
        {
            AnnounceOpeningRadio();
        }
    }

    private static bool IsPreparedForAssignment(PreparedOpeningSession prepared, ReplayAssignment assignment)
        => ReferenceEquals(prepared.Round, assignment.Round)
            && ReferenceEquals(prepared.ReplayPlayer, assignment.Player);

    private void StartRetakeSessions()
    {
        if (!_config.Enabled || _dataset == null || _dataset.Rounds.Count == 0)
        {
            return;
        }
        if (!CanUseDataset())
        {
            return;
        }

        ClearRetakeMoveTos(releaseBots: false);
        var startTime = Server.CurrentTime;
        var startedCount = 0;

        // Use the candidate pools precomputed in BuildRoundIndexes (when the dataset loaded).
        // Building them per-plant scanned every round + every player's full frame list and caused a
        // multi-100ms hitch on the main thread right when the round transitioned into post-plant.
        var ctCandidatesAll = _ctRetakeCandidates;
        var tCandidatesAll = _tRetakeCandidates;

        // Restrict the candidate pools to rounds where the pro plant happened at the SAME bombsite
        // as the current live plant. Uses dataset-derived centroids (k-means on PlantPos) to classify
        // each candidate and the live bomb into site clusters. This works for vertically-stacked sites
        // like de_nuke where simple distance thresholds fail.
        List<RetakeCandidate> ctCandidates;
        List<RetakeCandidate> tCandidates;
        var currentSiteIndex = ClassifyBySiteCentroids(_bombPos);
        if (currentSiteIndex >= 0 && _datasetSiteCentroids.Count >= 2)
        {
            ctCandidates = new List<RetakeCandidate>(ctCandidatesAll.Count);
            tCandidates = new List<RetakeCandidate>(tCandidatesAll.Count);
            foreach (var c in ctCandidatesAll)
            {
                if (ClassifyCandidateByCentroids(c) == currentSiteIndex) ctCandidates.Add(c);
            }
            foreach (var c in tCandidatesAll)
            {
                if (ClassifyCandidateByCentroids(c) == currentSiteIndex) tCandidates.Add(c);
            }
            // If site-specific filtering yields nothing (e.g. dataset doesn't have PlantPos for
            // this site), fall back to the full pool rather than skipping retake entirely.
            if (ctCandidates.Count == 0 && tCandidates.Count == 0)
            {
                ctCandidates = ctCandidatesAll;
                tCandidates = tCandidatesAll;
            }
        }
        else
        {
            ctCandidates = ctCandidatesAll;
            tCandidates = tCandidatesAll;
        }
        if (ctCandidates.Count == 0 && tCandidates.Count == 0)
        {
            return;
        }

        // Track which (Round, ProPlayer) candidates have already been handed out so two bots near
        // each other don't both pick the same pro path and end up walking in lockstep. We dedup at
        // the (Round, ProPlayer) level rather than the candidate-instance level just to be safe;
        // each pro player only contributes one candidate per round so they're equivalent here.
        var usedCandidates = new HashSet<(string, string)>();

        // Each bot picks the closest *unused* same-team candidate. Greedy nearest-neighbor: the bots\n        // we encounter first get the best matches, but with thousands of candidates per side that's\n        // rarely a meaningful difference.
        foreach (var player in Utilities.GetPlayers().Where(IsUsableBot))
        {
            var pawn = player.PlayerPawn.Value;
            if (pawn == null || pawn.AbsOrigin == null) continue;
            var pool = player.Team == CsTeam.CounterTerrorist ? ctCandidates : tCandidates;
            if (pool.Count == 0)
            {
                continue;
            }

            RetakeCandidate? best = null;
            float bestDistSq = float.MaxValue;
            foreach (var candidate in pool)
            {
                var key = (candidate.Round.Id, candidate.ProPlayer.SteamId);
                if (usedCandidates.Contains(key)) continue;
                var dx = candidate.StartFrame.X - pawn.AbsOrigin.X;
                var dy = candidate.StartFrame.Y - pawn.AbsOrigin.Y;
                var dz = candidate.StartFrame.Z - pawn.AbsOrigin.Z;
                var distSq = dx * dx + dy * dy + dz * dz;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    best = candidate;
                }
            }
            if (best == null) continue;
            usedCandidates.Add((best.Round.Id, best.ProPlayer.SteamId));

            var frames = BuildSessionFrames(best.ProPlayer, ReplaySessionKind.Retake);
            if (frames.Count == 0)
            {
                continue;
            }

            var target = new Vector(best.StartFrame.X, best.StartFrame.Y, best.StartFrame.Z);
            var moveTo = new RetakeMoveToSession(player, best.Round, best.ProPlayer, frames, target, startTime);
            if (IsAtRetakeMoveTarget(player, moveTo))
            {
                if (StartRetakeReplayFromMoveTo(moveTo))
                {
                    startedCount++;
                }
                continue;
            }

            if (!TryIssueRetakeMoveTo(moveTo))
            {
                continue;
            }
            _retakeMoveTos.Add(moveTo);
            startedCount++;
        }

        // Radio callouts for retake
        if (startedCount > 0 && _config.RadioCallouts)
        {
            AnnounceRetakeRadio(currentSiteIndex);
        }
    }

    private static List<ReplayFrame> BuildSessionFrames(ReplayPlayer player, ReplaySessionKind kind)
    {
        var start = kind == ReplaySessionKind.Retake ? player.RetakeStartFrame : player.StartFrame;
        var end = kind == ReplaySessionKind.Retake ? player.RetakeEndFrame : player.EndFrame;
        var duration = kind == ReplaySessionKind.Retake ? player.RetakeDuration : player.Duration;
        if (start == null || string.IsNullOrWhiteSpace(ReplayPathForKind(player, kind)))
        {
            return [];
        }

        var first = start.CloneAtTime(0f);
        var frames = new List<ReplayFrame> { first };
        if (end != null && duration > 0.001f)
        {
            frames.Add(end.CloneAtTime(duration));
        }
        return frames;
    }

    private static string ReplayPathForKind(ReplayPlayer player, ReplaySessionKind kind)
    {
        if (kind == ReplaySessionKind.Retake && !string.IsNullOrWhiteSpace(player.RetakeRecPath))
        {
            return player.RetakeRecPath;
        }
        return player.RecPath;
    }

    private static string ReplayKeyForKind(ReplayPlayer player, ReplaySessionKind kind)
    {
        if (kind == ReplaySessionKind.Retake && !string.IsNullOrWhiteSpace(player.RetakeRecKey))
        {
            return player.RetakeRecKey;
        }

        return player.RecKey;
    }

    private static List<ReplayGrenade> BuildSessionGrenades(ReplayPlayer player, ReplaySessionKind kind)
    {
        if (kind != ReplaySessionKind.Retake)
        {
            return player.Grenades.ToList();
        }

        var startTime = Math.Max(0f, player.RetakeStartTime > 0.001f ? player.RetakeStartTime : player.RetakeStartFrame?.Time ?? 0f);
        var startTick = player.RetakeStartRelativeTick != 0 ? player.RetakeStartRelativeTick : player.RetakeStartFrame?.RelativeTick ?? 0;
        var endTime = player.RetakeDuration > 0.001f ? startTime + player.RetakeDuration : float.MaxValue;
        return player.Grenades
            .Where(grenade => grenade.Time + 0.001f >= startTime && grenade.Time <= endTime + 0.001f)
            .Select(grenade => CloneGrenadeAtSessionTime(grenade, startTime, startTick))
            .OrderBy(grenade => grenade.Time)
            .ToList();
    }

    private static ReplayGrenade CloneGrenadeAtSessionTime(ReplayGrenade grenade, float timeOffset, int tickOffset)
    {
        return new ReplayGrenade
        {
            RelativeTick = Math.Max(0, grenade.RelativeTick - tickOffset),
            Time = Math.Max(0f, grenade.Time - timeOffset),
            Type = grenade.Type,
            X = grenade.X,
            Y = grenade.Y,
            Z = grenade.Z,
            Pitch = grenade.Pitch,
            Yaw = grenade.Yaw,
            VelocityX = grenade.VelocityX,
            VelocityY = grenade.VelocityY,
            VelocityZ = grenade.VelocityZ
        };
    }

    private sealed record RetakeCandidate(ReplayRound Round, ReplayPlayer ProPlayer, ReplayFrame StartFrame);

    // ═══════════════════════════════════════════════════════════
    //  Radio callouts — announce bot intentions to human players
    // ═══════════════════════════════════════════════════════════

    private static readonly string[] OpeningCallsA = ["Going A", "Rush A", "Execute A", "Heading A"];
    private static readonly string[] OpeningCallsB = ["Going B", "Rush B", "Execute B", "Heading B"];
    private static readonly string[] OpeningCallsGeneric = ["Let's go", "Move out", "Go go go"];
    private static readonly string[] RetakeCalls = ["Retake!", "Go go go!", "Let's retake", "Push together"];
    private static readonly string[] HoldCalls = ["Hold position", "Stay alive", "Play time"];

    private void AnnounceOpeningRadio()
    {
        // Pick one T-side session to announce the opening target.
        var tSession = _sessions.FirstOrDefault(s => s.Player.Team == CsTeam.Terrorist);
        if (tSession == null) return;

        // Determine destination by looking at the last frame of the replay
        var lastFrame = tSession.Frames.Count > 0 ? tSession.Frames[^1] : null;
        string[] callPool;
        if (lastFrame != null && _datasetSiteCentroids.Count >= 2)
        {
            var destPos = new Vector(lastFrame.X, lastFrame.Y, lastFrame.Z);
            var siteIdx = ClassifyBySiteCentroids(destPos);
            callPool = siteIdx == 0 ? OpeningCallsA : OpeningCallsB;
        }
        else
        {
            callPool = OpeningCallsGeneric;
        }

        var call = callPool[_random.Next(callPool.Length)];
        var botTeam = tSession.Player.Team;
        var teamColor = botTeam == CsTeam.Terrorist ? ChatColors.Yellow : ChatColors.Blue;
        var botName = tSession.PlayerName;
        Server.NextFrame(() =>
        {
            foreach (var player in Utilities.GetPlayers())
            {
                if (!player.IsValid || player.IsBot || player.Team != botTeam) continue;
                player.PrintToChat($" {teamColor}☆ {botName}{ChatColors.Default}: {call}");
            }
        });
    }

    private void AnnounceRetakeRadio(int siteIndex)
    {
        // CT: announce retake push
        var ctSession = _sessions.FirstOrDefault(s =>
            s.Kind == ReplaySessionKind.Retake && s.Player.Team == CsTeam.CounterTerrorist);

        // T: announce hold on site
        var tSession = _sessions.FirstOrDefault(s =>
            s.Kind == ReplaySessionKind.Retake && s.Player.Team == CsTeam.Terrorist);

        var siteName = siteIndex == 0 ? "A" : "B";
        Server.NextFrame(() =>
        {
            var players = Utilities.GetPlayers().Where(p => p.IsValid && !p.IsHLTV && !p.IsBot).ToList();
            if (players.Count == 0) return;

            if (ctSession != null)
            {
                var call = RetakeCalls[_random.Next(RetakeCalls.Length)];
                var botName = ctSession.PlayerName;
                foreach (var p in players.Where(p => p.Team == CsTeam.CounterTerrorist))
                    p.PrintToChat($" {ChatColors.Blue}☆ {botName}{ChatColors.Default}: {call} [{siteName}]");
            }
            if (tSession != null)
            {
                var call = HoldCalls[_random.Next(HoldCalls.Length)];
                var botName = tSession.PlayerName;
                foreach (var p in players.Where(p => p.Team == CsTeam.Terrorist))
                    p.PrintToChat($" {ChatColors.Yellow}☆ {botName}{ChatColors.Default}: {call} [{siteName}]");
            }
        });
    }

    // Classify a world position by index of the nearest entry in _bombSiteCenters. Returns -1 if
    // we have no site centers cached or the position is null. Uses full 3D distance to correctly
    // distinguish vertically-stacked sites (e.g. de_nuke A/B share similar XY but differ in Z).
    private int ClassifyPlantSite(Vector? pos)
    {
        if (pos == null || _bombSiteCenters.Count == 0) return -1;
        var bestIndex = -1;
        var bestDistSq = float.MaxValue;
        for (var i = 0; i < _bombSiteCenters.Count; i++)
        {
            var c = _bombSiteCenters[i];
            var dx = c.X - pos.X;
            var dy = c.Y - pos.Y;
            var dz = c.Z - pos.Z;
            var d = dx * dx + dy * dy + dz * dz;
            if (d < bestDistSq) { bestDistSq = d; bestIndex = i; }
        }
        return bestIndex;
    }

    private int CandidatePlantSite(RetakeCandidate c)
    {
        var pp = c.Round.PlantPos;
        if (pp == null || _bombSiteCenters.Count == 0) return -1;
        var bestIndex = -1;
        var bestDistSq = float.MaxValue;
        for (var i = 0; i < _bombSiteCenters.Count; i++)
        {
            var sc = _bombSiteCenters[i];
            var dx = sc.X - pp.X;
            var dy = sc.Y - pp.Y;
            var dz = sc.Z - pp.Z;
            var d = dx * dx + dy * dy + dz * dz;
            if (d < bestDistSq) { bestDistSq = d; bestIndex = i; }
        }
        return bestIndex;
    }

    /// <summary>
    /// Returns true if the candidate's pro plant position is within thresholdSq of the live bomb.
    /// Candidates without PlantPos are excluded (return false) to avoid sending bots to random sites.
    /// </summary>
    private static bool CandidateMatchesBombPos(RetakeCandidate c, Vector bombPos, float thresholdSq)
    {
        var pp = c.Round.PlantPos;
        if (pp == null) return false;
        var dx = pp.X - bombPos.X;
        var dy = pp.Y - bombPos.Y;
        var dz = pp.Z - bombPos.Z;
        return dx * dx + dy * dy + dz * dz < thresholdSq;
    }

    /// <summary>
    /// Classifies a world position by nearest dataset-derived site centroid. Returns -1 if
    /// centroids are not available or pos is null.
    /// </summary>
    private int ClassifyBySiteCentroids(Vector? pos)
    {
        if (pos == null || _datasetSiteCentroids.Count < 2) return -1;
        var bestIndex = -1;
        var bestDistSq = float.MaxValue;
        for (var i = 0; i < _datasetSiteCentroids.Count; i++)
        {
            var c = _datasetSiteCentroids[i];
            var dx = c.X - pos.X;
            var dy = c.Y - pos.Y;
            var dz = c.Z - pos.Z;
            var d = dx * dx + dy * dy + dz * dz;
            if (d < bestDistSq) { bestDistSq = d; bestIndex = i; }
        }
        return bestIndex;
    }

    /// <summary>
    /// Classifies a retake candidate's PlantPos by nearest dataset-derived site centroid.
    /// Returns -1 if the candidate has no PlantPos or centroids aren't available.
    /// </summary>
    private int ClassifyCandidateByCentroids(RetakeCandidate c)
    {
        var pp = c.Round.PlantPos;
        if (pp == null || _datasetSiteCentroids.Count < 2) return -1;
        var bestIndex = -1;
        var bestDistSq = float.MaxValue;
        for (var i = 0; i < _datasetSiteCentroids.Count; i++)
        {
            var sc = _datasetSiteCentroids[i];
            var dx = sc.X - pp.X;
            var dy = sc.Y - pp.Y;
            var dz = sc.Z - pp.Z;
            var d = dx * dx + dy * dy + dz * dz;
            if (d < bestDistSq) { bestDistSq = d; bestIndex = i; }
        }
        return bestIndex;
    }

    private void OnTick()
    {
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

            ApplyReplaySideEffects(session);
        }
    }

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

            if (ShouldHandOff(moveTo.Player, allPlayersThisTick))
            {
                EndRetakeMoveTo(i);
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

    private void EndRetakeMoveTo(int index)
    {
        var moveTo = _retakeMoveTos[index];
        _retakeMoveTos.RemoveAt(index);
        ReleaseBotToNativeAi(moveTo.Player);
    }

    private bool StartRetakeReplayFromMoveTo(RetakeMoveToSession moveTo)
    {
        var startTime = Server.CurrentTime;
        var session = new ReplaySession(
            moveTo.Player, moveTo.Round, moveTo.ReplayPlayer, moveTo.Frames,
            grenades: BuildSessionGrenades(moveTo.ReplayPlayer, ReplaySessionKind.Retake),
            startTime: startTime,
            kind: ReplaySessionKind.Retake);

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
        StopNativeReplay(session);
        ReleaseBotToNativeAi(session.Player);
    }

    private void ReleaseBotToNativeAi(CCSPlayerController player)
    {
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

            PrimeBotForHeardEnemy(listener, sourceOrigin);
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

    private static void PrimeBotForHeardEnemy(CCSPlayerController listener, Vector sourceOrigin)
    {
        var pawn = listener.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
        {
            return;
        }

        AimPawnAtPosition(pawn, sourceOrigin);

        var bot = pawn.Bot;
        if (bot == null)
        {
            return;
        }

        bot.IsSleeping = false;
        bot.AllowActive = true;
        bot.EyeAnglesUnderPathFinderControl = false;
        bot.InhibitLookAroundTimestamp = Server.CurrentTime + 0.5f;
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

    private ReplayRound? SelectRoundForTeam(CsTeam team, List<CCSPlayerController> bots)
    {
        if (_dataset == null || !_roundIndexes.TryGetValue(team, out var index))
        {
            return null;
        }

        return index.SelectClosest(GetCurrentTeamEconomy(bots), _random);
    }

    private static TeamEconomyState GetCurrentTeamEconomy(List<CCSPlayerController> bots)
    {
        var totalCash = 0;
        var totalEquipment = 0;
        var totalPrimary = 0;
        var totalUtility = 0;
        var totalArmor = 0;

        foreach (var bot in bots)
        {
            totalCash += bot.InGameMoneyServices?.Account ?? 0;
            var values = EstimateCurrentEquipment(bot);
            totalEquipment += values.TotalValue;
            totalPrimary += values.PrimaryValue;
            totalUtility += values.UtilityValue;
            totalArmor += values.ArmorValue;
        }

        return new TeamEconomyState(
            bots.Count,
            totalCash,
            bots.Count == 0 ? 0 : totalCash / bots.Count,
            totalEquipment,
            totalPrimary,
            totalUtility,
            totalArmor);
    }

    private void PrepareOpeningSessionsForPendingAssignments()
    {
        _preparedOpeningSessions.Clear();
        if (_pendingAssignments.Count == 0)
        {
            return;
        }

        foreach (var player in Utilities.GetPlayers().Where(IsUsableBot))
        {
            if (_pendingAssignments.TryGetValue(PlayerKey(player), out var assignment))
            {
                var frames = BuildSessionFrames(assignment.Player, ReplaySessionKind.Opening);
                if (frames.Count == 0)
                {
                    continue;
                }

                var grenades = BuildSessionGrenades(assignment.Player, ReplaySessionKind.Opening);
                var nativePreloaded = _nativeReplayAvailable
                    && PreloadNativeReplayBuffer(player, assignment.Player, ReplaySessionKind.Opening);
                _preparedOpeningSessions[PlayerKey(player)] = new PreparedOpeningSession(
                    assignment.Round,
                    assignment.Player,
                    frames,
                    grenades,
                    nativePreloaded);
            }
        }
    }

    private void PreloadPreparedOpeningReplayWeapons()
    {
        if (_preparedOpeningSessions.Count == 0)
        {
            return;
        }

        foreach (var player in Utilities.GetPlayers().Where(IsUsableBot))
        {
            var key = PlayerKey(player);
            if (!_preparedOpeningSessions.TryGetValue(key, out var prepared)
                || prepared.ReplayWeaponsPreloaded)
            {
                continue;
            }

            prepared.ReplayWeaponsPreloaded = PreloadReplayWeapons(
                player,
                prepared.ReplayPlayer,
                prepared.Frames,
                ReplaySessionKind.Opening);
        }
    }

    private bool PreloadNativeReplayBuffer(CCSPlayerController player, ReplayPlayer replayPlayer, ReplaySessionKind kind)
    {
        var slot = player.Slot;
        if (slot < 0 || slot >= 64)
        {
            return false;
        }

        var replayPath = ResolveReplayPath(replayPlayer, kind);
        if (string.IsNullOrWhiteSpace(replayPath) || !File.Exists(replayPath))
        {
            _nativeReplayPreloadKeys.Remove(slot);
            return false;
        }

        var startTick = kind == ReplaySessionKind.Retake
            ? Math.Max(0, replayPlayer.RetakeStartTickIndex)
            : 0;
        var replayKey = ReplayKeyForKind(replayPlayer, kind);
        var loadKey = NativeReplayLoadKey(replayPath, startTick, replayKey, _config.SuppressReplayAttackInput);
        if (_nativeReplayPreloadKeys.TryGetValue(slot, out var existing) && existing == loadKey)
        {
            return true;
        }

        if (!BotController.LoadReplayFromFile(slot, replayPath, startTick, _config.SuppressReplayAttackInput, replayKey))
        {
            _nativeReplayPreloadKeys.Remove(slot);
            return false;
        }

        _nativeReplayPreloadKeys[slot] = loadKey;
        return true;
    }

    private static string NativeReplayLoadKey(string replayPath, int startTick, string replayKey, bool suppressAttackInput)
        => $"{Path.GetFullPath(replayPath)}\n{startTick}\n{replayKey}\n{suppressAttackInput}";

    private bool TryStartNativeReplay(ReplaySession session)
    {
        if (!_nativeReplayAvailable)
        {
            return false;
        }

        var slot = session.Player.Slot;
        if (slot < 0 || slot >= 64)
        {
            return false;
        }

        if (!session.ReplayWeaponsPreloaded)
        {
            session.ReplayWeaponsPreloaded = PreloadReplayWeapons(
                session.Player,
                session.ReplayPlayer,
                session.Frames,
                session.Kind);
        }

        if (!session.NativeReplayPreloaded
            && !PreloadNativeReplayBuffer(session.Player, session.ReplayPlayer, session.Kind))
        {
            return false;
        }
        session.NativeReplayPreloaded = true;

        if (!BotController.StartReplay(slot))
        {
            return false;
        }

        session.NativeReplayActive = true;
        session.NativeReplaySlot = slot;
        session.NativeReplayTickCount = BotController.GetReplayTotal(slot);
        session.NativeReplayLastCursor = -1;
        session.NativeReplayStallTicks = 0;
        session.NativeReplayDiagnosticLogged = false;
        ApplyReplayWeaponPreset(session, ChooseStartWeaponDef(session), allowSlotReplacement: true, force: true);
        return true;
    }

    private string ResolveReplayPath(ReplaySession session)
        => ResolveReplayPath(session.ReplayPlayer, session.Kind);

    private string ResolveReplayPath(ReplayPlayer replayPlayer, ReplaySessionKind kind)
    {
        var relativeOrAbsolute = ReplayPathForKind(replayPlayer, kind);
        if (string.IsNullOrWhiteSpace(relativeOrAbsolute))
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(relativeOrAbsolute))
        {
            return relativeOrAbsolute;
        }

        var baseDirectory = string.IsNullOrWhiteSpace(_dataset?.BaseDirectory)
            ? ModuleDirectory
            : _dataset.BaseDirectory;
        return Path.GetFullPath(Path.Combine(baseDirectory, relativeOrAbsolute.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static int WeaponDefIndex(string activeWeapon)
    {
        var itemName = NormalizeGrenadeType(activeWeapon);
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return -1;
        }

        if (WeaponDefIndexes.TryGetValue(itemName, out var defIndex))
        {
            return defIndex;
        }

        return itemName.Contains("knife", StringComparison.OrdinalIgnoreCase)
            || itemName.Contains("bayonet", StringComparison.OrdinalIgnoreCase)
            ? 42
            : -1;
    }

    private static int WeaponDefIndex(ReplayFrame frame)
    {
        if (frame.ActiveWeaponDefIndex.HasValue)
        {
            return NormalizeWeaponDefIndex(frame.ActiveWeaponDefIndex.Value);
        }
        return WeaponDefIndex(frame.ActiveWeapon);
    }

    private bool PreloadReplayWeapons(
        CCSPlayerController player,
        ReplayPlayer replayPlayer,
        List<ReplayFrame> frames,
        ReplaySessionKind kind)
    {
        var slot = player.Slot;
        if (slot < 0)
        {
            return false;
        }

        foreach (var defIndex in ReplayWeaponDefs(replayPlayer, frames))
        {
            var normalized = NormalizeWeaponDefIndex(defIndex);
            if (!ShouldApplyReplayWeaponForSession(kind, normalized)
                || !IsPreloadWeaponDefIndex(normalized)
                || !_preloadedReplayWeapons.Add((slot, normalized)))
            {
                continue;
            }

            EnsureReplayWeaponForSlot(
                slot,
                normalized,
                forceSwitch: false,
                allowGive: true,
                replaceConflictingSlot: false);
        }

        return true;
    }

    private static IEnumerable<int> ReplayWeaponDefs(ReplaySession session)
        => ReplayWeaponDefs(session.ReplayPlayer, session.Frames);

    private static IEnumerable<int> ReplayWeaponDefs(ReplayPlayer replayPlayer, List<ReplayFrame> frames)
    {
        foreach (var defIndex in ReplayPlayerWeaponDefs(replayPlayer))
        {
            yield return defIndex;
        }

        foreach (var frame in frames)
        {
            yield return WeaponDefIndex(frame);
        }
    }

    private static int ChooseStartWeaponDef(ReplaySession session)
    {
        if (session.Kind == ReplaySessionKind.Retake)
        {
            if (session.Frames.Count == 0)
            {
                return -1;
            }

            var firstFrameDef = NormalizeWeaponDefIndex(WeaponDefIndex(session.Frames[0]));
            return ShouldApplyReplayWeaponForSession(session, firstFrameDef) ? firstFrameDef : -1;
        }

        var first = NormalizeWeaponDefIndex(session.ReplayPlayer.FirstWeaponDefIndex);
        if (IsKnownWeaponDefIndex(first) && GetReplayLockTarget(first) != LockTarget.Slot5)
        {
            return first;
        }

        foreach (var frame in session.Frames)
        {
            var defIndex = WeaponDefIndex(frame);
            if (IsKnownWeaponDefIndex(defIndex) && GetReplayLockTarget(defIndex) != LockTarget.Slot5)
            {
                return NormalizeWeaponDefIndex(defIndex);
            }
        }

        foreach (var defIndex in ReplayWeaponDefs(session))
        {
            var normalized = NormalizeWeaponDefIndex(defIndex);
            if (IsKnownWeaponDefIndex(normalized))
            {
                return normalized;
            }
        }

        return -1;
    }

    private bool ApplyReplayWeaponPreset(
        ReplaySession session,
        int weaponDefIndex,
        bool allowSlotReplacement,
        bool force)
    {
        var slot = session.Player.Slot;
        var normalized = NormalizeWeaponDefIndex(weaponDefIndex);
        if (slot < 0 || !IsKnownWeaponDefIndex(normalized))
        {
            return false;
        }

        if (!ShouldApplyReplayWeaponForSession(session, normalized))
        {
            return false;
        }

        if (!force
            && _lastReplayWeaponDef.TryGetValue(slot, out var lastDef)
            && lastDef == normalized)
        {
            return true;
        }

        var target = GetReplayLockTarget(normalized);
        if (target != LockTarget.None
            && (force
                || !_lastLockedWeaponTarget.TryGetValue(slot, out var lastTarget)
                || lastTarget != target))
        {
            if (BotController.Lock(slot, target))
            {
                _lastLockedWeaponTarget[slot] = target;
            }
        }

        if (allowSlotReplacement && IsSlotReplaceableWeaponDef(normalized))
        {
            EnsureReplayWeaponForSlot(
                slot,
                normalized,
                forceSwitch: false,
                allowGive: true,
                replaceConflictingSlot: false);
        }

        var switched = BotController.SwitchBotWeapon(slot, normalized);
        _lastReplayWeaponDef[slot] = normalized;
        return switched;
    }

    private void EnsureReplayWeaponForSlot(
        int slot,
        int weaponDefIndex,
        bool forceSwitch,
        bool allowGive,
        bool replaceConflictingSlot)
    {
        var normalized = NormalizeWeaponDefIndex(weaponDefIndex);
        if (normalized < 0)
        {
            return;
        }

        if (_lastEnsuredWeaponDef.TryGetValue(slot, out var last)
            && last == normalized
            && !forceSwitch)
        {
            return;
        }

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true }
            || player.PlayerPawn is not { IsValid: true, Value.IsValid: true })
        {
            return;
        }

        if (!TryEnsureReplayWeapon(
                player,
                normalized,
                allowGive,
                replaceConflictingSlot,
                out _))
        {
            _lastEnsuredWeaponDef[slot] = normalized;
            return;
        }

        _lastEnsuredWeaponDef[slot] = normalized;
        if (forceSwitch)
        {
            BotController.SwitchBotWeapon(slot, normalized);
        }
    }

    private static IEnumerable<int> ReplayPlayerWeaponDefs(ReplayPlayer replayPlayer)
    {
        foreach (var defIndex in replayPlayer.InventoryDefIndexes)
        {
            yield return defIndex;
        }

        foreach (var defIndex in replayPlayer.PreloadWeaponDefIndexes)
        {
            yield return defIndex;
        }

        yield return replayPlayer.FirstWeaponDefIndex;

        foreach (var item in replayPlayer.Inventory)
        {
            yield return WeaponDefIndex(item);
        }
    }

    private static bool TryEnsureReplayWeapon(
        CCSPlayerController player,
        int weaponDefIndex,
        bool allowGive,
        bool replaceConflictingSlot,
        out string className)
    {
        className = string.Empty;
        if (!TryGetWeaponClassByDefIndex(weaponDefIndex, out className))
        {
            return false;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn is not { IsValid: true })
        {
            return false;
        }

        if (HasReplayWeapon(pawn, className))
        {
            return true;
        }

        var slot = GetReplayWeaponSlot(className);
        if (!allowGive
            || slot is ReplayWeaponSlot.Other or ReplayWeaponSlot.Knife or ReplayWeaponSlot.C4 or ReplayWeaponSlot.Taser)
        {
            return false;
        }

        if (!replaceConflictingSlot && HasConflictingWeaponInSlot(pawn, slot, className))
        {
            return false;
        }

        _ = replaceConflictingSlot;

        try
        {
            player.GiveNamedItem(className);
        }
        catch (Exception ex)
        {
            _ = ex;
            return false;
        }

        return HasReplayWeapon(pawn, className) || slot == ReplayWeaponSlot.Utility;
    }

    private static bool HasReplayWeapon(CCSPlayerPawn pawn, string className)
    {
        if (pawn.WeaponServices == null)
        {
            return false;
        }

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid)
            {
                continue;
            }
            if (WeaponClassMatches(weapon.DesignerName, className))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasConflictingWeaponInSlot(CCSPlayerPawn pawn, ReplayWeaponSlot slot, string expectedClassName)
    {
        if (slot is not (ReplayWeaponSlot.Primary or ReplayWeaponSlot.Secondary) || pawn.WeaponServices == null)
        {
            return false;
        }

        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid)
            {
                continue;
            }
            if (WeaponClassMatches(weapon.DesignerName, expectedClassName))
            {
                continue;
            }
            if (GetReplayWeaponSlot(weapon.DesignerName) == slot)
            {
                return true;
            }
        }

        return false;
    }

    private static bool WeaponClassMatches(string actual, string expected)
    {
        if (actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return expected.Equals("weapon_knife", StringComparison.OrdinalIgnoreCase)
            && (actual.StartsWith("weapon_knife", StringComparison.OrdinalIgnoreCase)
                || actual.Contains("bayonet", StringComparison.OrdinalIgnoreCase));
    }

    private static ReplayWeaponSlot GetReplayWeaponSlot(string className)
    {
        if (className.StartsWith("weapon_knife", StringComparison.OrdinalIgnoreCase)
            || className.Contains("bayonet", StringComparison.OrdinalIgnoreCase))
        {
            return ReplayWeaponSlot.Knife;
        }

        return className switch
        {
            "weapon_ak47" or "weapon_aug" or "weapon_awp" or "weapon_famas" or
            "weapon_g3sg1" or "weapon_galilar" or "weapon_m249" or "weapon_m4a1" or
            "weapon_m4a1_silencer" or "weapon_mac10" or "weapon_p90" or
            "weapon_mp5sd" or "weapon_mp7" or "weapon_mp9" or "weapon_ump45" or
            "weapon_xm1014" or "weapon_bizon" or "weapon_mag7" or "weapon_negev" or
            "weapon_sawedoff" or "weapon_nova" or "weapon_scar20" or "weapon_sg556" or
            "weapon_ssg08" => ReplayWeaponSlot.Primary,

            "weapon_deagle" or "weapon_elite" or "weapon_fiveseven" or "weapon_glock" or
            "weapon_hkp2000" or "weapon_p250" or "weapon_tec9" or "weapon_usp_silencer" or
            "weapon_cz75a" or "weapon_revolver" => ReplayWeaponSlot.Secondary,

            "weapon_flashbang" or "weapon_hegrenade" or "weapon_smokegrenade" or
            "weapon_molotov" or "weapon_decoy" or "weapon_incgrenade" => ReplayWeaponSlot.Utility,

            "weapon_c4" => ReplayWeaponSlot.C4,
            "weapon_taser" => ReplayWeaponSlot.Taser,
            _ => ReplayWeaponSlot.Other
        };
    }

    private static LockTarget GetReplayLockTarget(int weaponDefIndex)
    {
        if (!TryGetWeaponClassByDefIndex(weaponDefIndex, out var className))
        {
            return LockTarget.None;
        }

        return GetReplayWeaponSlot(className) switch
        {
            ReplayWeaponSlot.Primary => LockTarget.Slot1,
            ReplayWeaponSlot.Secondary => LockTarget.Slot2,
            ReplayWeaponSlot.Knife or ReplayWeaponSlot.Taser => LockTarget.Slot3,
            ReplayWeaponSlot.Utility => LockTarget.Slot4,
            ReplayWeaponSlot.C4 => LockTarget.Slot5,
            _ => LockTarget.None
        };
    }

    private static bool IsSlotReplaceableWeaponDef(int weaponDefIndex)
    {
        return TryGetWeaponClassByDefIndex(weaponDefIndex, out var className)
            && GetReplayWeaponSlot(className) is ReplayWeaponSlot.Primary or ReplayWeaponSlot.Secondary;
    }

    private static bool IsPrimaryWeapon(string itemName)
        => PrimaryWeapons.Contains(itemName);

    private static bool IsSecondaryWeapon(string itemName)
        => SecondaryWeapons.Contains(itemName);

    private static bool IsPreloadWeaponDefIndex(int weaponDefIndex)
    {
        if (!TryGetWeaponClassByDefIndex(weaponDefIndex, out var className))
        {
            return false;
        }

        return GetReplayWeaponSlot(className) is not ReplayWeaponSlot.Other
            and not ReplayWeaponSlot.Knife
            and not ReplayWeaponSlot.C4
            and not ReplayWeaponSlot.Taser;
    }

    private static bool ShouldApplyReplayWeaponForSession(ReplaySession session, int weaponDefIndex)
        => ShouldApplyReplayWeaponForSession(session.Kind, weaponDefIndex);

    private static bool ShouldApplyReplayWeaponForSession(ReplaySessionKind kind, int weaponDefIndex)
    {
        if (kind != ReplaySessionKind.Retake)
        {
            return true;
        }

        return TryGetWeaponClassByDefIndex(weaponDefIndex, out var className)
            && GetReplayWeaponSlot(className) == ReplayWeaponSlot.Utility;
    }

    private static bool IsKnownWeaponDefIndex(int weaponDefIndex)
        => TryGetWeaponClassByDefIndex(weaponDefIndex, out _);

    private static int NormalizeWeaponDefIndex(int weaponDefIndex)
    {
        if (weaponDefIndex == 42 || weaponDefIndex == 59 || weaponDefIndex is >= 500 and < 600 || weaponDefIndex == 9001)
        {
            return 42;
        }

        return weaponDefIndex;
    }

    private static bool TryGetWeaponClassByDefIndex(int weaponDefIndex, out string className)
    {
        className = NormalizeWeaponDefIndex(weaponDefIndex) switch
        {
            1 => "weapon_deagle",
            2 => "weapon_elite",
            3 => "weapon_fiveseven",
            4 => "weapon_glock",
            7 => "weapon_ak47",
            8 => "weapon_aug",
            9 => "weapon_awp",
            10 => "weapon_famas",
            11 => "weapon_g3sg1",
            13 => "weapon_galilar",
            14 => "weapon_m249",
            16 => "weapon_m4a1",
            17 => "weapon_mac10",
            19 => "weapon_p90",
            23 => "weapon_mp5sd",
            24 => "weapon_ump45",
            25 => "weapon_xm1014",
            26 => "weapon_bizon",
            27 => "weapon_mag7",
            28 => "weapon_negev",
            29 => "weapon_sawedoff",
            30 => "weapon_tec9",
            31 => "weapon_taser",
            32 => "weapon_hkp2000",
            33 => "weapon_mp7",
            34 => "weapon_mp9",
            35 => "weapon_nova",
            36 => "weapon_p250",
            38 => "weapon_scar20",
            39 => "weapon_sg556",
            40 => "weapon_ssg08",
            42 => "weapon_knife",
            43 => "weapon_flashbang",
            44 => "weapon_hegrenade",
            45 => "weapon_smokegrenade",
            46 => "weapon_molotov",
            47 => "weapon_decoy",
            48 => "weapon_incgrenade",
            49 => "weapon_c4",
            60 => "weapon_m4a1_silencer",
            61 => "weapon_usp_silencer",
            63 => "weapon_cz75a",
            64 => "weapon_revolver",
            _ => string.Empty
        };
        return className.Length > 0;
    }

    private void StopNativeReplay(ReplaySession session)
    {
        if (!session.NativeReplayActive)
        {
            return;
        }

        BotController.StopReplay(session.NativeReplaySlot);
        ClearNativeWeaponState(session.NativeReplaySlot);
        session.NativeReplayActive = false;
        session.NativeReplaySlot = -1;
    }

    private void StopAllNativeReplays()
    {
        foreach (var session in _sessions)
        {
            StopNativeReplay(session);
        }
        ClearNativeWeaponState();
    }

    private void ClearRetakeMoveTos(bool releaseBots)
    {
        if (releaseBots)
        {
            foreach (var moveTo in _retakeMoveTos)
            {
                ReleaseBotToNativeAi(moveTo.Player);
            }
        }
        _retakeMoveTos.Clear();
    }

    private void ClearNativeWeaponState(int slot)
    {
        BotController.Unlock(slot, LockKind.Weapon);
        _lastEnsuredWeaponDef.Remove(slot);
        _lastReplayWeaponDef.Remove(slot);
        _lastLockedWeaponTarget.Remove(slot);
        _preloadedReplayWeapons.RemoveWhere(entry => entry.Slot == slot);
    }

    private void ClearNativeWeaponState()
    {
        foreach (var slot in _lastLockedWeaponTarget.Keys.ToArray())
        {
            BotController.Unlock(slot, LockKind.Weapon);
        }
        _lastEnsuredWeaponDef.Clear();
        _lastReplayWeaponDef.Clear();
        _lastLockedWeaponTarget.Clear();
        _preloadedReplayWeapons.Clear();
    }

    private void ApplyReplaySideEffects(ReplaySession session)
    {
        var allowReplayAttack = false;
        if (session.NativeReplayActive && BotController.TryGetReplayTick(session.NativeReplaySlot, out var tick))
        {
            allowReplayAttack = BotController.IsThrowableUtilityWeaponDef(tick.WeaponDefIndex);
            ApplyReplayWeaponPreset(session, tick.WeaponDefIndex, allowSlotReplacement: true, force: false);
        }

        ApplyReplayControlSideEffects(session.Player, session.StartTime, allowReplayAttack);
    }

    private void ApplyReplayControlSideEffects(CCSPlayerController player, float startTime, bool allowReplayAttack)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
        {
            return;
        }
        var bot = pawn.Bot;
        if (bot == null)
        {
            return;
        }

        // Suppress combat only
        if (_config.SuppressBotEngagementWhileReplaying)
        {
            bot.IsAttacking = false;
            bot.IsRapidFiring = false;
            bot.IsAimingAtEnemy = false;

            if (!allowReplayAttack)
            {
                bot.FireWeaponTimestamp = Server.CurrentTime + 0.5f;

                var ws = pawn.WeaponServices as CCSPlayer_WeaponServices;
                if (ws != null)
                {
                    ws.NextAttack = Server.CurrentTime + 0.5f;
                }
            }

            // Prevent stuck-recovery jumps
            ref bool isStuck = ref bot.IsStuck;
            isStuck = false;
            ref float jumpTimestamp = ref bot.JumpTimestamp;
            jumpTimestamp = Server.CurrentTime + 2.0f;
            CountdownTimer stuckJumpTimer = bot.StuckJumpTimer;
            ref float stuckJumpDur = ref stuckJumpTimer.Duration;
            stuckJumpDur = 2.0f;
            ref float stuckJumpTs = ref stuckJumpTimer.Timestamp;
            stuckJumpTs = Server.CurrentTime + 2.0f;

            if (!_config.KeepBotPerceptionDuringReplay)
            {
                // After 20s of replay, allow perception so bots react to sounds.
                var elapsed = Server.CurrentTime - startTime;
                if (elapsed < 20f)
                {
                    CountdownTimer ignoreEnemiesTimer = bot.IgnoreEnemiesTimer;
                    ref float ignoreDuration = ref ignoreEnemiesTimer.Duration;
                    ignoreDuration = 0.5f;
                    ref float ignoreTimestamp = ref ignoreEnemiesTimer.Timestamp;
                    ignoreTimestamp = Server.CurrentTime + 0.5f;
                    ref float ignoreScale = ref ignoreEnemiesTimer.Timescale;
                    ignoreScale = 1.0f;
                }
                else
                {
                    CountdownTimer ignoreEnemiesTimer = bot.IgnoreEnemiesTimer;
                    ref float ignoreDuration = ref ignoreEnemiesTimer.Duration;
                    ignoreDuration = 0f;
                    ref float ignoreTimestamp = ref ignoreEnemiesTimer.Timestamp;
                    ignoreTimestamp = 0f;
                }
            }
            else
            {
                // Make sure no leftover ignore window is still ticking from a prior frame -- we want
                // perception to register sights/sounds as they happen.
                CountdownTimer ignoreEnemiesTimer = bot.IgnoreEnemiesTimer;
                ref float ignoreDuration = ref ignoreEnemiesTimer.Duration;
                ignoreDuration = 0f;
                ref float ignoreTimestamp = ref ignoreEnemiesTimer.Timestamp;
                ignoreTimestamp = 0f;
            }
        }
    }

    private static string NormalizeGrenadeType(string grenadeType)
    {
        return GrenadeTypeAliases.GetValueOrDefault(grenadeType, grenadeType);
    }

    private bool ApplyLoadout(CCSPlayerController player, ReplayPlayer replayPlayer, int loadoutBudget)
    {
        if (!player.IsValid || player.InGameMoneyServices == null)
        {
            return false;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
        {
            return false;
        }

        var itemServices = pawn.ItemServices != null && pawn.ItemServices.Handle != IntPtr.Zero
            ? new CCSPlayer_ItemServices(pawn.ItemServices.Handle)
            : null;

        StripAllWeapons(player);

        pawn.ArmorValue = replayPlayer.ArmorValue;
        Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");
        if (itemServices != null)
        {
            itemServices.HasHelmet = replayPlayer.HasHelmet;
            itemServices.HasDefuser = player.Team == CsTeam.CounterTerrorist && replayPlayer.HasDefuser;
        }

        var targetItems = BuildReplayLoadoutItems(replayPlayer);
        GiveTargetItemsDirect(player, targetItems, IsPrimaryWeapon);
        GiveTargetItemsDirect(player, targetItems, IsSecondaryWeapon);
        GiveTargetItemsDirect(player, targetItems, itemName => !IsPrimaryWeapon(itemName) && !IsSecondaryWeapon(itemName));
        SwitchToReplayLoadoutStartWeapon(player, replayPlayer);

        var loadoutValue = ReplayLoadoutValue(replayPlayer);
        player.InGameMoneyServices.Account = RoundMoneyDown(loadoutBudget - loadoutValue);
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
        return true;
    }

    private static void GiveTargetItemsDirect(
        CCSPlayerController player,
        Dictionary<string, int> targetItems,
        Func<string, bool> predicate)
    {
        foreach (var (itemName, targetCount) in targetItems.Where(pair => predicate(pair.Key)).ToList())
        {
            for (var i = 0; i < targetCount; i++)
            {
                player.GiveNamedItem(itemName);
            }
        }
    }

    private static int BuyTargetItems(
        CCSPlayerController player,
        Dictionary<string, int> targetItems,
        int currentMoney,
        int alreadySpent,
        Func<string, bool> predicate)
    {
        var spent = 0;
        foreach (var (itemName, targetCount) in targetItems.Where(pair => predicate(pair.Key)).ToList())
        {
            for (var i = 0; i < targetCount; i++)
            {
                if (!CanReplayBuyItem(player, currentMoney, alreadySpent + spent, itemName))
                {
                    break;
                }

                player.GiveNamedItem(itemName);
                if (!IsDefaultPistolForTeam(player.Team, itemName))
                {
                    spent += ItemPrice(itemName);
                }
            }
        }

        return spent;
    }

    private static void StripAllWeapons(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn?.WeaponServices == null) return;

        var toRemove = new List<string>();
        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var w = handle.Value;
            if (w == null || !w.IsValid) continue;
            var name = w.DesignerName;
            if (string.IsNullOrEmpty(name)) continue;
            if (name.Contains("knife")
                || name == "weapon_bayonet"
                || name == "weapon_c4"
                || name == "weapon_c4_explosive") continue;
            toRemove.Add(name);
        }

        foreach (var name in toRemove)
        {
            player.RemoveItemByDesignerName(name);
        }
    }

    private static void RemoveUnownedReplayWeapons()
    {
        foreach (var itemName in ItemPrices.Keys)
        {
            if (!itemName.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase)
                || itemName.Equals("weapon_knife", StringComparison.OrdinalIgnoreCase)
                || itemName.Equals("weapon_knife_t", StringComparison.OrdinalIgnoreCase)
                || itemName.Equals("weapon_c4", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var weapon in Utilities.FindAllEntitiesByDesignerName<CBasePlayerWeapon>(itemName))
            {
                if (weapon == null || !weapon.IsValid || weapon.OwnerEntity.IsValid)
                {
                    continue;
                }

                weapon.AcceptInput("Kill");
            }
        }
    }

    private int ApplyTeamLoadouts(List<BotReplayAssignment> assignments)
    {
        var applied = 0;
        foreach (var assignment in assignments)
        {
            if (ApplyLoadout(assignment.Bot, assignment.Player, assignment.Budget))
            {
                _loadoutAppliedKeys.Add(PlayerKey(assignment.Bot));
                applied++;
            }
        }
        return applied;
    }

    private static void TransferSavedUtility(List<BotReplayAssignment> assignments)
    {
        var states = assignments
            .Select(assignment => new UtilityTransferState(
                assignment,
                CountItems(GetCurrentInventory(assignment.Bot)),
                CountItems(assignment.Player.Inventory.Where(IsGiveableItem))))
            .ToList();

        foreach (var itemName in ThrowableUtilityItems)
        {
            foreach (var receiver in states)
            {
                var missingCount = receiver.Missing(itemName);
                for (var itemIndex = 0; itemIndex < missingCount; itemIndex++)
                {
                    var donor = states.FirstOrDefault(candidate => !ReferenceEquals(candidate, receiver) && candidate.Surplus(itemName) > 0);
                    if (donor == null)
                    {
                        break;
                    }

                    donor.Assignment.Bot.RemoveItemByDesignerName(itemName);
                    receiver.Assignment.Bot.GiveNamedItem(itemName);
                    donor.CurrentItems[itemName] = donor.CurrentItems.GetValueOrDefault(itemName) - 1;
                    receiver.CurrentItems[itemName] = receiver.CurrentItems.GetValueOrDefault(itemName) + 1;
                }
            }
        }
    }

    private void ApplyUtilityCap(ReplayPlayer replayPlayer, Dictionary<string, int> targetItems)
    {
        if (_config.MaxUtilityBeyondThrown < 0)
        {
            return;
        }

        var thrownCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var grenade in replayPlayer.Grenades)
        {
            var normalized = NormalizeGrenadeType(grenade.Type);
            if (string.IsNullOrEmpty(normalized) || !ThrowableUtilityItems.Contains(normalized))
            {
                continue;
            }
            thrownCounts[normalized] = thrownCounts.GetValueOrDefault(normalized) + 1;
        }

        foreach (var itemName in ThrowableUtilityItems)
        {
            if (!targetItems.TryGetValue(itemName, out var current))
            {
                continue;
            }

            var thrown = thrownCounts.GetValueOrDefault(itemName);
            var cap = thrown + _config.MaxUtilityBeyondThrown;
            if (current <= cap)
            {
                continue;
            }

            if (cap <= 0)
            {
                targetItems.Remove(itemName);
            }
            else
            {
                targetItems[itemName] = cap;
            }
        }
    }

    private static void PrepareSecondaryTarget(
        CCSPlayerController player,
        Dictionary<string, int> currentItems,
        Dictionary<string, int> targetItems,
        int currentMoney,
        int spent)
    {
        var targetSecondary = BestSecondary(targetItems.Keys);
        if (targetSecondary == null)
        {
            EnsureDefaultPistolTarget(player, currentItems, targetItems);
            return;
        }

        if (DefaultPistols.Contains(targetSecondary))
        {
            return;
        }

        if (!CanReplayBuyItem(player, currentMoney, spent, targetSecondary))
        {
            targetItems.Remove(targetSecondary);
            EnsureDefaultPistolTarget(player, currentItems, targetItems);
        }
    }

    private static void EnsureDefaultPistolTarget(
        CCSPlayerController player,
        Dictionary<string, int> currentItems,
        Dictionary<string, int> targetItems)
    {
        if (targetItems.Keys.Any(itemName => SecondaryWeapons.Contains(itemName)))
        {
            return;
        }

        var defaultPistol = CurrentDefaultPistol(player.Team, currentItems.Keys) ?? DefaultPistolForTeam(player.Team);
        if (defaultPistol == null)
        {
            return;
        }

        targetItems[defaultPistol] = Math.Max(1, targetItems.GetValueOrDefault(defaultPistol));
    }

    private static void EnsureDefaultPistol(CCSPlayerController player)
    {
        var currentItems = CountItems(GetCurrentInventory(player));
        if (currentItems.Keys.Any(itemName => SecondaryWeapons.Contains(itemName)))
        {
            return;
        }

        var defaultPistol = DefaultPistolForTeam(player.Team);
        if (defaultPistol != null)
        {
            player.GiveNamedItem(defaultPistol);
        }
    }

    private static bool ReplayActivelyUsesWeapon(ReplayPlayer replayPlayer, string itemName)
    {
        var defIndex = WeaponDefIndex(itemName);
        if (defIndex >= 0)
        {
            var normalized = NormalizeWeaponDefIndex(defIndex);
            if (replayPlayer.FirstWeaponDefIndex == normalized
                || replayPlayer.PreloadWeaponDefIndexes.Any(def => NormalizeWeaponDefIndex(def) == normalized)
                || replayPlayer.InventoryDefIndexes.Any(def => NormalizeWeaponDefIndex(def) == normalized))
            {
                return true;
            }
        }

        return replayPlayer.StartFrame?.ActiveWeapon.Equals(itemName, StringComparison.OrdinalIgnoreCase) == true
            || replayPlayer.EndFrame?.ActiveWeapon.Equals(itemName, StringComparison.OrdinalIgnoreCase) == true
            || replayPlayer.RetakeStartFrame?.ActiveWeapon.Equals(itemName, StringComparison.OrdinalIgnoreCase) == true
            || replayPlayer.RetakeEndFrame?.ActiveWeapon.Equals(itemName, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool CanReplayBuyItem(CCSPlayerController player, int currentMoney, int spent, string itemName)
    {
        var remainingMoney = Math.Max(0, currentMoney - spent);
        var price = ItemPrice(itemName);
        var isTerrorist = player.Team == CsTeam.Terrorist;
        var isCounterTerrorist = player.Team == CsTeam.CounterTerrorist;

        var canBuyOnTeam = itemName switch
        {
            "weapon_glock" => isTerrorist,
            "weapon_hkp2000" or "weapon_usp_silencer" => isCounterTerrorist,
            "weapon_tec9" or "weapon_mac10" or "weapon_sawedoff" or "weapon_galilar" or "weapon_ak47" or "weapon_sg556" or "weapon_g3sg1" or "weapon_molotov" => isTerrorist,
            "weapon_fiveseven" or "weapon_mp9" or "weapon_mag7" or "weapon_famas" or "weapon_m4a1" or "weapon_m4a1_silencer" or "weapon_aug" or "weapon_scar20" or "weapon_incgrenade" or "item_defuser" => isCounterTerrorist,
            _ => ItemPrices.ContainsKey(itemName)
        };

        return canBuyOnTeam && price <= remainingMoney;
    }

    private static void RemoveSurplusItems(
        CCSPlayerController player,
        Dictionary<string, int> currentItems,
        Dictionary<string, int> targetItems,
        string? preservedPrimary)
    {
        foreach (var (itemName, ownedCount) in currentItems.ToList())
        {
            if (preservedPrimary != null && itemName.Equals(preservedPrimary, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var targetCount = targetItems.GetValueOrDefault(itemName);
            var surplusCount = Math.Max(0, ownedCount - targetCount);
            for (var itemIndex = 0; itemIndex < surplusCount; itemIndex++)
            {
                player.RemoveItemByDesignerName(itemName);
            }

            if (surplusCount == 0)
            {
                continue;
            }

            if (targetCount > 0)
            {
                currentItems[itemName] = targetCount;
            }
            else
            {
                currentItems.Remove(itemName);
            }
        }
    }

    private static int ApplyArmorAndKit(CCSPlayerController player, CCSPlayerPawn pawn, ReplayPlayer replayPlayer, int remainingMoney)
    {
        var spent = 0;
        var itemServices = pawn.ItemServices != null && pawn.ItemServices.Handle != IntPtr.Zero
            ? new CCSPlayer_ItemServices(pawn.ItemServices.Handle)
            : null;
        var boughtHelmetWithArmor = false;

        if (replayPlayer.ArmorValue > pawn.ArmorValue)
        {
            var armorPrice = replayPlayer.HasHelmet ? (pawn.ArmorValue > 0 ? 350 : 1_000) : 650;
            if (armorPrice > remainingMoney)
            {
                return spent;
            }

            spent += armorPrice;
            remainingMoney -= armorPrice;
            boughtHelmetWithArmor = replayPlayer.HasHelmet;
            pawn.ArmorValue = replayPlayer.ArmorValue;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");
        }

        if (itemServices != null && replayPlayer.HasHelmet && !itemServices.HasHelmet)
        {
            if (!boughtHelmetWithArmor)
            {
                var helmetPrice = pawn.ArmorValue > 0 ? 350 : 1_000;
                if (helmetPrice > remainingMoney)
                {
                    return spent;
                }

                spent += helmetPrice;
                remainingMoney -= helmetPrice;
            }
            itemServices.HasHelmet = true;
        }

        if (itemServices != null && player.Team == CsTeam.CounterTerrorist && replayPlayer.HasDefuser && !itemServices.HasDefuser)
        {
            var defuserPrice = ItemPrice("item_defuser");
            if (defuserPrice > remainingMoney)
            {
                return spent;
            }

            spent += defuserPrice;
            itemServices.HasDefuser = true;
        }

        return spent;
    }

    private static void RemoveReplayManagedWeapons(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn?.WeaponServices == null)
        {
            return;
        }

        var weaponNames = pawn.WeaponServices.MyWeapons
            .Select(handle => handle.Value)
            .Where(weapon => weapon != null && weapon.IsValid)
            .Select(weapon => weapon!.DesignerName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Where(name => name != "weapon_knife" && name != "weapon_knife_t" && name != "weapon_c4")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var weaponName in weaponNames)
        {
            player.RemoveItemByDesignerName(weaponName);
        }
    }

    private bool ShouldHandOff(CCSPlayerController player, IEnumerable<CCSPlayerController> allPlayers)
    {
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
        if (RayTraceSeesAnyEnemy(player, pawn, players))
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
                return true;
            }
        }

        return false;
    }

    private bool RayTraceSeesAnyEnemy(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        IEnumerable<CCSPlayerController> allPlayers)
    {
        var rayTrace = TryGetRayTrace();
        if (rayTrace == null || !TryGetEyePosition(pawn, out var eye))
        {
            return false;
        }

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
                return true;
            }
        }

        return false;
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

    private bool CanUseDataset()
    {
        if (!_config.Enabled || _dataset == null || _dataset.Rounds.Count == 0)
        {
            return false;
        }

        // The dataset filename / contents must match the current map. We auto-load the right file on map
        // change, so this normally just rejects stale state during a brief reload window.
        return string.Equals(Server.MapName, _dataset.MapName, StringComparison.OrdinalIgnoreCase);
    }

    private void LoadConfig()
    {
        var path = Path.Join(ModuleDirectory, "config.json");
        if (!File.Exists(path))
        {
            _config = new ReplayConfig();
            Directory.CreateDirectory(ModuleDirectory);
            File.WriteAllText(path, JsonSerializer.Serialize(_config, _jsonOptions));
            return;
        }

        try
        {
            _config = JsonSerializer.Deserialize<ReplayConfig>(File.ReadAllText(path), _jsonOptions) ?? new ReplayConfig();
        }
        catch (Exception exception)
        {
            _ = exception;
            _config = new ReplayConfig();
        }
    }

    private void LoadDataset()
    {
        CancelReplayBundlePrewarm();
        BotController.ClearReplayCache();
        _dataset = null;
        _roundIndexes.Clear();
        _spawnIndexes.Clear();
        var loadGeneration = Volatile.Read(ref _replayBundlePrewarmGeneration);

        var currentMap = Server.MapName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(currentMap))
        {
            // Plugin loaded before a map is active. We'll get another shot via OnMapStart.
            return;
        }

        var template = string.IsNullOrWhiteSpace(_config.DatasetPathTemplate)
            ? "data/{map}_openings_manifest.json"
            : _config.DatasetPathTemplate;
        var relativeOrAbsolute = template.Replace("{map}", currentMap, StringComparison.OrdinalIgnoreCase);
        var path = Path.IsPathRooted(relativeOrAbsolute)
            ? relativeOrAbsolute
            : Path.Join(ModuleDirectory, relativeOrAbsolute);

        if (!File.Exists(path))
        {
            return;
        }

        Task.Run(() =>
        {
            try
            {
                ReplayDataset? dataset;
                using (var stream = File.OpenRead(path))
                {
                    dataset = JsonSerializer.Deserialize<ReplayDataset>(stream, _jsonOptions);
                }
                if (dataset != null)
                {
                    dataset.BaseDirectory = Path.GetDirectoryName(path) ?? ModuleDirectory;
                }
                PrepareDataset(dataset);
                // Hop back to the main thread to assign + index. Indexing touches non-thread-safe state.
                Server.NextFrame(() =>
                {
                    if (Volatile.Read(ref _replayBundlePrewarmGeneration) != loadGeneration)
                    {
                        return;
                    }

                    _dataset = dataset;
                    BuildRoundIndexes();
                    StartReplayBundlePrewarm();
                });
            }
            catch (Exception exception)
            {
                _ = exception;
            }
        });
    }

    private void StartReplayBundlePrewarm()
    {
        if (!_config.PrewarmReplayBundles || _dataset == null || !_nativeReplayAvailable)
        {
            return;
        }

        var paths = CollectReplayBundlePaths(_dataset);
        if (paths.Count == 0)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _replayBundlePrewarmGeneration);
        var cancellation = new CancellationTokenSource();
        _replayBundlePrewarmCancellation = cancellation;
        Volatile.Write(ref _replayBundlePrewarmTotal, paths.Count);
        Volatile.Write(ref _replayBundlePrewarmCompleted, 0);
        Volatile.Write(ref _replayBundlePrewarmFailed, 0);

        var cacheLimit = Math.Max(paths.Count, _config.ReplayBundleCacheMaxEntries);
        var batchSize = Math.Max(1, _config.PrewarmReplayBundleBatchSize);
        var delayMs = Math.Max(0, (int)Math.Round(_config.PrewarmReplayBundleBatchDelay * 1000f));
        BotController.ConfigureReplayBundleCacheLimit(cacheLimit);

        Task.Run(async () =>
        {
            var token = cancellation.Token;
            try
            {
                for (var index = 0; index < paths.Count; index += batchSize)
                {
                    token.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref _replayBundlePrewarmGeneration) != generation)
                    {
                        return;
                    }

                    var end = Math.Min(paths.Count, index + batchSize);
                    for (var pathIndex = index; pathIndex < end; pathIndex++)
                    {
                        token.ThrowIfCancellationRequested();
                        if (BotController.PrewarmReplayBundle(paths[pathIndex]))
                        {
                            Interlocked.Increment(ref _replayBundlePrewarmCompleted);
                        }
                        else
                        {
                            Interlocked.Increment(ref _replayBundlePrewarmFailed);
                        }
                    }

                    if (delayMs > 0 && end < paths.Count)
                    {
                        await Task.Delay(delayMs, token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, cancellation.Token);
    }

    private void CancelReplayBundlePrewarm()
    {
        Interlocked.Increment(ref _replayBundlePrewarmGeneration);
        var cancellation = Interlocked.Exchange(ref _replayBundlePrewarmCancellation, null);
        cancellation?.Cancel();
        Volatile.Write(ref _replayBundlePrewarmTotal, 0);
        Volatile.Write(ref _replayBundlePrewarmCompleted, 0);
        Volatile.Write(ref _replayBundlePrewarmFailed, 0);
    }

    private List<string> CollectReplayBundlePaths(ReplayDataset dataset)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var round in dataset.Rounds)
        {
            foreach (var player in round.Players)
            {
                AddReplayBundlePath(paths, dataset, player.RecPath);
                AddReplayBundlePath(paths, dataset, player.RetakeRecPath);
            }
        }

        return paths.OrderBy(path => path, StringComparer.Ordinal).ToList();
    }

    private void AddReplayBundlePath(HashSet<string> paths, ReplayDataset dataset, string relativeOrAbsolute)
    {
        if (string.IsNullOrWhiteSpace(relativeOrAbsolute))
        {
            return;
        }

        var path = ResolveReplayPath(dataset, relativeOrAbsolute);
        if (File.Exists(path))
        {
            paths.Add(path);
        }
    }

    private string ResolveReplayPath(ReplayDataset dataset, string relativeOrAbsolute)
    {
        if (Path.IsPathRooted(relativeOrAbsolute))
        {
            return relativeOrAbsolute;
        }

        var baseDirectory = string.IsNullOrWhiteSpace(dataset.BaseDirectory)
            ? ModuleDirectory
            : dataset.BaseDirectory;
        return Path.GetFullPath(Path.Combine(baseDirectory, relativeOrAbsolute.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static void PrepareDataset(ReplayDataset? dataset)
    {
        if (dataset == null)
        {
            return;
        }

        foreach (var round in dataset.Rounds)
        {
            foreach (var player in round.Players)
            {
                player.Grenades.Sort((left, right) => left.Time.CompareTo(right.Time));
            }
        }
    }

    private void BuildRoundIndexes()
    {
        _roundIndexes.Clear();
        _spawnIndexes.Clear();
        _ctRetakeCandidates.Clear();
        _tRetakeCandidates.Clear();
        _retakeCandidateRoundsWithPlant = 0;
        if (_dataset == null)
        {
            return;
        }

        // Precompute retake candidates once per dataset load. Without this, every bomb plant would
        // walk all rounds + all players' frames on the main thread (~2k candidates over a ~600 round
        // dataset), causing a noticeable server hitch right at the moment players need responsive bots.
        var saveFilterEnabled = _config.RetakeSaveFilterRadius > 0;
        var saveFilterMinEndDist = _config.RetakeSaveFilterRadius;
        foreach (var round in _dataset.Rounds)
        {
            if (round.PlantRelativeTick == null) continue;
            var countedRound = false;
            foreach (var proPlayer in round.Players)
            {
                if (string.IsNullOrWhiteSpace(ReplayPathForKind(proPlayer, ReplaySessionKind.Retake)) || proPlayer.RetakeStartFrame == null)
                {
                    continue;
                }
                countedRound = true;

                // Save filter: exclude players whose trajectory moves AWAY from the bomb.
                // A saving player ends farther from the bomb than they started AND ends beyond a
                // minimum absolute distance. This catches CTs who run to spawn and Ts who abandon site.
                if (saveFilterEnabled && round.PlantPos != null && proPlayer.RetakeEndFrame != null)
                {
                    var bombX = round.PlantPos.X;
                    var bombY = round.PlantPos.Y;
                    var bombZ = round.PlantPos.Z;

                    var startF = proPlayer.RetakeStartFrame;
                    var sdx = startF.X - bombX;
                    var sdy = startF.Y - bombY;
                    var sdz = startF.Z - bombZ;
                    var startDistSq = sdx * sdx + sdy * sdy + sdz * sdz;

                    var endF = proPlayer.RetakeEndFrame;
                    var edx = endF.X - bombX;
                    var edy = endF.Y - bombY;
                    var edz = endF.Z - bombZ;
                    var endDistSq = edx * edx + edy * edy + edz * edz;

                    // Filter: player moved away (end > start) AND ended beyond the minimum radius.
                    if (endDistSq > startDistSq && endDistSq > saveFilterMinEndDist * saveFilterMinEndDist)
                    {
                        continue;
                    }
                }

                var candidate = new RetakeCandidate(round, proPlayer, proPlayer.RetakeStartFrame);
                if (proPlayer.TeamNum == 3) _ctRetakeCandidates.Add(candidate);
                else if (proPlayer.TeamNum == 2) _tRetakeCandidates.Add(candidate);
            }
            if (countedRound)
            {
                _retakeCandidateRoundsWithPlant++;
            }
        }
        foreach (var round in _dataset.Rounds)
        {
            foreach (var economy in round.TeamEconomies)
            {
                if (economy.TeamNum != (int)CsTeam.Terrorist && economy.TeamNum != (int)CsTeam.CounterTerrorist)
                {
                    continue;
                }

                var team = (CsTeam)economy.TeamNum;
                if (!round.Players.Any(player => player.TeamNum == economy.TeamNum && player.StartFrame != null && !string.IsNullOrWhiteSpace(player.RecPath)))
                {
                    continue;
                }

                if (!_roundIndexes.TryGetValue(team, out var index))
                {
                    index = new RoundEconomyIndex();
                    _roundIndexes[team] = index;
                }

                index.Add(round, economy);

                if (!_spawnIndexes.TryGetValue(team, out var spawnIndex))
                {
                    spawnIndex = new SpawnReplayIndex((int)team);
                    _spawnIndexes[team] = spawnIndex;
                }

                spawnIndex.Add(round, economy);
            }
        }

        foreach (var index in _roundIndexes.Values)
        {
            index.Sort();
        }

        // Compute dataset-derived site centroids via k-means (k=2) on PlantPos values.
        // These replace func_bomb_target AbsOrigin for retake site classification.
        ComputeDatasetSiteCentroids();
    }

    /// <summary>
    /// Runs k-means (k=2) on all PlantPos values from rounds with plants to derive
    /// two bombsite centroids. Works correctly for vertically-stacked sites (de_nuke).
    /// Falls back to empty if fewer than 2 distinct plant positions exist.
    /// </summary>
    private void ComputeDatasetSiteCentroids()
    {
        _datasetSiteCentroids.Clear();
        if (_dataset == null) return;

        var plantPositions = _dataset.Rounds
            .Where(r => r.PlantPos != null)
            .Select(r => r.PlantPos!)
            .ToList();

        if (plantPositions.Count < 2) return;

        // K-means with k=2. Initialize with the two most distant points.
        float maxDistSq = 0;
        int idxA = 0, idxB = 1;
        for (int i = 0; i < Math.Min(plantPositions.Count, 200); i++)
        {
            for (int j = i + 1; j < Math.Min(plantPositions.Count, 200); j++)
            {
                var dx = plantPositions[i].X - plantPositions[j].X;
                var dy = plantPositions[i].Y - plantPositions[j].Y;
                var dz = plantPositions[i].Z - plantPositions[j].Z;
                var d = dx * dx + dy * dy + dz * dz;
                if (d > maxDistSq) { maxDistSq = d; idxA = i; idxB = j; }
            }
        }

        // If all plants are at approximately the same position (single site?), skip.
        if (maxDistSq < 200f * 200f) return;

        float cAx = plantPositions[idxA].X, cAy = plantPositions[idxA].Y, cAz = plantPositions[idxA].Z;
        float cBx = plantPositions[idxB].X, cBy = plantPositions[idxB].Y, cBz = plantPositions[idxB].Z;

        // 10 iterations of k-means is more than enough for 2 clusters.
        for (int iter = 0; iter < 10; iter++)
        {
            float sAx = 0, sAy = 0, sAz = 0; int nA = 0;
            float sBx = 0, sBy = 0, sBz = 0; int nB = 0;

            foreach (var p in plantPositions)
            {
                var dA = (p.X - cAx) * (p.X - cAx) + (p.Y - cAy) * (p.Y - cAy) + (p.Z - cAz) * (p.Z - cAz);
                var dB = (p.X - cBx) * (p.X - cBx) + (p.Y - cBy) * (p.Y - cBy) + (p.Z - cBz) * (p.Z - cBz);
                if (dA <= dB) { sAx += p.X; sAy += p.Y; sAz += p.Z; nA++; }
                else          { sBx += p.X; sBy += p.Y; sBz += p.Z; nB++; }
            }

            if (nA > 0) { cAx = sAx / nA; cAy = sAy / nA; cAz = sAz / nA; }
            if (nB > 0) { cBx = sBx / nB; cBy = sBy / nB; cBz = sBz / nB; }
        }

        _datasetSiteCentroids.Add(new Vector(cAx, cAy, cAz));
        _datasetSiteCentroids.Add(new Vector(cBx, cBy, cBz));
    }

    private static EquipmentValues EstimateCurrentEquipment(CCSPlayerController player)
    {
        var primaryValue = 0;
        var utilityValue = 0;
        var weaponValue = 0;
        foreach (var itemName in GetCurrentInventory(player))
        {
            var price = ItemPrice(itemName);
            weaponValue += price;
            if (PrimaryWeapons.Contains(itemName))
            {
                primaryValue += price;
            }
            else if (UtilityItems.Contains(itemName))
            {
                utilityValue += price;
            }
        }

        var pawn = player.PlayerPawn.Value;
        var armorValue = 0;
        if (pawn != null && pawn.IsValid)
        {
            var hasHelmet = false;
            if (pawn.ItemServices != null && pawn.ItemServices.Handle != IntPtr.Zero)
            {
                var itemServices = new CCSPlayer_ItemServices(pawn.ItemServices.Handle);
                hasHelmet = itemServices.HasHelmet;
                if (player.Team == CsTeam.CounterTerrorist && itemServices.HasDefuser)
                {
                    weaponValue += ItemPrice("item_defuser");
                }
            }

            if (pawn.ArmorValue > 0)
            {
                armorValue = hasHelmet ? ItemPrice("item_assaultsuit") : ItemPrice("item_kevlar");
            }
        }

        return new EquipmentValues(weaponValue + armorValue, primaryValue, utilityValue, armorValue);
    }

    private static EquipmentValues EstimateCurrentBudgetEquipment(CCSPlayerController player)
    {
        var primaryValue = 0;
        var utilityValue = 0;
        var weaponValue = 0;
        foreach (var itemName in GetCurrentInventory(player))
        {
            var price = BudgetItemValue(itemName);
            weaponValue += price;
            if (PrimaryWeapons.Contains(itemName))
            {
                primaryValue += price;
            }
            else if (UtilityItems.Contains(itemName))
            {
                utilityValue += price;
            }
        }

        var pawn = player.PlayerPawn.Value;
        var armorValue = 0;
        if (pawn != null && pawn.IsValid)
        {
            var hasHelmet = false;
            if (pawn.ItemServices != null && pawn.ItemServices.Handle != IntPtr.Zero)
            {
                var itemServices = new CCSPlayer_ItemServices(pawn.ItemServices.Handle);
                hasHelmet = itemServices.HasHelmet;
                if (player.Team == CsTeam.CounterTerrorist && itemServices.HasDefuser)
                {
                    weaponValue += BudgetItemValue("item_defuser");
                }
            }

            if (pawn.ArmorValue > 0)
            {
                armorValue = hasHelmet ? BudgetItemValue("item_assaultsuit") : BudgetItemValue("item_kevlar");
            }
        }

        return new EquipmentValues(weaponValue + armorValue, primaryValue, utilityValue, armorValue);
    }

    public static int ReplayLoadoutValue(ReplayPlayer replayPlayer)
    {
        var value = BuildReplayLoadoutItems(replayPlayer).Sum(pair => BudgetItemValue(pair.Key) * pair.Value);
        if (replayPlayer.ArmorValue > 0)
        {
            value += replayPlayer.HasHelmet ? BudgetItemValue("item_assaultsuit") : BudgetItemValue("item_kevlar");
        }
        if (replayPlayer.HasDefuser)
        {
            value += BudgetItemValue("item_defuser");
        }
        return value;
    }

    public static bool ReplayUsesPrimaryWeapon(ReplayPlayer replayPlayer)
    {
        if (BuildReplayLoadoutItems(replayPlayer).Keys.Any(itemName => PrimaryWeapons.Contains(itemName)))
        {
            return true;
        }

        if (replayPlayer.InventoryDefIndexes.Any(defIndex => PrimaryWeaponDefIndexes.Contains(NormalizeWeaponDefIndex(defIndex))))
        {
            return true;
        }

        if (PrimaryWeaponDefIndexes.Contains(NormalizeWeaponDefIndex(replayPlayer.FirstWeaponDefIndex)))
        {
            return true;
        }

        return replayPlayer.PreloadWeaponDefIndexes.Any(defIndex => PrimaryWeaponDefIndexes.Contains(NormalizeWeaponDefIndex(defIndex)));
    }

    private static int BudgetItemValue(string itemName)
    {
        var normalized = NormalizeLoadoutItem(itemName);
        return IsFreeDefaultPistol(normalized) ? 0 : ItemPrice(normalized);
    }

    private static bool IsBudgetWeapon(string itemName)
    {
        var normalized = NormalizeLoadoutItem(itemName);
        return normalized.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase)
            && !normalized.Contains("knife", StringComparison.OrdinalIgnoreCase)
            && !normalized.Equals("weapon_bayonet", StringComparison.OrdinalIgnoreCase)
            && !normalized.Equals("weapon_c4", StringComparison.OrdinalIgnoreCase)
            && !normalized.Equals("weapon_c4_explosive", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFreeDefaultPistol(string itemName)
    {
        return itemName.Equals("weapon_glock", StringComparison.OrdinalIgnoreCase)
            || itemName.Equals("weapon_hkp2000", StringComparison.OrdinalIgnoreCase)
            || itemName.Equals("weapon_usp_silencer", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, int> BuildReplayLoadoutItems(ReplayPlayer replayPlayer)
    {
        var targetItems = CountItems(replayPlayer.Inventory
            .Select(NormalizeLoadoutItem)
            .Where(IsReplayLoadoutItem));
        MergeReplayLoadoutDefs(targetItems, replayPlayer.InventoryDefIndexes);
        MergeReplayLoadoutDefs(targetItems, replayPlayer.PreloadWeaponDefIndexes);
        MergeReplayLoadoutDefs(targetItems, [replayPlayer.FirstWeaponDefIndex]);

        if (!targetItems.Keys.Any(itemName => SecondaryWeapons.Contains(itemName)))
        {
            var defaultPistol = DefaultPistolForTeam((CsTeam)replayPlayer.TeamNum);
            if (defaultPistol != null)
            {
                targetItems[defaultPistol] = Math.Max(1, targetItems.GetValueOrDefault(defaultPistol));
            }
        }

        return targetItems;
    }

    private static void MergeReplayLoadoutDefs(Dictionary<string, int> targetItems, IEnumerable<int> defIndexes)
    {
        var defCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var defIndex in defIndexes)
        {
            if (!TryGetWeaponClassByDefIndex(defIndex, out var className))
            {
                continue;
            }

            var itemName = NormalizeLoadoutItem(className);
            if (!IsReplayLoadoutItem(itemName))
            {
                continue;
            }

            defCounts[itemName] = defCounts.GetValueOrDefault(itemName) + 1;
        }

        foreach (var (itemName, count) in defCounts)
        {
            targetItems[itemName] = Math.Max(targetItems.GetValueOrDefault(itemName), count);
        }
    }

    private void SwitchToReplayLoadoutStartWeapon(CCSPlayerController player, ReplayPlayer replayPlayer)
    {
        var defIndex = ChooseLoadoutStartWeaponDef(replayPlayer);
        if (defIndex < 0 || player.Slot < 0)
        {
            return;
        }

        if (_nativeReplayAvailable && BotController.SwitchBotWeapon(player.Slot, defIndex))
        {
            return;
        }

        if (player.UserId != null && TryGetWeaponClassByDefIndex(defIndex, out var className))
        {
            NativeAPI.IssueClientCommand(player.UserId.Value, $"use {className}");
        }
    }

    private void SwitchToBestGunForHandoff(CCSPlayerController player)
    {
        if (player.Slot < 0)
        {
            return;
        }

        var pawn = player.PlayerPawn.Value;
        var weaponServices = pawn?.WeaponServices;
        if (pawn == null || !pawn.IsValid || weaponServices == null)
        {
            return;
        }

        var active = weaponServices.ActiveWeapon.Value;
        if (active != null && active.IsValid
            && GetReplayWeaponSlot(active.DesignerName) is ReplayWeaponSlot.Primary or ReplayWeaponSlot.Secondary)
        {
            return;
        }

        CBasePlayerWeapon? primary = null;
        CBasePlayerWeapon? secondary = null;
        foreach (var handle in weaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid)
            {
                continue;
            }

            switch (GetReplayWeaponSlot(weapon.DesignerName))
            {
                case ReplayWeaponSlot.Primary when primary == null:
                    primary = weapon;
                    break;
                case ReplayWeaponSlot.Secondary when secondary == null:
                    secondary = weapon;
                    break;
            }
        }

        var target = primary ?? secondary;
        if (target == null)
        {
            return;
        }

        var defIndex = WeaponDefIndex(target.DesignerName);
        if (_nativeReplayAvailable && BotController.SwitchBotWeapon(player.Slot, defIndex))
        {
            return;
        }

        weaponServices.ActiveWeapon.Raw = target.EntityHandle.Raw;
        Utilities.SetStateChanged(pawn, "CBasePlayerPawn", "m_pWeaponServices");

        if (player.UserId != null)
        {
            NativeAPI.IssueClientCommand(player.UserId.Value, $"use {target.DesignerName}");
        }
    }

    private static int ChooseLoadoutStartWeaponDef(ReplayPlayer replayPlayer)
    {
        var fallback = -1;
        foreach (var defIndex in ReplayPlayerWeaponDefs(replayPlayer))
        {
            var normalized = NormalizeWeaponDefIndex(defIndex);
            if (!IsKnownWeaponDefIndex(normalized))
            {
                continue;
            }

            if (!TryGetWeaponClassByDefIndex(normalized, out var className))
            {
                continue;
            }

            var slot = GetReplayWeaponSlot(className);
            if (slot is ReplayWeaponSlot.Primary or ReplayWeaponSlot.Secondary)
            {
                return normalized;
            }

            if (fallback < 0 && slot is ReplayWeaponSlot.Utility or ReplayWeaponSlot.Taser)
            {
                fallback = normalized;
            }
        }

        return fallback;
    }

    private static bool IsReplayLoadoutItem(string itemName)
    {
        return IsGiveableItem(itemName)
            && !itemName.Contains("knife", StringComparison.OrdinalIgnoreCase)
            && !itemName.Equals("weapon_bayonet", StringComparison.OrdinalIgnoreCase)
            && !itemName.Equals("weapon_c4", StringComparison.OrdinalIgnoreCase)
            && !itemName.Equals("weapon_c4_explosive", StringComparison.OrdinalIgnoreCase)
            && ItemPrices.ContainsKey(itemName);
    }

    private static string NormalizeLoadoutItem(string itemName)
    {
        return itemName switch
        {
            "weapon_decoy_grenade" => "weapon_decoy",
            "weapon_c4_explosive" => "weapon_c4",
            _ => itemName
        };
    }

    private static List<string> GetCurrentInventory(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn?.WeaponServices == null)
        {
            return [];
        }

        return pawn.WeaponServices.MyWeapons
            .Select(handle => handle.Value)
            .Where(weapon => weapon != null && weapon.IsValid)
            .Select(weapon => weapon!.DesignerName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Where(name => !name.Equals("weapon_knife", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("weapon_knife_t", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("weapon_c4", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("weapon_c4_explosive", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static Dictionary<string, int> CountItems(IEnumerable<string> itemNames)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var itemName in itemNames.Where(IsGiveableItem))
        {
            counts[itemName] = counts.GetValueOrDefault(itemName) + 1;
        }

        return counts;
    }

    private static string? BestPrimary(IEnumerable<string> itemNames)
    {
        return itemNames
            .Where(itemName => PrimaryWeapons.Contains(itemName))
            .OrderByDescending(ItemPrice)
            .ThenBy(itemName => itemName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string? BestSecondary(IEnumerable<string> itemNames)
    {
        return itemNames
            .Where(itemName => SecondaryWeapons.Contains(itemName))
            .OrderByDescending(ItemPrice)
            .ThenBy(itemName => itemName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string? CurrentDefaultPistol(CsTeam team, IEnumerable<string> itemNames)
    {
        return itemNames.FirstOrDefault(itemName => IsDefaultPistolForTeam(team, itemName));
    }

    private static string? DefaultPistolForTeam(CsTeam team)
    {
        return team switch
        {
            CsTeam.Terrorist => "weapon_glock",
            CsTeam.CounterTerrorist => "weapon_hkp2000",
            _ => null
        };
    }

    private static bool IsDefaultPistolForTeam(CsTeam team, string itemName)
    {
        return team switch
        {
            CsTeam.Terrorist => itemName.Equals("weapon_glock", StringComparison.OrdinalIgnoreCase),
            CsTeam.CounterTerrorist => itemName.Equals("weapon_hkp2000", StringComparison.OrdinalIgnoreCase)
                || itemName.Equals("weapon_usp_silencer", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool CanPreservePrimary(string currentPrimary, string targetPrimary)
    {
        if (currentPrimary.Equals(targetPrimary, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (targetPrimary.Equals("weapon_awp", StringComparison.OrdinalIgnoreCase) && !currentPrimary.Equals("weapon_awp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (RifleLikeWeapons.Contains(currentPrimary) && RifleLikeWeapons.Contains(targetPrimary))
        {
            return true;
        }

        return ItemPrice(currentPrimary) >= ItemPrice(targetPrimary);
    }

    private static int ItemPrice(string itemName)
    {
        return ItemPrices.GetValueOrDefault(itemName);
    }

    public static int RoundMoneyDown(int money)
    {
        return Math.Max(0, money / 50 * 50);
    }

    private static bool IsGiveableItem(string itemName)
    {
        if (itemName.Equals("weapon_c4", StringComparison.OrdinalIgnoreCase)
            || itemName.Equals("weapon_c4_explosive", StringComparison.OrdinalIgnoreCase)
            || itemName.Equals("weapon_knife", StringComparison.OrdinalIgnoreCase)
            || itemName.Equals("weapon_knife_t", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return itemName.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase)
            || itemName.StartsWith("item_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUsableBot(CCSPlayerController player)
    {
        return player.IsValid
            && player.IsBot
            && player.PawnIsAlive
            && !player.HasBeenControlledByPlayerThisRound
            && (player.Team == CsTeam.Terrorist || player.Team == CsTeam.CounterTerrorist)
            && player.PlayerPawn.Value is { IsValid: true };
    }

    private static bool IsRoundBudgetOwner(CCSPlayerController player)
    {
        return player.IsValid
            && player.PawnIsAlive
            && (player.Team == CsTeam.Terrorist || player.Team == CsTeam.CounterTerrorist)
            && player.PlayerPawn.Value is { IsValid: true };
    }

    private static bool IsLiveEnemy(CCSPlayerController player, CCSPlayerController candidate)
    {
        return candidate.IsValid
            && candidate.PawnIsAlive
            && candidate.Team != player.Team
            && (candidate.Team == CsTeam.Terrorist || candidate.Team == CsTeam.CounterTerrorist)
            && candidate.PlayerPawn.Value is { IsValid: true };
    }

    private static bool IsBotFlashed(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
        {
            return false;
        }

        return pawn.BlindUntilTime > Server.CurrentTime || (pawn.FlashDuration > 0.05f && pawn.FlashMaxAlpha > 32f);
    }

    private static int PlayerKey(CCSPlayerController player)
    {
        return player.UserId ?? (int)player.Index;
    }

    private static void Reply(CCSPlayerController? player, CommandInfo commandInfo, string message)
    {
        if (player is { IsValid: true })
        {
            player.PrintToChat($"{ChatColors.Green}[ProReplay]{ChatColors.Default} {message}");
            return;
        }

        commandInfo.ReplyToCommand($"[ProReplay] {message}");
    }
}


public static class NativeSignatures
{
    public static readonly MemoryFunctionVoid<nint, nint, int> CCSBotMoveTo = new(
        IsLinux
            ? "48 8B 06 48 89 87 E0 02 00 00 8B 46 08 48 8D B7 D8 02 00 00 89 97 EC 02 00 00 89 87 E8 02 00 00 E9 ? ? ? ?"
            : "F2 0F 10 02 F2 0F 11 81 E8 02 00 00 8B 42 08 48 8D 91 E0 02 00 00 89 81 F0 02 00 00 44 89 81 F4 02 00 00 E9 ? ? ? ?");

    private static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
}

public sealed class ReplayConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Path template used to locate per-map opening manifests. The literal token {map} is replaced with the
    /// current Server.MapName at load time. Default convention: data/de_inferno_openings_manifest.json,
    /// data/de_dust2_openings_manifest.json, etc. Set to a fully-qualified path to override.
    /// </summary>
    public string DatasetPathTemplate { get; set; } = "data/{map}_openings_manifest.json";
    [Obsolete("Use DatasetPathTemplate.")]
    public string MapName { get; set; } = "";
    [Obsolete("Use DatasetPathTemplate.")]
    public string DatasetPath { get; set; } = "";
    public bool ApplyLoadouts { get; set; } = true;
    public bool PreserveUsefulEquipment { get; set; } = true;
    public bool TransferSavedUtility { get; set; } = true;
    /// <summary>
    /// Legacy option kept for existing configs. Grenades are now thrown by native replay input;
    /// manifest grenade entries are used for matching and validation, not projectile spawning.
    /// </summary>
    public bool ThrowGrenades { get; set; } = true;
    public bool StopOnEnemyContact { get; set; } = true;
    /// <summary>
    /// Hand off to the bot AI when the bot is flashed. Off by default: pros routinely run through their own pop
    /// flashes, and ending the replay on flash truncated openings noticeably early.
    /// </summary>
    public bool StopOnFlash { get; set; } = false;
    /// <summary>
    /// Hand off when an audible enemy footstep/sound is heard nearby. Off by default for the same reason as flash:
    /// hearing an enemy is not contact, and the engine's spotted-mask check still fires the moment LOS is established.
    /// </summary>
    public bool StopOnAudibleEnemyNoise { get; set; } = false;
    /// <summary>
    /// When true, bots emit radio-style chat messages at key moments (opening target, retake callout).
    /// </summary>
    public bool RadioCallouts { get; set; } = true;
    public float SpawnMatchTolerance { get; set; } = 24f;
    public float HumanSpawnBlockRadius { get; set; } = 72f;
    public float MatchSelectionDelay { get; set; } = 0.85f;
    public float LoadoutApplyDelay { get; set; } = 1.0f;
    public float HandoffDistance { get; set; } = 1800f;
    public float HandoffFovDegrees { get; set; } = 90f;
    public float FootstepHandoffDistance { get; set; } = 1150f;
    /// <summary>
    /// Maximum number of each grenade type the bot keeps beyond what the pro actually threw in the replay window.
    /// 0 means buy exactly as many as the pro threw (no leftovers); -1 disables the cap and copies the pro's full inventory.
    /// </summary>
    public int MaxUtilityBeyondThrown { get; set; } = 0;
    /// <summary>
    /// When true, the SelectClosest matcher refuses to assign a non-pistol-round demo to a pistol-round bot loadout.
    /// </summary>
    public bool EnforcePistolRoundMatching { get; set; } = true;
    /// <summary>
    /// During replay, suppress the bot AI's own enemy engagement (so its built-in shooting does not fight our pre-aim).
    /// Hand-off detection still runs in this plugin so it does not affect first-contact responsiveness.
    /// </summary>
    public bool SuppressBotEngagementWhileReplaying { get; set; } = true;

    /// <summary>
    /// Drop non-utility attack inputs from native replay so pro prefire shots do not create sound
    /// events that make other replaying bots hand off on heard-enemy noise. Throwable utility
    /// attack/release inputs are preserved so grenades are thrown by the native replay path.
    /// </summary>
    public bool SuppressReplayAttackInput { get; set; } = true;
    /// <summary>
    /// Pre-decompress every .cs2rec bundle referenced by the current map manifest after the dataset loads.
    /// This moves the Brotli cost out of freeze-end route startup.
    /// </summary>
    public bool PrewarmReplayBundles { get; set; } = true;
    public int PrewarmReplayBundleBatchSize { get; set; } = 1;
    public float PrewarmReplayBundleBatchDelay { get; set; } = 0.15f;
    public int ReplayBundleCacheMaxEntries { get; set; } = 1024;

    /// <summary>
    /// Keep the bot's native perception/sensor loop running during replay so it can already "see" enemies
    /// while we're driving its body. When the replay ends and AI hand-off happens, the bot already has its
    /// last-known-enemy populated and reacts immediately instead of needing a fresh sight acquisition.
    /// We still suppress engagement (no shooting/aiming-at-enemy) via SuppressBotEngagementWhileReplaying.
    /// </summary>
    public bool KeepBotPerceptionDuringReplay { get; set; } = true;

    /// <summary>
    /// Minimum end-distance threshold (CS units) for the save filter. A retake candidate is excluded
    /// if their trajectory ends FARTHER from the bomb than it started AND the end distance exceeds this
    /// value. This filters out pros who saved (ran away) after bomb plant. Set to 0 to disable.
    /// </summary>
    public float RetakeSaveFilterRadius { get; set; } = 1200f;
    public float RetakeMoveToReachThreshold { get; set; } = 80f;
    public float RetakeMoveToRefreshInterval { get; set; } = 0.1f;
    public float RetakeMoveToTimeout { get; set; } = 12f;
}

public sealed class ReplayDataset
{
    public string MapName { get; set; } = "de_dust2";
    public List<ReplayRound> Rounds { get; set; } = [];
    [JsonIgnore]
    public string BaseDirectory { get; set; } = string.Empty;
}

public sealed class ReplayRound
{
    public string Id { get; set; } = string.Empty;
    public string DemoPath { get; set; } = string.Empty;
    public int RoundNumber { get; set; }
    public int FreezeEndTick { get; set; }
    public List<TeamEconomy> TeamEconomies { get; set; } = [];
    public List<ReplayPlayer> Players { get; set; } = [];
    // Bomb plant info, populated by the pipeline only for rounds where the bomb actually got planted
    // within the captured window. Used to assemble retake/post-plant replays. Explicit JsonPropertyName
    // because PropertyNameCaseInsensitive=true on JsonSerializerOptions wasn't binding camelCase JSON
    // ("plantRelativeTick") to the PascalCase property at runtime -- the dataset was loading 632 rounds
    // but every PlantRelativeTick stayed null. Adding the attribute makes the binding deterministic.
    [JsonPropertyName("plantRelativeTick")]
    public int? PlantRelativeTick { get; set; }
    [JsonPropertyName("plantPos")]
    public PlantPosition? PlantPos { get; set; }
}

public sealed class PlantPosition
{
    [JsonPropertyName("x")] public float X { get; set; }
    [JsonPropertyName("y")] public float Y { get; set; }
    [JsonPropertyName("z")] public float Z { get; set; }
}

public sealed class TeamEconomy
{
    public int TeamNum { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public int PlayerCount { get; set; }
    public int TotalStartBalance { get; set; }
    public int AverageStartBalance { get; set; }
    public int TotalEquipmentValue { get; set; }
    public int TotalPrimaryValue { get; set; }
    public int TotalUtilityValue { get; set; }
    public int TotalArmorValue { get; set; }
    public int TotalCashEquipmentValue { get; set; }
}

public sealed class ReplayPlayer
{
    public string SteamId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int TeamNum { get; set; }
    public int Slot { get; set; }
    public int StartBalance { get; set; }
    public int Balance { get; set; }
    public int EquipmentValue { get; set; }
    public int ArmorValue { get; set; }
    public bool HasHelmet { get; set; }
    public bool HasDefuser { get; set; }
    public List<string> Inventory { get; set; } = [];
    public List<int> InventoryDefIndexes { get; set; } = [];
    public string RecPath { get; set; } = string.Empty;
    public string RecKey { get; set; } = string.Empty;
    public string RetakeRecPath { get; set; } = string.Empty;
    public string RetakeRecKey { get; set; } = string.Empty;
    public float Duration { get; set; }
    [JsonPropertyName("retakeDuration")]
    public float RetakeDuration { get; set; }
    [JsonPropertyName("retakeStartTime")]
    public float RetakeStartTime { get; set; }
    [JsonPropertyName("retakeStartRelativeTick")]
    public int RetakeStartRelativeTick { get; set; }
    [JsonPropertyName("retakeStartTickIndex")]
    public int RetakeStartTickIndex { get; set; }
    public int FirstWeaponDefIndex { get; set; } = -1;
    public List<int> PreloadWeaponDefIndexes { get; set; } = [];
    public ReplayFrame? StartFrame { get; set; }
    public ReplayFrame? EndFrame { get; set; }
    public ReplayFrame? RetakeStartFrame { get; set; }
    public ReplayFrame? RetakeEndFrame { get; set; }
    public List<ReplayGrenade> Grenades { get; set; } = [];
}

public sealed class ReplayFrame
{
    public int RelativeTick { get; set; }
    public float Time { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Pitch { get; set; }
    public float Yaw { get; set; }
    public long Buttons { get; set; }
    public int? ActiveWeaponDefIndex { get; set; }
    public string ActiveWeapon { get; set; } = string.Empty;

    public ReplayFrame CloneAtTime(float time)
    {
        return new ReplayFrame
        {
            RelativeTick = RelativeTick,
            Time = time,
            X = X,
            Y = Y,
            Z = Z,
            Pitch = Pitch,
            Yaw = Yaw,
            Buttons = Buttons,
            ActiveWeaponDefIndex = ActiveWeaponDefIndex,
            ActiveWeapon = ActiveWeapon
        };
    }
}

public sealed class ReplayGrenade
{
    public int RelativeTick { get; set; }
    public float Time { get; set; }
    public string Type { get; set; } = string.Empty;
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Pitch { get; set; }
    public float Yaw { get; set; }
    public float VelocityX { get; set; }
    public float VelocityY { get; set; }
    public float VelocityZ { get; set; }
}

public sealed record ReplayAssignment(ReplayRound Round, ReplayPlayer Player, int Budget);

internal sealed class PreparedOpeningSession
{
    public PreparedOpeningSession(
        ReplayRound round,
        ReplayPlayer replayPlayer,
        List<ReplayFrame> frames,
        List<ReplayGrenade> grenades,
        bool nativeReplayPreloaded)
    {
        Round = round;
        ReplayPlayer = replayPlayer;
        Frames = frames;
        Grenades = grenades;
        NativeReplayPreloaded = nativeReplayPreloaded;
    }

    public ReplayRound Round { get; }
    public ReplayPlayer ReplayPlayer { get; }
    public List<ReplayFrame> Frames { get; }
    public List<ReplayGrenade> Grenades { get; }
    public bool NativeReplayPreloaded { get; set; }
    public bool ReplayWeaponsPreloaded { get; set; }
}

public sealed record BotReplayAssignment(CCSPlayerController Bot, ReplayRound Round, ReplayPlayer Player, int Budget);

public sealed record BotSpawn(CCSPlayerController Bot, SpawnPosition Spawn, int Budget);

public sealed record TeamEconomyState(
    int PlayerCount,
    int TotalCash,
    int AverageCash,
    int TotalEquipmentValue,
    int TotalPrimaryValue,
    int TotalUtilityValue,
    int TotalArmorValue)
{
    public int TotalCashEquipmentValue => TotalCash + TotalEquipmentValue;
}

public sealed record EquipmentValues(int TotalValue, int PrimaryValue, int UtilityValue, int ArmorValue);

public sealed class UtilityTransferState(
    BotReplayAssignment assignment,
    Dictionary<string, int> currentItems,
    Dictionary<string, int> targetItems)
{
    public BotReplayAssignment Assignment { get; } = assignment;
    public Dictionary<string, int> CurrentItems { get; } = currentItems;
    private Dictionary<string, int> TargetItems { get; } = targetItems;

    public int Missing(string itemName)
    {
        return Math.Max(0, TargetItems.GetValueOrDefault(itemName) - CurrentItems.GetValueOrDefault(itemName));
    }

    public int Surplus(string itemName)
    {
        return Math.Max(0, CurrentItems.GetValueOrDefault(itemName) - TargetItems.GetValueOrDefault(itemName));
    }
}

public readonly record struct SpawnPosition(float X, float Y, float Z)
{
    public static SpawnPosition FromFrame(ReplayFrame frame)
    {
        return new SpawnPosition(frame.X, frame.Y, frame.Z);
    }

    public bool Matches(SpawnPosition other, float tolerance)
    {
        var deltaX = X - other.X;
        var deltaY = Y - other.Y;
        var deltaZ = Z - other.Z;
        return (deltaX * deltaX) + (deltaY * deltaY) + (deltaZ * deltaZ) <= tolerance * tolerance;
    }
}

public sealed record PlayerSpawnEntry(ReplayRound Round, ReplayPlayer Player, SpawnPosition Spawn);

public sealed record RoundSpawnEntry(ReplayRound Round, TeamEconomy Economy, List<PlayerSpawnEntry> Players);

public sealed class SpawnReplayIndex(int teamNum)
{
    private readonly List<RoundSpawnEntry> _rounds = [];

    public int RoundCount => _rounds.Count;

    public void Add(ReplayRound round, TeamEconomy economy)
    {
        var players = round.Players
            .Where(player => player.TeamNum == teamNum && player.StartFrame != null && !string.IsNullOrWhiteSpace(player.RecPath))
            .Select(player => new PlayerSpawnEntry(round, player, SpawnPosition.FromFrame(player.StartFrame!)))
            .ToList();

        if (players.Count == 0)
        {
            return;
        }

        _rounds.Add(new RoundSpawnEntry(round, economy, players));
    }

    public List<BotReplayAssignment>? SelectTeamAssignments(
        List<BotSpawn> bots,
        List<SpawnPosition> humanOccupiedSpawns,
        float humanSpawnBlockRadius,
        bool enforcePistolRoundMatching,
        bool currentIsPistolRound,
        Random random)
    {
        if (bots.Count == 0 || bots.Count > 5)
        {
            return null;
        }

        var bestScore = int.MaxValue;
        var bestAssignments = new List<List<BotReplayAssignment>>();

        foreach (var round in _rounds)
        {
            if (enforcePistolRoundMatching && currentIsPistolRound && !IsPistolReplayRound(round))
            {
                continue;
            }

            if (bots.Count == 5 && round.Players.Count != 5)
            {
                continue;
            }

            if (round.Players.Count < bots.Count)
            {
                continue;
            }

            var assignments = TryMatchRound(bots, round, humanOccupiedSpawns, humanSpawnBlockRadius);
            if (assignments == null)
            {
                continue;
            }

            var score = LoadoutBudgetScore(assignments);
            if (score < bestScore)
            {
                bestScore = score;
                bestAssignments.Clear();
                bestAssignments.Add(assignments);
            }
            else if (score == bestScore)
            {
                bestAssignments.Add(assignments);
            }
        }

        return bestAssignments.Count == 0 ? null : bestAssignments[random.Next(bestAssignments.Count)];
    }

    private static int LoadoutBudgetScore(List<BotReplayAssignment> assignments)
    {
        return assignments.Sum(assignment => Math.Max(0, assignment.Budget - ProOpeningReplayPlugin.ReplayLoadoutValue(assignment.Player)));
    }

    private static List<BotReplayAssignment>? TryMatchRound(
        List<BotSpawn> bots,
        RoundSpawnEntry round,
        List<SpawnPosition> humanOccupiedSpawns,
        float humanSpawnBlockRadius)
    {
        var orderedBots = bots
            .OrderBy(bot => bot.Budget)
            .ThenBy(bot => PlayerKey(bot.Bot))
            .ToList();
        var teamBudget = orderedBots.Sum(bot => bot.Budget);

        var players = round.Players
            .Where(player => !IsHumanOccupied(player.Spawn, humanOccupiedSpawns, humanSpawnBlockRadius))
            .Select(player => new
            {
                Entry = player,
                LoadoutValue = ProOpeningReplayPlugin.ReplayLoadoutValue(player.Player)
            })
            .OrderBy(player => player.LoadoutValue)
            .ThenBy(player => player.Entry.Player.SteamId, StringComparer.Ordinal)
            .Take(orderedBots.Count)
            .ToList();
        if (players.Count < orderedBots.Count)
        {
            return null;
        }

        var teamLoadoutValue = players.Sum(player => player.LoadoutValue);
        if (teamLoadoutValue > teamBudget)
        {
            return null;
        }

        var usedPlayers = new bool[players.Count];
        var result = new BotReplayAssignment?[orderedBots.Count];
        List<BotReplayAssignment>? bestAssignments = null;
        var bestScore = int.MaxValue;

        TryAssignBot(0, 0, 0);
        return bestAssignments;

        void TryAssignBot(int botIndex, int currentLoadoutValue, int currentScore)
        {
            if (botIndex >= orderedBots.Count)
            {
                if (currentLoadoutValue > teamBudget)
                {
                    return;
                }

                var score = currentScore + (teamBudget - currentLoadoutValue);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestAssignments = AllocateTeamBudgets(result.Select(assignment => assignment!).ToList());
                }
                return;
            }
            if (currentLoadoutValue > teamBudget || currentScore >= bestScore)
            {
                return;
            }

            var bot = orderedBots[botIndex];
            for (var playerIndex = 0; playerIndex < players.Count; playerIndex++)
            {
                if (usedPlayers[playerIndex])
                {
                    continue;
                }

                var player = players[playerIndex];
                var loadoutValue = player.LoadoutValue;
                usedPlayers[playerIndex] = true;
                result[botIndex] = new BotReplayAssignment(bot.Bot, round.Round, player.Entry.Player, bot.Budget);
                TryAssignBot(
                    botIndex + 1,
                    currentLoadoutValue + loadoutValue,
                    currentScore + Math.Abs(bot.Budget - loadoutValue));
                result[botIndex] = null;
                usedPlayers[playerIndex] = false;
            }
        }
    }

    private static List<BotReplayAssignment> AllocateTeamBudgets(List<BotReplayAssignment> assignments)
    {
        var totalBudget = ProOpeningReplayPlugin.RoundMoneyDown(
            assignments.Sum(assignment => ProOpeningReplayPlugin.RoundMoneyDown(assignment.Budget)));
        var loadoutValues = assignments.ToDictionary(
            assignment => assignment,
            assignment => ProOpeningReplayPlugin.ReplayLoadoutValue(assignment.Player));
        var totalLoadout = loadoutValues.Values.Sum();
        var remaining = ProOpeningReplayPlugin.RoundMoneyDown(totalBudget - totalLoadout);
        var positiveSurplus = assignments.Sum(assignment =>
            ProOpeningReplayPlugin.RoundMoneyDown(Math.Max(
                0,
                ProOpeningReplayPlugin.RoundMoneyDown(assignment.Budget) - loadoutValues[assignment])));

        var allocated = new List<BotReplayAssignment>(assignments.Count);
        var distributed = 0;
        for (var i = 0; i < assignments.Count; i++)
        {
            var assignment = assignments[i];
            var loadoutValue = loadoutValues[assignment];
            var finalMoney = 0;
            if (remaining > 0)
            {
                if (positiveSurplus > 0)
                {
                    finalMoney = i == assignments.Count - 1
                        ? remaining - distributed
                        : remaining * ProOpeningReplayPlugin.RoundMoneyDown(Math.Max(
                            0,
                            ProOpeningReplayPlugin.RoundMoneyDown(assignment.Budget) - loadoutValue)) / positiveSurplus;
                }
                else
                {
                    finalMoney = i == assignments.Count - 1
                        ? remaining - distributed
                        : remaining / assignments.Count;
                }
                finalMoney = ProOpeningReplayPlugin.RoundMoneyDown(finalMoney);
            }

            distributed += finalMoney;
            allocated.Add(assignment with { Budget = loadoutValue + finalMoney });
        }

        return allocated;
    }

    private static bool IsHumanOccupied(SpawnPosition spawn, List<SpawnPosition> humanOccupiedSpawns, float radius)
    {
        return humanOccupiedSpawns.Any(humanSpawn => spawn.Matches(humanSpawn, radius));
    }

    private static bool IsPistolReplayRound(RoundSpawnEntry round)
    {
        return RoundEconomyIndex.IsPistolRoundEconomy(round.Economy)
            && round.Players.All(player => !ProOpeningReplayPlugin.ReplayUsesPrimaryWeapon(player.Player));
    }

    private static int PlayerKey(CCSPlayerController player)
    {
        return player.UserId ?? player.Slot;
    }
}

public sealed record IndexedRound(ReplayRound Round, TeamEconomy Economy)
{
    public int SortEconomy => EffectiveEconomy(Economy);

    public static int EffectiveEconomy(TeamEconomy economy)
    {
        return economy.TotalCashEquipmentValue > 0
            ? economy.TotalCashEquipmentValue
            : economy.TotalStartBalance + economy.TotalEquipmentValue;
    }
}

public sealed class RoundEconomyIndex
{
    private const int PlayerCountWeight = 5_000;
    private const int EffectiveEconomyWeight = 2;
    private readonly Dictionary<int, List<IndexedRound>> _roundsByPlayerCount = [];

    public int Count => _roundsByPlayerCount.Values.Sum(rounds => rounds.Count);

    public void Add(ReplayRound round, TeamEconomy economy)
    {
        if (!_roundsByPlayerCount.TryGetValue(economy.PlayerCount, out var rounds))
        {
            rounds = [];
            _roundsByPlayerCount[economy.PlayerCount] = rounds;
        }

        rounds.Add(new IndexedRound(round, economy));
    }

    public void Sort()
    {
        foreach (var rounds in _roundsByPlayerCount.Values)
        {
            rounds.Sort((left, right) => left.SortEconomy.CompareTo(right.SortEconomy));
        }
    }

    public ReplayRound? SelectClosest(TeamEconomyState state, Random random)
    {
        var bestScore = int.MaxValue;
        var bestRounds = new List<ReplayRound>();

        foreach (var (playerCount, rounds) in _roundsByPlayerCount)
        {
            var playerCountPenalty = Math.Abs(playerCount - state.PlayerCount) * PlayerCountWeight;
            if (playerCountPenalty > bestScore)
            {
                continue;
            }

            InspectClosestEconomies(rounds, state, playerCountPenalty, ref bestScore, bestRounds);
        }

        return bestRounds.Count == 0 ? null : bestRounds[random.Next(bestRounds.Count)];
    }

    private static void InspectClosestEconomies(
        List<IndexedRound> rounds,
        TeamEconomyState state,
        int playerCountPenalty,
        ref int bestScore,
        List<ReplayRound> bestRounds)
    {
        if (rounds.Count == 0)
        {
            return;
        }

        var targetEconomy = state.TotalCashEquipmentValue;
        var rightIndex = LowerBound(rounds, targetEconomy);
        var leftIndex = rightIndex - 1;
        var inspectedAny = false;

        while (leftIndex >= 0 || rightIndex < rounds.Count)
        {
            var leftBound = leftIndex >= 0 ? LowerBoundScore(rounds[leftIndex], targetEconomy, playerCountPenalty) : int.MaxValue;
            var rightBound = rightIndex < rounds.Count ? LowerBoundScore(rounds[rightIndex], targetEconomy, playerCountPenalty) : int.MaxValue;

            if (inspectedAny && Math.Min(leftBound, rightBound) > bestScore)
            {
                break;
            }

            if (leftBound <= rightBound)
            {
                Inspect(rounds[leftIndex], state, ref bestScore, bestRounds);
                leftIndex--;
            }
            else
            {
                Inspect(rounds[rightIndex], state, ref bestScore, bestRounds);
                rightIndex++;
            }

            inspectedAny = true;
        }
    }

    private static int LowerBound(List<IndexedRound> rounds, int targetEconomy)
    {
        var low = 0;
        var high = rounds.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (rounds[middle].SortEconomy < targetEconomy)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static int LowerBoundScore(IndexedRound round, int targetEconomy, int playerCountPenalty)
    {
        return playerCountPenalty + Math.Abs(round.SortEconomy - targetEconomy) * EffectiveEconomyWeight;
    }

    private static void Inspect(IndexedRound round, TeamEconomyState state, ref int bestScore, List<ReplayRound> bestRounds)
    {
        var score = EconomyScore(round, state);
        if (score < bestScore)
        {
            bestScore = score;
            bestRounds.Clear();
            bestRounds.Add(round.Round);
            return;
        }

        if (score == bestScore)
        {
            bestRounds.Add(round.Round);
        }
    }

    public static int CalculateScore(TeamEconomy economy, TeamEconomyState state)
    {
        // All TOTAL deltas must be normalized to per-player before comparing -- otherwise a 2-bot live
        // team is forced to match against pro rounds whose 5-player totals are inherently 2.5x larger,
        // and the matcher ends up preferring force/eco rounds with low utility totals. That left small
        // teams visibly under-equipped (no nades) compared to full 5v5 lobbies.
        var statePlayers = Math.Max(1, state.PlayerCount);
        var roundPlayers = Math.Max(1, economy.PlayerCount);
        var effectiveEconomy = IndexedRound.EffectiveEconomy(economy);

        // Relax PlayerCount mismatch: a smaller team should still be allowed to pull execute templates
        // from a 5v5 pro round (they're the only data we have); we just nudge the matcher slightly
        // toward same-sized rounds when both options are available.
        var playerCountDelta = Math.Abs(economy.PlayerCount - state.PlayerCount) * (PlayerCountWeight / 10);

        var perEffective = Math.Abs(effectiveEconomy / roundPlayers - state.TotalCashEquipmentValue / statePlayers);
        var effectiveDelta = perEffective * statePlayers * EffectiveEconomyWeight;

        var perCash = Math.Abs(economy.TotalStartBalance / roundPlayers - state.TotalCash / statePlayers);
        var cashDelta = perCash * statePlayers;

        var averageCashDelta = Math.Abs(economy.AverageStartBalance - state.AverageCash) * statePlayers;

        var perEquip = Math.Abs(economy.TotalEquipmentValue / roundPlayers - state.TotalEquipmentValue / statePlayers);
        var equipmentDelta = perEquip * statePlayers;

        var perPrimary = Math.Abs(economy.TotalPrimaryValue / roundPlayers - state.TotalPrimaryValue / statePlayers);
        var primaryDelta = perPrimary * statePlayers / 2;

        var perUtility = Math.Abs(economy.TotalUtilityValue / roundPlayers - state.TotalUtilityValue / statePlayers);
        var utilityDelta = perUtility * statePlayers;

        var perArmor = Math.Abs(economy.TotalArmorValue / roundPlayers - state.TotalArmorValue / statePlayers);
        var armorDelta = perArmor * statePlayers / 2;

        return playerCountDelta + effectiveDelta + cashDelta + averageCashDelta + equipmentDelta + primaryDelta + utilityDelta + armorDelta;
    }

    public static bool IsPistolRoundEconomy(TeamEconomy economy)
    {
        if (economy.PlayerCount == 0) return false;
        return economy.AverageStartBalance + (economy.TotalEquipmentValue / economy.PlayerCount) <= 1100
            && economy.TotalPrimaryValue == 0;
    }

    private static int EconomyScore(IndexedRound round, TeamEconomyState state)
    {
        return CalculateScore(round.Economy, state);
    }
}

public enum ReplayWeaponSlot
{
    Other,
    Primary,
    Secondary,
    Utility,
    C4,
    Taser,
    Knife
}

public enum ReplaySessionKind { Opening, Retake }

public enum BotMoveRouteType
{
    Default = 0,
    Fastest = 1,
    Safest = 2,
    Retreat = 3
}

public sealed class RetakeMoveToSession
{
    public RetakeMoveToSession(
        CCSPlayerController player,
        ReplayRound round,
        ReplayPlayer replayPlayer,
        List<ReplayFrame> frames,
        Vector target,
        float startTime)
    {
        Player = player;
        Round = round;
        ReplayPlayer = replayPlayer;
        Frames = frames;
        Target = target;
        StartTime = startTime;
        NextIssueTime = startTime;
    }

    public CCSPlayerController Player { get; }
    public ReplayRound Round { get; }
    public ReplayPlayer ReplayPlayer { get; }
    public List<ReplayFrame> Frames { get; }
    public Vector Target { get; }
    public float StartTime { get; }
    public float NextIssueTime { get; set; }
}

public sealed class ReplaySession
{
    public ReplaySession(
        CCSPlayerController player,
        ReplayRound round,
        ReplayPlayer replayPlayer,
        List<ReplayFrame> frames,
        List<ReplayGrenade> grenades,
        float startTime,
        ReplaySessionKind kind = ReplaySessionKind.Opening,
        float frameTimeOffset = 0f)
    {
        Player = player;
        // Snapshot the player name eagerly. After a player disconnects (or quickly switches teams) the
        // CBasePlayerController schema pointer goes null and reading PlayerName from EndSession's chat log
        // throws ArgumentNullException, killing the OnTick callback.
        PlayerName = SafeGetName(player);
        Round = round;
        ReplayPlayer = replayPlayer;
        Frames = frames;
        Grenades = grenades;
        StartTime = startTime;
        Kind = kind;
        FrameTimeOffset = frameTimeOffset;
        LastFrameTime = frames.Count == 0 ? 0f : frames[^1].Time - frameTimeOffset;
    }

    public CCSPlayerController Player { get; }
    public string PlayerName { get; }
    public ReplayRound Round { get; }
    public ReplayPlayer ReplayPlayer { get; }
    public List<ReplayFrame> Frames { get; }
    public List<ReplayGrenade> Grenades { get; }
    public float StartTime { get; }
    public float LastFrameTime { get; }
    public ReplaySessionKind Kind { get; }
    // For retake sessions, frames still carry their original Time field (relative to round freeze-end).
    // FrameTimeOffset is the Time value of the plant tick; we subtract it so elapsed=0 lines up with the
    // first post-plant frame. Opening sessions leave it 0.
    public float FrameTimeOffset { get; }
    public int NextGrenadeIndex { get; set; }
    public bool NativeReplayActive { get; set; }
    public bool NativeReplayPreloaded { get; set; }
    public bool ReplayWeaponsPreloaded { get; set; }
    public int NativeReplaySlot { get; set; } = -1;
    public int NativeReplayTickCount { get; set; }
    public int NativeReplayLastCursor { get; set; } = -1;
    public int NativeReplayStallTicks { get; set; }
    public bool NativeReplayDiagnosticLogged { get; set; }

    private static string SafeGetName(CCSPlayerController player)
    {
        try { return player.IsValid ? player.PlayerName : "<unknown>"; }
        catch { return "<unknown>"; }
    }
}
