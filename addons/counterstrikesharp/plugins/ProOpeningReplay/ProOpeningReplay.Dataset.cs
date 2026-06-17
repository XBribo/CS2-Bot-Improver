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
                    if (dataset == null)
                    {
                        Logger.LogWarning("[ProReplay] dataset load returned null path={Path}", path);
                        return;
                    }

                    _dataset = dataset;
                    BuildRoundIndexes();
                    Logger.LogInformation(
                        "[ProReplay] dataset loaded map={MapName} rounds={RoundCount} records={RecordCount} path={Path}",
                        _dataset.MapName,
                        _dataset.Rounds.Count,
                        CollectReplayBundlePaths(_dataset).Count,
                        path);
                    StartReplayBundlePrewarm();
                    if (!_freezeEnded && !_roundPrepared && _roundLoadoutBudgets.Count > 0)
                    {
                        ScheduleFreezePrepareAttempts();
                    }
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

}
