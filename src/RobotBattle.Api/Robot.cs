namespace RobotBattle.Api;

/// <summary>Base class for all player robots. Implement Tick; it is called once per simulation tick.</summary>
public abstract class Robot
{
    public abstract string Name { get; }

    /// <summary>Inspect the state and queue actions on <paramref name="actions"/>.</summary>
    public abstract void Tick(RobotState state, Actions actions);
}

public enum HitType { Robot, Obstacle }

/// <summary>One object found by a scan. Bearing is absolute degrees (0 = east/+X, 90 = south/+Y); Bearing and Distance point at its centre, Radius is its size.</summary>
public sealed record ScanHit(HitType Type, double Bearing, double Distance, double Radius);

public sealed class ScanResult
{
    public double Direction { get; init; }
    public double Arc { get; init; }
    public IReadOnlyList<ScanHit> Hits { get; init; } = Array.Empty<ScanHit>();
}

/// <summary>Everything a robot is allowed to know about itself and the arena.</summary>
public sealed class RobotState
{
    public int Tick { get; init; }
    public double ArenaWidth { get; init; }
    public double ArenaHeight { get; init; }
    /// <summary>Remaining health. Explosions reduce it; at 0 the robot is destroyed.</summary>
    public double Health { get; init; }
    /// <summary>Remaining energy. Actions spend it; at 0 the robot cannot act (except Recharge) but stays alive.</summary>
    public double Energy { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    /// <summary>Body heading in degrees (0 = east, 90 = south).</summary>
    public double Heading { get; init; }
    /// <summary>Turret heading in absolute degrees.</summary>
    public double TurretHeading { get; init; }
    public double Speed { get; init; }
    /// <summary>Result of the scan requested on the previous tick, or null.</summary>
    public ScanResult? LastScan { get; init; }
    /// <summary>Damage taken from explosions since the previous tick.</summary>
    public double DamageTaken { get; init; }
    public bool HitWall { get; init; }
}

/// <summary>Actions a robot requests this tick. The last call of each kind wins.</summary>
public sealed class Actions
{
    public double? MoveSpeed { get; private set; }
    public double TurnDegrees { get; private set; }
    public double TurretDegrees { get; private set; }
    public bool RechargeRequested { get; private set; }
    public (double Distance, double Power)? ShootRequest { get; private set; }
    public (double Direction, double Arc)? ScanRequest { get; private set; }

    /// <summary>Set speed; positive forwards, negative backwards. Clamped to +/-Rules.MaxSpeed.</summary>
    public void Move(double speed) => MoveSpeed = speed;
    public void Turn(double degrees) => TurnDegrees = degrees;
    public void TurnTurret(double degrees) => TurretDegrees = degrees;
    /// <summary>Fire a bullet that explodes <paramref name="distance"/> from the robot centre (scan distances map directly).</summary>
    public void Shoot(double distance, double power = 1) => ShootRequest = (distance, power);
    /// <summary>Spend this tick idle to regain energy: the robot stops and every other action this tick is ignored.</summary>
    public void Recharge() => RechargeRequested = true;
    /// <summary>Scan an arc centred on absolute <paramref name="direction"/> degrees.</summary>
    public void Scan(double direction, double arc) => ScanRequest = (direction, arc);
}
