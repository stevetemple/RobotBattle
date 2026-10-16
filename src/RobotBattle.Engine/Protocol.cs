using System.Text.Json;
using RobotBattle.Api;

namespace RobotBattle.Engine;

/// <summary>Wire format for one tick of the engine &lt;-&gt; host protocol (one JSON document per line).</summary>
public sealed class ActionsDto
{
    public double? MoveSpeed { get; set; }
    public double Turn { get; set; }
    public double Turret { get; set; }
    public double? ShootDistance { get; set; }
    public double ShootPower { get; set; }
    public bool Recharge { get; set; }
    public double? ScanDirection { get; set; }
    public double ScanArc { get; set; }
    /// <summary>Set by the host when the robot threw; the engine treats the tick as faulted.</summary>
    public string? Error { get; set; }
    /// <summary>The robot hit a hard resource limit (memory); it is disqualified rather than just missing a tick.</summary>
    public bool Fatal { get; set; }

    public static ActionsDto From(Actions a) => new()
    {
        MoveSpeed = a.MoveSpeed, Turn = a.TurnDegrees, Turret = a.TurretDegrees,
        ShootDistance = a.ShootRequest?.Distance, ShootPower = a.ShootRequest?.Power ?? 0,
        Recharge = a.RechargeRequested,
        ScanDirection = a.ScanRequest?.Direction, ScanArc = a.ScanRequest?.Arc ?? 0,
    };

    public Actions ToActions()
    {
        var a = new Actions();
        if (Recharge) a.Recharge();
        if (MoveSpeed.HasValue) a.Move(MoveSpeed.Value);
        a.Turn(Turn); a.TurnTurret(Turret);
        if (ShootDistance.HasValue) a.Shoot(ShootDistance.Value, ShootPower);
        if (ScanDirection.HasValue) a.Scan(ScanDirection.Value, ScanArc);
        return a;
    }
}

public sealed class HandshakeDto { public string Name { get; set; } = ""; public string? Error { get; set; } }

public static class Wire
{
    public static readonly JsonSerializerOptions Options = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
}

/// <summary>Thrown by a controller that can never recover (process died, timed out). The engine disqualifies immediately.</summary>
public sealed class ControllerFatalException : Exception
{
    public ControllerFatalException(string message, Exception? inner = null) : base(message, inner) { }
}
