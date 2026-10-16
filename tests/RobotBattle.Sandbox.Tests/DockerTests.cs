using System.Diagnostics;
using RobotBattle.Compiler;
using RobotBattle.Engine;
using Xunit;

namespace RobotBattle.Sandbox.Tests;

/// <summary>Skipped unless Docker and the robot-battle-host image are available (see docker/Host.Dockerfile).</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        try
        {
            var psi = new ProcessStartInfo(DockerHost.ResolveDocker(new DockerOptions()), "image inspect robot-battle-host")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            if (!p.WaitForExit(15000) || p.ExitCode != 0) Skip = "robot-battle-host image not available";
        }
        catch { Skip = "docker not available"; }
    }
}

public class DockerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rb-docker-").FullName;
    private readonly List<IDisposable> _controllers = new();
    private static readonly DockerOptions Opts = new();

    public void Dispose()
    {
        foreach (var c in _controllers) c.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string Src(string body) => $@"
using System; using System.Collections.Generic; using RobotBattle.Api;
public class Bot : Robot {{
  public override string Name => ""Bot"";
  public override void Tick(RobotState s, Actions a) {{ {body} }}
}}";

    private ProcessController Launch(string body, int tickBudgetMs = 500)
    {
        var r = new RobotCompiler().Compile(Src(body));
        Assert.True(r.Success);
        var file = Guid.NewGuid().ToString("N") + ".dll";
        File.WriteAllBytes(Path.Combine(_dir, file), r.Assembly!);
        var c = new ProcessController(DockerHost.Launch(Opts, _dir, file, r.RobotTypeName!), TimeSpan.FromMilliseconds(tickBudgetMs), TimeSpan.FromSeconds(30));
        _controllers.Add(c);
        return c;
    }

    private static RobotBattle.Api.RobotState State(int t) => new() { Tick = t, ArenaWidth = 800, ArenaHeight = 600, Energy = 100 };

    private static string RunningContainers()
    {
        var psi = new ProcessStartInfo(DockerHost.ResolveDocker(Opts), "ps -q --filter name=rb-") { RedirectStandardOutput = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd(); p.WaitForExit();
        return o.Trim();
    }

    [DockerFact]
    public void RobotRunsInContainer()
    {
        var c = Launch("a.Move(2); a.Turn(s.Tick);");
        Assert.Equal("Bot", c.Name);
        for (int t = 0; t < 20; t++) Assert.Equal(t, c.Tick(State(t)).TurnDegrees);
    }

    [DockerFact]
    public void InfiniteLoopIsKilledAndContainerRemoved()
    {
        var c = Launch("if (s.Tick > 0) { while (true) { } }");
        c.Tick(State(0));
        Assert.Throws<ControllerFatalException>(() => c.Tick(State(1)));
        Thread.Sleep(1500);
        Assert.Equal("", RunningContainers());
    }

    [DockerFact]
    public void MemoryBombIsContained()
    {
        var c = Launch("if (s.Tick > 0) { var l = new List<byte[]>(); while (true) { var b = new byte[1_000_000]; b[0] = 1; l.Add(b); } }", tickBudgetMs: 5000);
        c.Tick(State(0));
        Assert.Throws<ControllerFatalException>(() => c.Tick(State(1)));
        Thread.Sleep(1500);
        Assert.Equal("", RunningContainers());
    }
}
