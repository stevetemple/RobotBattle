using RobotBattle.Compiler;
using RobotBattle.Engine;
using Xunit;

namespace RobotBattle.Sandbox.Tests;

public class HostTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rb-test-").FullName;
    private readonly List<IDisposable> _controllers = new();

    public void Dispose()
    {
        foreach (var c in _controllers) c.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string Src(string body, string members = "") => $@"
using System; using System.Collections.Generic; using RobotBattle.Api;
public class Bot : Robot {{
  public override string Name => ""Bot"";
  {members}
  public override void Tick(RobotState s, Actions a) {{ {body} }}
}}";

    private ProcessController Launch(string source, TimeSpan? budget = null)
    {
        var result = new RobotCompiler().Compile(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var dll = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".dll");
        File.WriteAllBytes(dll, result.Assembly!);
        var host = Path.Combine(AppContext.BaseDirectory, "RobotBattle.Host.dll");
        var c = new ProcessController(new HostLaunch("dotnet", new[] { host, dll, result.RobotTypeName! }), budget);
        _controllers.Add(c);
        return c;
    }

    private static RobotBattle.Api.RobotState State(int tick = 0) => new() { Tick = tick, ArenaWidth = 800, ArenaHeight = 600, Energy = 100 };

    [Fact]
    public void RobotRunsInHostProcessAndReturnsActions()
    {
        var c = Launch(Src("a.Move(3); a.Turn(s.Tick); a.Shoot(100, 2);"));
        Assert.Equal("Bot", c.Name);
        var act = c.Tick(State(5));
        Assert.Equal(3, act.MoveSpeed);
        Assert.Equal(5, act.TurnDegrees);
        Assert.Equal((100d, 2d), act.ShootRequest);
    }

    [Fact]
    public void InfiniteLoopTimesOutAndIsFatal()
    {
        // The first tick has a longer warm-up budget, so loop only after it.
        var c2 = Launch(Src("if (s.Tick > 0) { while (true) { } }"), TimeSpan.FromMilliseconds(100));
        c2.Tick(State(0));
        Assert.Throws<ControllerFatalException>(() => c2.Tick(State(1)));
        Assert.Throws<ControllerFatalException>(() => c2.Tick(State(2)));
    }

    [Fact]
    public void ExceptionInRobotIsRecoverableFault()
    {
        var c = Launch(Src("if (s.Tick == 1) throw new InvalidOperationException(\"boom\"); a.Move(1);"));
        c.Tick(State(0));
        Assert.Throws<InvalidOperationException>(() => c.Tick(State(1)));
        Assert.Equal(1, c.Tick(State(2)).MoveSpeed);
    }

    [Fact]
    public void StackOverflowKillsOnlyTheHost()
    {
        var c = Launch(Src("if (s.Tick > 0) Recurse(1);", "static int Recurse(int n) => Recurse(n + 1) + 1;"));
        c.Tick(State(0));
        Assert.Throws<ControllerFatalException>(() => c.Tick(State(1)));
    }

    [Fact]
    public void FullBattleWithSandboxedRobotsDisqualifiesBadOne()
    {
        var good = Launch(Src("a.Turn(2); a.TurnTurret(5); a.Scan(s.TurretHeading, 30);"));
        var bad = Launch(Src("if (s.Tick > 3) { while (true) { } }"), TimeSpan.FromMilliseconds(100));
        var replay = new Simulation(new IRobotController[] { good, bad }, 1, new Rules { MaxTicks = 50 }).Run();
        Assert.Equal(0, replay.WinnerId);
        Assert.False(replay.Frames[^1].Robots[1].Alive);
    }
}
