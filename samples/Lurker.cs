using System;
using System.Collections.Generic;
using System.Linq;
using RobotBattle.Api;

// A Sniper that doesn't stand in the open. Scans see straight through obstacles but bullets do not,
// so the Lurker maps the obstacles, then parks directly behind one with the obstacle between it and the
// enemy. While hidden it keeps its turret on the enemy and waits for the gun to cool down. Then it
// steps sideways until the line of fire clears the obstacle, fires a led shot, and ducks back.
//
// Handy to know: enemy bullets explode on the obstacle's surface, and the blast reaches 60 units, so the
// hiding spot sits far enough behind the obstacle's centre to stay outside it.
public class Lurker : Robot
{
    public override string Name => "Lurker";

    const double BulletSpeed = 12;
    const double RobotRadius = 18;
    const int GunCooldown = 10;

    enum Mode { Hide, Peek }

    class Rock { public double X, Y, R; }

    readonly List<Rock> rocks = new();
    int mapTicks;
    double sweep;

    // Enemy tracking (same lead calculation as Sniper).
    bool haveEnemy, havePrev;
    double prevX, prevY, aimX, aimY;
    int prevTick;
    double enemyX, enemyY;
    int ticksSinceSeen = 999;

    Mode mode = Mode.Hide;
    Rock? home;
    int homeSince;
    int peekTicks;
    int side = 1;
    int lastShot = -999;
    int unstick;
    int recharged;

    public override void Tick(RobotState s, Actions a)
    {
        Observe(s);

        // Spend the first few ticks scanning the whole arena (60 degrees at a time) to map the obstacles.
        if (mapTicks < 6)
        {
            a.Scan(mapTicks * 60 + 30, 60);
            mapTicks++;
            a.Move(0);
            return;
        }

        if (s.HitWall && unstick == 0) unstick = 10;
        if (unstick > 0)
        {
            unstick--;
            a.Move(-3);
            a.Turn(8);
            Track(s, a);
            return;
        }

        if (!haveEnemy) { a.Move(0); Track(s, a); return; }

        double aimBearing = Math.Atan2(aimY - s.Y, aimX - s.X) * 180 / Math.PI;
        double aimDistance = Math.Sqrt((aimX - s.X) * (aimX - s.X) + (aimY - s.Y) * (aimY - s.Y));
        double diff = Math.IEEERemainder(aimBearing - s.TurretHeading, 360);
        bool ready = s.Tick - lastShot >= GunCooldown;
        bool clear = !Blocked(s.X, s.Y, aimX, aimY);

        ChooseHome(s);

        if (home == null)
        {
            // No usable cover: hold position and shoot whenever the line is clear.
            a.Move(0);
            if (ready && clear && Math.Abs(diff) < 4) Fire(s, a, aimDistance);
        }
        else if (mode == Mode.Hide)
        {
            double hx, hy;
            HidingSpot(home, out hx, out hy);
            double d = GoTo(s, a, hx, hy);
            if (d < 8)
            {
                // Safe behind cover: top up energy. A recharge tick can't scan, so at most two in a row
                // to keep the enemy's position fresh.
                if (s.Energy <= 90 && ticksSinceSeen <= 1 && recharged < 2)
                {
                    recharged++;
                    a.Recharge();
                    return;
                }
                recharged = 0;
                if (ready) { mode = Mode.Peek; peekTicks = 0; }
            }
        }
        else
        {
            peekTicks++;
            double px, py;
            PeekSpot(home, side, out px, out py);
            if (!Open(px, py, home)) { side = -side; PeekSpot(home, side, out px, out py); }
            GoTo(s, a, px, py);

            if (ready && clear && Math.Abs(diff) < 4)
            {
                Fire(s, a, aimDistance);
                mode = Mode.Hide;
            }
            else if (peekTicks > 45)
            {
                // Couldn't get a clear shot from this side; try the other, or another obstacle.
                side = -side;
                mode = Mode.Hide;
                if (peekTicks > 45) homeSince = -999;
            }
        }

        Track(s, a);
    }

    void Fire(RobotState s, Actions a, double distance)
    {
        a.Shoot(distance, 2);
        lastShot = s.Tick;
    }

    // Folds the last scan into the obstacle map and the enemy estimate.
    void Observe(RobotState s)
    {
        var scan = s.LastScan;
        if (scan == null) { ticksSinceSeen++; if (ticksSinceSeen > 3) havePrev = false; return; }

        foreach (var h in scan.Hits.Where(h => h.Type == HitType.Obstacle))
        {
            double rad = h.Bearing * Math.PI / 180;
            double x = s.X + Math.Cos(rad) * h.Distance, y = s.Y + Math.Sin(rad) * h.Distance;
            if (!rocks.Any(r => Math.Abs(r.X - x) < 6 && Math.Abs(r.Y - y) < 6))
                rocks.Add(new Rock { X = x, Y = y, R = h.Radius });
        }

        var hit = scan.Hits.FirstOrDefault(h => h.Type == HitType.Robot);
        if (hit != null)
        {
            double rad = hit.Bearing * Math.PI / 180;
            double x = s.X + Math.Cos(rad) * hit.Distance, y = s.Y + Math.Sin(rad) * hit.Distance;
            double vx = 0, vy = 0;
            int dt = s.Tick - prevTick;
            if (havePrev && dt > 0 && dt <= 3) { vx = (x - prevX) / dt; vy = (y - prevY) / dt; }
            prevX = x; prevY = y; prevTick = s.Tick; havePrev = true;

            double t = 1 + hit.Distance / BulletSpeed;
            aimX = x + vx * t; aimY = y + vy * t;
            enemyX = x; enemyY = y;
            haveEnemy = true;
            ticksSinceSeen = 0;
        }
        else
        {
            ticksSinceSeen++;
            if (ticksSinceSeen > 3) havePrev = false;
        }
    }

    // Keeps the turret on the aim point and the scan on the enemy (or sweeping if it is lost).
    void Track(RobotState s, Actions a)
    {
        if (haveEnemy && ticksSinceSeen < 6)
        {
            double bearing = Math.Atan2(aimY - s.Y, aimX - s.X) * 180 / Math.PI;
            a.TurnTurret(Math.IEEERemainder(bearing - s.TurretHeading, 360));
            a.Scan(bearing, 24);
        }
        else
        {
            sweep += 55;
            a.Scan(sweep, 60);
            a.TurnTurret(12);
        }
    }

    // Where to stand: directly behind the obstacle as seen from the enemy, outside the blast radius of
    // a shell that bursts on its near surface.
    void HidingSpot(Rock r, out double x, out double y)
    {
        double ux = r.X - enemyX, uy = r.Y - enemyY;
        double len = Math.Sqrt(ux * ux + uy * uy);
        if (len < 1e-6) { ux = 1; uy = 0; len = 1; }
        ux /= len; uy /= len;
        x = r.X + ux * (r.R + 22);
        y = r.Y + uy * (r.R + 22);
    }

    // Where to stand to shoot: out beside the hiding spot, far enough off the enemy-obstacle line to clear the rock.
    void PeekSpot(Rock r, int sgn, out double x, out double y)
    {
        double hx, hy;
        HidingSpot(r, out hx, out hy);
        double ux = r.X - enemyX, uy = r.Y - enemyY;
        double len = Math.Max(1e-6, Math.Sqrt(ux * ux + uy * uy));
        double nx = -uy / len * sgn, ny = ux / len * sgn;
        // The line from the peek spot to the enemy passes the obstacle closer to the axis than we stand, so
        // scale the sideways step by the geometry (a close enemy needs a much wider step than a distant one).
        double behind = r.R + 22;
        double side = (r.R + 10) * (len + behind) / Math.Max(len, 1);
        x = hx + nx * side;
        y = hy + ny * side;
    }

    // Is a spot inside the arena and clear of every obstacle (other than the one we hide behind)?
    bool Open(double x, double y, Rock self)
    {
        if (x < 24 || x > 776 || y < 24 || y > 576) return false;
        return !rocks.Any(r => r != self && Dist(x, y, r.X, r.Y) < r.R + RobotRadius + 4);
    }

    void ChooseHome(RobotState s)
    {
        bool stillGood = false;
        if (home != null)
        {
            double hx, hy;
            HidingSpot(home, out hx, out hy);
            double de = Dist(hx, hy, enemyX, enemyY);
            stillGood = Open(hx, hy, home) && de > 90 && de < 580;
        }
        if (stillGood && s.Tick - homeSince < 100) return;

        Rock? best = null;
        double bestCost = double.MaxValue;
        foreach (var r in rocks)
        {
            double hx, hy;
            HidingSpot(r, out hx, out hy);
            double de = Dist(hx, hy, enemyX, enemyY);
            if (!Open(hx, hy, r) || de < 90 || de > 580) continue;
            double px, py;
            PeekSpot(r, 1, out px, out py);
            bool a1 = Open(px, py, r);
            PeekSpot(r, -1, out px, out py);
            if (!a1 && !Open(px, py, r)) continue;      // nowhere to step out to
            double cost = Dist(s.X, s.Y, hx, hy) + (r == home ? -40 : 0);
            if (cost < bestCost) { bestCost = cost; best = r; }
        }
        if (best != home) { mode = Mode.Hide; side = 1; }
        home = best;
        homeSince = s.Tick;
    }

    // Drives toward a point, swinging around any obstacle in the way. Returns the distance to the point.
    double GoTo(RobotState s, Actions a, double tx, double ty)
    {
        double gx = tx, gy = ty;
        foreach (var r in rocks)
        {
            if (SegmentDist(r.X, r.Y, s.X, s.Y, tx, ty) >= r.R + RobotRadius + 4) continue;
            if (Dist(s.X, s.Y, r.X, r.Y) < r.R + RobotRadius + 3) continue;   // already hugging it; just go
            double dx = tx - s.X, dy = ty - s.Y;
            double len = Math.Max(1e-6, Math.Sqrt(dx * dx + dy * dy));
            double nx = -dy / len, ny = dx / len;
            double off = r.R + RobotRadius + 12;
            double ax = r.X + nx * off, ay = r.Y + ny * off, bx = r.X - nx * off, by = r.Y - ny * off;
            bool useA = Dist(ax, ay, tx, ty) < Dist(bx, by, tx, ty);
            gx = useA ? ax : bx; gy = useA ? ay : by;
            break;
        }

        double dist = Dist(s.X, s.Y, gx, gy);
        if (dist < 2) { a.Move(0); return Dist(s.X, s.Y, tx, ty); }   // arrived: don't wobble in place
        double err = Math.IEEERemainder(Math.Atan2(gy - s.Y, gx - s.X) * 180 / Math.PI - s.Heading, 360);
        a.Turn(Math.Max(-8, Math.Min(8, err)));
        a.Move(Math.Abs(err) > 35 ? 0 : Math.Min(4, dist));
        return Dist(s.X, s.Y, tx, ty);
    }

    // True if a shell fired from (fx, fy) at (tx, ty) would burst on a known obstacle first.
    bool Blocked(double fx, double fy, double tx, double ty)
        => rocks.Any(r => SegmentDist(r.X, r.Y, fx, fy, tx, ty) < r.R + 4
                          && Dist(fx, fy, r.X, r.Y) < Dist(fx, fy, tx, ty));

    static double SegmentDist(double px, double py, double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1, dy = y2 - y1;
        double l2 = dx * dx + dy * dy;
        double t = l2 < 1e-9 ? 0 : Math.Max(0, Math.Min(1, ((px - x1) * dx + (py - y1) * dy) / l2));
        return Dist(px, py, x1 + dx * t, y1 + dy * t);
    }

    static double Dist(double x1, double y1, double x2, double y2)
        => Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));
}
