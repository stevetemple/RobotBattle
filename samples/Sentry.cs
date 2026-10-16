using System;
using System.Linq;
using RobotBattle.Api;

// A turret that holds its ground. It only moves when an obstacle blocks its line of fire,
// so otherwise it spends energy only on scanning and shooting.
// Shows how to remember a target between ticks: a scan only returns what is in the arc
// you scanned *this* tick, so a robot that forgets its last sighting loses the target
// every time it looks away.
public class Sentry : Robot
{
    public override string Name => "Sentry";

    double sweep;
    double lastBearing;
    double lastDistance;
    int ticksSinceSeen = 999;
    int blockedTicks;
    int relocating;

    public override void Tick(RobotState s, Actions a)
    {
        // Blocked for a while? Drive a short way to find a new firing position.
        if (blockedTicks > 20) { relocating = 40; blockedTicks = 0; }
        if (relocating > 0) { relocating--; a.Turn(4); a.Move(3); }
        else a.Move(0);

        var target = s.LastScan?.Hits.FirstOrDefault(h => h.Type == HitType.Robot);
        if (target != null)
        {
            lastBearing = target.Bearing;
            lastDistance = target.Distance;
            ticksSinceSeen = 0;
            blockedTicks = Blocked(s, target) ? blockedTicks + 1 : 0;
        }
        else
        {
            ticksSinceSeen++;
        }

        if (ticksSinceSeen < 10)
        {
            // Locked on (or only just lost it): aim and fire at the last known position.
            double diff = Math.IEEERemainder(lastBearing - s.TurretHeading, 360);
            a.TurnTurret(diff);
            if (Math.Abs(diff) < 8 && blockedTicks == 0) a.Shoot(lastDistance, 2);
            a.Scan(lastBearing, 30);
        }
        else
        {
            // Lost it: sweep. Stepping 50 degrees with a 60 degree arc overlaps, so nothing slips through.
            sweep += 50;
            a.Scan(sweep, 60);
            a.TurnTurret(12);
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
