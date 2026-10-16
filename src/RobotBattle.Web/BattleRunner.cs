using System.IO.Compression;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using RobotBattle.Api;
using RobotBattle.Engine;

namespace RobotBattle.Web;

public sealed class SandboxOptions
{
    /// <summary>"Docker" (default, isolated) or "Process" (dev only: NOT a security boundary).</summary>
    public string Mode { get; set; } = "Docker";
    public string Image { get; set; } = "robot-battle-host";
    public int TickBudgetMs { get; set; } = 200;
    public int MemoryMb { get; set; } = 192;
    /// <summary>
    /// Where each battle's robot files are staged. Defaults to App_Data/work under the app. It must be readable by whatever
    /// account runs Docker, which is why the process temp folder (C:\Windows\Temp under IIS) is not used.
    /// </summary>
    public string? WorkDir { get; set; }
}

public sealed class BattleQueue
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>();
    public void Enqueue(int battleId) => _channel.Writer.TryWrite(battleId);
    public IAsyncEnumerable<int> ReadAll(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>One robot's outcome across a whole match.</summary>
public sealed record MatchRobot(
    int RobotId, string Label, string Owner,
    double Points, int RoundWins, int Place, double RatingBefore, double RatingAfter);

/// <summary>One round's outcome. <c>Places</c> is indexed like the match's robot list; 1 is best, ties share a place.</summary>
public sealed record RoundSummary(int Round, List<int> Places, string? EndReason, int Ticks);

public sealed record MatchResult(List<MatchRobot> Robots, List<RoundSummary> Rounds);

/// <summary>What is stored (gzipped) for a finished battle and sent to the viewer.</summary>
public sealed record MatchReplay(List<Replay> Rounds, MatchResult Result);

/// <summary>Stand-in for a robot that could not even start; it is disqualified on its first tick.</summary>
internal sealed class DeadController : IRobotController
{
    public DeadController(string name) => Name = name;
    public string Name { get; }
    public Actions Tick(RobotState state) => throw new ControllerFatalException("Robot failed to start.");
}

/// <summary>Runs queued battles one at a time. A battle is several rounds; every round gets a fresh arena and fresh sandboxes.</summary>
public sealed class BattleRunner : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly BattleQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly SandboxOptions _sandbox;
    private readonly ILogger<BattleRunner> _log;
    private readonly string _workRoot;

    public BattleRunner(BattleQueue queue, IServiceScopeFactory scopes, SandboxOptions sandbox, ILogger<BattleRunner> log, IHostEnvironment env)
    {
        _queue = queue; _scopes = scopes; _sandbox = sandbox; _log = log;
        _workRoot = Path.GetFullPath(sandbox.WorkDir ?? Path.Combine(env.ContentRootPath, "App_Data", "work"));
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Battles interrupted by a restart go back on the queue (and restart from round 1).
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var pending = await db.Battles.Where(b => b.Status == BattleStatus.Queued || b.Status == BattleStatus.Running)
                .OrderBy(b => b.Id).Select(b => b.Id).ToListAsync(ct);
            foreach (var id in pending) _queue.Enqueue(id);
        }

        await foreach (var id in _queue.ReadAll(ct))
        {
            try { await RunOne(id, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) { _log.LogError(e, "Battle {Id} crashed", id); await MarkFailed(id, e.Message); }
        }
    }

    private async Task MarkFailed(int id, string error)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var b = await db.Battles.FindAsync(id);
        if (b == null) return;
        b.Status = BattleStatus.Failed; b.Error = Trim(error);
        await db.SaveChangesAsync();
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;

    private async Task RunOne(int id, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var battle = await db.Battles.FindAsync(new object[] { id }, ct);
        if (battle == null || battle.Status is BattleStatus.Done or BattleStatus.Failed) return;
        battle.Status = BattleStatus.Running;
        battle.RoundsDone = 0;
        await db.SaveChangesAsync(ct);

        var ids = battle.RobotIds.Split(',').Select(int.Parse).ToList();
        var found = await db.Robots.Include(r => r.Player).Where(r => ids.Contains(r.Id)).ToListAsync(ct);
        var robots = ids.Select(i => found.FirstOrDefault(r => r.Id == i)).ToList();
        if (robots.Any(r => r == null)) { await MarkFailed(id, "A robot in this battle no longer exists."); return; }
        int n = robots.Count;
        var labels = robots.Select(r => $"{r!.Label} ({r.Player?.Name})").ToList();

        Directory.CreateDirectory(_workRoot);
        var work = Path.Combine(_workRoot, "rb-battle-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Stage each robot once; one directory per robot so none can see another's DLL. The same files serve every round.
            var dirs = new List<string>();
            for (int i = 0; i < n; i++)
            {
                var dir = Path.Combine(work, i.ToString());
                Directory.CreateDirectory(dir);
                await File.WriteAllBytesAsync(Path.Combine(dir, "robot.dll"), robots[i]!.Assembly, ct);
                dirs.Add(dir);
            }

            var ratings = robots.Select(r => r!.Rating).ToArray();
            var startRatings = (double[])ratings.Clone();
            var points = new double[n];
            var roundWins = new int[n];
            var roundDraws = new int[n];
            var replays = new List<Replay>();
            var summaries = new List<RoundSummary>();
            var startupProblems = new Dictionary<int, string>();

            for (int round = 0; round < battle.Rounds; round++)
            {
                ct.ThrowIfCancellationRequested();

                // Fresh sandboxes every round so nothing a robot remembered carries over. Launch them in parallel: container start-up dominates.
                var launched = await Task.WhenAll(Enumerable.Range(0, n).Select(i => Task.Run(() =>
                {
                    try { return (Controller: (IRobotController)Launch(dirs[i], robots[i]!.TypeName), Problem: (string?)null); }
                    catch (ControllerFatalException e)
                    {
                        _log.LogWarning("Robot {Id} failed to start in round {Round}: {Msg}", robots[i]!.Id, round + 1, e.Message);
                        return (Controller: new DeadController(labels[i]), Problem: $"{robots[i]!.Label} failed to start (round {round + 1}): {e.Message}");
                    }
                })));
                var controllers = launched.Select(l => l.Controller).ToList();
                for (int i = 0; i < n; i++)
                    if (launched[i].Problem != null) startupProblems.TryAdd(i, launched[i].Problem!);

                Replay replay;
                try
                {
                    int roundSeed = unchecked(battle.Seed + round * 7919);
                    replay = await Task.Run(() => new Simulation(controllers, roundSeed).Run(), ct);
                }
                finally
                {
                    foreach (var c in controllers.OfType<IDisposable>()) c.Dispose();
                }

                // Show "label (owner)" in the viewer instead of whatever the robot calls itself.
                for (int i = 0; i < n; i++) replay.Robots[i] = new RobotInfo(i, labels[i]);
                replays.Add(replay);
                summaries.Add(new RoundSummary(round + 1, replay.Places.ToList(), replay.EndReason, replay.Frames.Count));

                var scores = Scoring.RoundScores(replay.Places);
                var delta = Scoring.EloDeltas(ratings, replay.Places);
                int firsts = replay.Places.Count(p => p == 1);
                for (int i = 0; i < n; i++)
                {
                    points[i] += scores[i];
                    ratings[i] += delta[i];
                    if (replay.Places[i] == 1) { if (firsts == 1) roundWins[i]++; else roundDraws[i]++; }
                }

                battle.RoundsDone = round + 1;
                await db.SaveChangesAsync(ct);   // progress only: ratings are applied once, at the end
            }

            // Match placings from total points (ties share a place).
            var matchPlaces = Enumerable.Range(0, n).Select(i => 1 + Enumerable.Range(0, n).Count(j => points[j] > points[i] + 1e-9)).ToList();
            int topCount = matchPlaces.Count(p => p == 1);

            var result = new MatchResult(
                Enumerable.Range(0, n).Select(i => new MatchRobot(
                    robots[i]!.Id, robots[i]!.Label, robots[i]!.Player?.Name ?? "", Math.Round(points[i], 2), roundWins[i],
                    matchPlaces[i], Math.Round(startRatings[i], 1), Math.Round(ratings[i], 1))).ToList(),
                summaries);

            using var ms = new MemoryStream();
            await using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                await JsonSerializer.SerializeAsync(gz, new MatchReplay(replays, result), Json, ct);

            for (int i = 0; i < n; i++)
            {
                var r = robots[i]!;
                r.Rating = ratings[i];
                r.Matches++;
                if (matchPlaces[i] == 1 && topCount == 1) r.MatchWins++;
                r.RoundsPlayed += battle.Rounds;
                r.RoundWins += roundWins[i];
                r.RoundDraws += roundDraws[i];
            }

            battle.ReplayGz = ms.ToArray();
            battle.ResultJson = JsonSerializer.Serialize(result, Json);
            battle.WinnerRobotId = topCount == 1 ? robots[matchPlaces.IndexOf(1)]!.Id : null;
            battle.EndReason = "rounds-complete";
            battle.Error = startupProblems.Count > 0 ? Trim(string.Join("; ", startupProblems.Values)) : null;
            battle.Status = BattleStatus.Done;
            await SaveSkippingDeletedRobots(db, ct);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { /* best effort */ }
        }
    }

    /// <summary>A player may delete a robot while its match is running; drop that robot's rating update rather than failing the battle.</summary>
    private static async Task SaveSkippingDeletedRobots(AppDb db, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { await db.SaveChangesAsync(ct); return; }
            catch (DbUpdateConcurrencyException e) when (attempt < 3)
            {
                foreach (var entry in e.Entries.Where(x => x.Entity is RobotEntry)) entry.State = EntityState.Detached;
            }
        }
    }

    private ProcessController Launch(string dir, string typeName)
    {
        var budget = TimeSpan.FromMilliseconds(_sandbox.TickBudgetMs);
        var launch = _sandbox.Mode.Equals("Process", StringComparison.OrdinalIgnoreCase)
            ? new HostLaunch("dotnet", new[] { Path.Combine(AppContext.BaseDirectory, "RobotBattle.Host.dll"), Path.Combine(dir, "robot.dll"), typeName })
            : DockerHost.Launch(new DockerOptions { Image = _sandbox.Image, MemoryMb = _sandbox.MemoryMb }, dir, "robot.dll", typeName);
        return new ProcessController(launch, budget, TimeSpan.FromSeconds(30));
    }
}
