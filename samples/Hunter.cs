using System;
using System.Linq;
using RobotBattle.Api;

// The starter robot. It sweeps the arena with scans until it finds an enemy, then
// swings its turret onto it, fires when lined up, and keeps its distance.
//
// Every tick the arena calls Tick. Look at `s` (your state) and queue actions on `a`.
// Angles are in degrees: 0 = east, 90 = south. Everything costs energy, and so does being hit.
public class Hunter : Robot
{
    public override string Name => "Hunter";

    double sweep;

    public override void Tick(RobotState s, Actions a)
    {
        // Did the scan we asked for last tick find another robot?
        var target = s.LastScan?.Hits.FirstOrDefault(h => h.Type == HitType.Robot);

        if (target != null)
        {
            // Swing the turret onto the target and fire when it is lined up.
            // IEEERemainder gives the shortest turn, in the range -180..180.
            double diff = Math.IEEERemainder(target.Bearing - s.TurretHeading, 360);
            a.TurnTurret(diff);
            bool blocked = Blocked(s, target);
            if (Math.Abs(diff) < 12 && !blocked) a.Shoot(target.Distance, 2);

            a.Scan(target.Bearing, 20);              // narrow scan: keep an eye on it
            if (blocked)
            {
                // An obstacle is in the way: sidestep to get a clear line.
                a.Turn(Math.IEEERemainder(target.Bearing + 90 - s.Heading, 360));
                a.Move(3);
            }
            else
            {
                a.Move(target.Distance > 200 ? 2 : -2);  // close in, or back off
            }
        }
        else
        {
            // Nothing known: sweep the arena with the turret and look around.
            sweep += 30;
            a.TurnTurret(30);
            a.Scan(sweep, 60);                       // 60 degrees is the widest scan allowed
            a.Turn(2);
            a.Move(1.5);
        }
    }

    // True if an obstacle sits on the line between us and the target, so a shot would explode on it.
    // Scan hits carry the obstacle's Radius, which is what makes this check possible.
    static bool Blocked(RobotState s, ScanHit target)
    {
        foreach (var h in s.LastScan!.Hits)
        {
            if (h.Type != HitType.Obstacle || h.Distance >= target.Distance) continue;
            double angle = (h.Bearing - target.Bearing) * Math.PI / 180;
            double sideways = Math.Abs(Math.Sin(angle) * h.Distance);   // how far the obstacle's centre is off our line of fire
            if (sideways < h.Radius + 5) return true;
        }
        return false;
    }
}
