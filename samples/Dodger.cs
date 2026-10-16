using System;
using System.Linq;
using RobotBattle.Api;

// Strafes sideways around its target so bullets aimed at where it *was* miss.
// Shows the feedback fields in RobotState: HitWall (we bumped into the arena edge, an
// obstacle or another robot) and DamageTaken (an explosion hurt us last tick).
public class Dodger : Robot
{
    public override string Name => "Dodger";

    readonly Random rng = new Random();
    double sweep;
    double lastBearing;
    double lastDistance;
    int ticksSinceSeen = 999;
    int direction = 1;   // +1 or -1: which way along the strafe line we are going

    public override void Tick(RobotState s, Actions a)
    {
        var target = s.LastScan?.Hits.FirstOrDefault(h => h.Type == HitType.Robot);
        if (target != null)
        {
            lastBearing = target.Bearing;
            lastDistance = target.Distance;
            ticksSinceSeen = 0;
        }
        else
        {
            ticksSinceSeen++;
        }
        bool tracking = ticksSinceSeen < 8;

        // --- gun ---
        if (tracking)
        {
            double diff = Math.IEEERemainder(lastBearing - s.TurretHeading, 360);
            a.TurnTurret(diff);
            if (Math.Abs(diff) < 10 && s.Energy > 15) a.Shoot(lastDistance, 2);
            a.Scan(lastBearing, 24);
        }
        else
        {
            sweep += 50;
            a.TurnTurret(30);
            a.Scan(sweep, 60);
        }

        // --- legs ---
        // Reverse when we hit something, and now and then when we are hit, so we are hard to predict.
        if (s.HitWall || (s.DamageTaken > 0 && rng.NextDouble() < 0.5))
            direction = -direction;

        // Point sideways to the target (or just wander when we have not found one) and drive.
        double wanted = tracking ? lastBearing + 90 : s.Heading + 15;
        a.Turn(Math.IEEERemainder(wanted - s.Heading, 360));
        a.Move(3 * direction);   // negative speed reverses
    }
}
