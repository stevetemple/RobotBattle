namespace RobotBattle.Engine;

/// <summary>Tunable game constants.</summary>
public sealed class Rules
{
    public double ArenaWidth { get; init; } = 800;
    public double ArenaHeight { get; init; } = 600;
    public double RobotRadius { get; init; } = 18;
    public double StartEnergy { get; init; } = 100;
    public double StartHealth { get; init; } = 100;
    public int MaxTicks { get; init; } = 5000;
    public int ObstacleCount { get; init; } = 5;

    public double MaxSpeed { get; init; } = 4;
    public double MaxTurn { get; init; } = 8;          // degrees/tick
    public double MaxTurretTurn { get; init; } = 12;   // degrees/tick
    public double MaxScanArc { get; init; } = 60;
    public double MaxScanRange { get; init; } = 1000; // >= arena diagonal: a scan sees the whole arena along its arc
    public double BulletSpeed { get; init; } = 12;
    public double MaxShotDistance { get; init; } = 600;
    public double RechargeFraction { get; init; } = 0.01; // of StartEnergy, gained per Recharge tick
    public double MaxPower { get; init; } = 3;
    public double ExplosionRadius { get; init; } = 60;
    public double DamagePerPower { get; init; } = 10;
    public int GunCooldownTicks { get; init; } = 10;

    // Energy costs
    public double MoveCostPerSpeed { get; init; } = 0.01;
    public double TurnCostPerDegree { get; init; } = 0.01;
    public double TurretCostPerDegree { get; init; } = 0.005;
    public double ShootCostPerPower { get; init; } = 1.0;
    public double ShootCostPerDistance { get; init; } = 0.002;
    public double ScanCostPerDegree { get; init; } = 0.001;
    public double WallHitCost { get; init; } = 0.5;
    public double IdleDrain { get; init; } = 0.0;
}
