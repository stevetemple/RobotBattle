namespace RobotBattle.Engine;

public sealed record RobotInfo(int Id, string Name);
public sealed record ObstacleInfo(double X, double Y, double Radius);

public sealed record RobotFrame(
    int Id, double X, double Y, double Heading, double Turret, double Health, double Energy, bool Alive,
    double? ScanDir, double? ScanArc);

public sealed record BulletFrame(double X, double Y);
public sealed record ExplosionFrame(double X, double Y, double Radius);

public sealed record Frame(
    int Tick,
    IReadOnlyList<RobotFrame> Robots,
    IReadOnlyList<BulletFrame> Bullets,
    IReadOnlyList<ExplosionFrame> Explosions);

/// <summary>Everything the browser viewer needs to play a battle back.</summary>
public sealed class Replay
{
    public double ArenaWidth { get; init; }
    public double ArenaHeight { get; init; }
    public double RobotRadius { get; init; }
    public int Seed { get; init; }
    public List<RobotInfo> Robots { get; init; } = new();
    public List<ObstacleInfo> Obstacles { get; init; } = new();
    public List<Frame> Frames { get; init; } = new();
    /// <summary>Id of the winning robot, or null for a draw.</summary>
    public int? WinnerId { get; set; }
    public string? EndReason { get; set; }
    /// <summary>Final place of each robot, indexed by robot id. 1 is best; tied robots share a place.</summary>
    public List<int> Places { get; set; } = new();
}
