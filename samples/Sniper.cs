using System;
using System.Linq;
using RobotBattle.Api;

// Holds position and leads its shots. It works out where the enemy is going from two
// consecutive sightings, then aims where the enemy will be when the bullet explodes
// (bullets travel 12 units per tick, so a long shot takes a while).
//
// Handy to know: a bullet explodes at the distance you ask for, whether or not anything is there,
// and the blast does less damage the further a robot is from its centre.
public class Sniper : Robot
{
    public override string Name => "Sniper";

    const double BulletSpeed = 12;

    double sweep;
    bool havePrev;
    double prevX, prevY;
    int prevTick;
    double aimBearing;
    double aimDistance;
    int ticksSinceSeen = 999;
    int blockedTicks;
    int relocating;

    public override void Tick(RobotState s, Actions a)
    {
        // Blocked for a while? Drive a short way to find a new firing position.
        if (blockedTicks > 20) { relocating = 40; blockedTicks = 0; }
        if (relocating > 0) { relocating--; a.Turn(4); a.Move(3); }
        else a.Move(0);

        var hit = s.LastScan?.Hits.FirstOrDefault(h => h.Type == HitType.Robot);
        if (hit != null)
        {
            double rad = hit.Bearing * Math.PI / 180;
            double x = s.X + Math.Cos(rad) * hit.Distance;
            double y = s.Y + Math.Sin(rad) * hit.Distance;

            // Velocity (units per tick) from the previous sighting, if it was recent.
            double vx = 0, vy = 0;
            int dt = s.Tick - prevTick;
            if (havePrev && dt > 0 && dt <= 3) { vx = (x - prevX) / dt; vy = (y - prevY) / dt; }
            prevX = x; prevY = y; prevTick = s.Tick; havePrev = true;

            // The sighting is one tick old, and the bullet needs distance/speed ticks to arrive.
            double t = 1 + hit.Distance / BulletSpeed;
            double px = x + vx * t, py = y + vy * t;
            aimBearing = Math.Atan2(py - s.Y, px - s.X) * 180 / Math.PI;
            aimDistance = Math.Sqrt((px - s.X) * (px - s.X) + (py - s.Y) * (py - s.Y));
            ticksSinceSeen = 0;
            blockedTicks = Blocked(s, hit) ? blockedTicks + 1 : 0;
        }
        else
        {
            ticksSinceSeen++;
            if (ticksSinceSeen > 3) havePrev = false;   // velocity estimate is stale
        }

        if (ticksSinceSeen < 10)
        {
            double diff = Math.IEEERemainder(aimBearing - s.TurretHeading, 360);
            a.TurnTurret(diff);
            if (Math.Abs(diff) < 4 && blockedTicks == 0) a.Shoot(aimDistance, 2);   // ignored while the gun is cooling down
            a.Scan(aimBearing, 16);
        }
        else
        {
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
