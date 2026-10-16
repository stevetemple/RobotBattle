using System.Linq;
using RobotBattle.Api;

// A crude beginner robot: drive in a circle, spin the turret, and fire whenever a scan
// found someone. It does not aim, so it mostly relies on the turret not having moved far
// since the scan. See Hunter for a robot that actually lines up its shots.
public class Spinner : Robot
{
    public override string Name => "Spinner";

    public override void Tick(RobotState s, Actions a)
    {
        a.Move(2);          // speed persists until you change it
        a.Turn(3);          // body turns 3 degrees every tick -> a big circle
        a.TurnTurret(10);   // turret sweeps around

        // A scan requested last tick delivers its results now.
        var target = s.LastScan?.Hits.FirstOrDefault(h => h.Type == HitType.Robot);
        if (target != null)
            a.Shoot(target.Distance, 1);

        // Look along the turret, 40 degrees wide.
        a.Scan(s.TurretHeading, 40);
    }
}
