using RobotBattle.Api;

namespace RobotBattle.Engine;

/// <summary>Deterministic battle simulation. Same seed + same controller behaviour = same replay.</summary>
public sealed class Simulation
{
    private const int MaxConsecutiveFaults = 30;

    private sealed class Bot
    {
        public int Id;
        public IRobotController Controller = null!;
        public double X, Y, Heading, Turret, Health, Energy, Speed;
        public bool Alive = true;
        public int DeathTick = -1;
        public int Cooldown;
        public int Faults;
        public ScanResult? PendingScan;
        public double DamageTaken;
        public bool HitWall;
        public double? ScanDir, ScanArc;
    }

    private sealed class Bullet
    {
        public double X, Y, Dx, Dy, Remaining, Power;
    }

    private readonly Rules _rules;
    private readonly int _seed;
    private readonly Random _rng;
    private readonly List<Bot> _bots = new();
    private readonly List<ObstacleInfo> _obstacles = new();
    private readonly List<Bullet> _bullets = new();

    public Simulation(IReadOnlyList<IRobotController> controllers, int seed, Rules? rules = null)
    {
        if (controllers.Count < 2) throw new ArgumentException("At least two robots are required.");
        _rules = rules ?? new Rules();
        _seed = seed;
        _rng = new Random(seed);
        PlaceObstacles();
        for (int i = 0; i < controllers.Count; i++) _bots.Add(Spawn(i, controllers[i]));
    }

    private void PlaceObstacles()
    {
        for (int i = 0; i < _rules.ObstacleCount; i++)
        {
            // Keep obstacles spread out: retry until clear of the others by a wide gap, relaxing the gap
            // only if the arena is too crowded to satisfy it (so placement always terminates).
            for (int attempt = 0; ; attempt++)
            {
                double r = 20 + _rng.NextDouble() * 20;
                double x = r + 60 + _rng.NextDouble() * (_rules.ArenaWidth - 2 * r - 120);
                double y = r + 60 + _rng.NextDouble() * (_rules.ArenaHeight - 2 * r - 120);
                double gap = Math.Max(0, 150 - attempt / 5);
                if (attempt < 1000 && _obstacles.Any(o => Dist(x, y, o.X, o.Y) < o.Radius + r + gap)) continue;
                _obstacles.Add(new ObstacleInfo(Math.Round(x, 2), Math.Round(y, 2), Math.Round(r, 2)));
                break;
            }
        }
    }

    private Bot Spawn(int id, IRobotController c)
    {
        double rr = _rules.RobotRadius;
        for (int attempt = 0; ; attempt++)
        {
            double x = rr + 5 + _rng.NextDouble() * (_rules.ArenaWidth - 2 * rr - 10);
            double y = rr + 5 + _rng.NextDouble() * (_rules.ArenaHeight - 2 * rr - 10);
            bool ok = !_obstacles.Any(o => Dist(x, y, o.X, o.Y) < o.Radius + rr + 2)
                   && !_bots.Any(b => Dist(x, y, b.X, b.Y) < 150 - Math.Min(attempt, 100));
            if (!ok && attempt < 1000) continue;
            double h = _rng.NextDouble() * 360;
            return new Bot { Id = id, Controller = c, X = x, Y = y, Heading = h, Turret = h, Health = _rules.StartHealth, Energy = _rules.StartEnergy };
        }
    }

    public Replay Run()
    {
        var replay = new Replay
        {
            ArenaWidth = _rules.ArenaWidth, ArenaHeight = _rules.ArenaHeight, RobotRadius = _rules.RobotRadius,
            Seed = _seed,
            Robots = _bots.Select(b => new RobotInfo(b.Id, b.Controller.Name)).ToList(),
            Obstacles = _obstacles.ToList(),
        };

        for (int tick = 0; tick < _rules.MaxTicks; tick++)
        {
            var explosions = new List<ExplosionFrame>();
            StepRobots(tick, explosions);
            StepBullets(explosions);
            ApplyDamage(explosions);
            foreach (var b in _bots.Where(b => !b.Alive && b.DeathTick < 0)) b.DeathTick = tick;
            replay.Frames.Add(Snapshot(tick, explosions));

            int alive = _bots.Count(b => b.Alive);
            if (alive <= 1)
            {
                replay.WinnerId = _bots.FirstOrDefault(b => b.Alive)?.Id;
                replay.EndReason = alive == 1 ? "last-standing" : "mutual-destruction";
                replay.Places = ComputePlaces();
                return replay;
            }
        }

        var best = _bots.Where(b => b.Alive).OrderByDescending(b => b.Health).ToList();
        replay.WinnerId = best.Count > 1 && Math.Abs(best[0].Health - best[1].Health) < 1e-9 ? null : best[0].Id;
        replay.EndReason = "time-limit";
        replay.Places = ComputePlaces();
        return replay;
    }

    /// <summary>Competition ranking (1, 2, 2, 4): survivors beat the dead, survivors rank by health, the dead by how late they died. Equal keys tie.</summary>
    private List<int> ComputePlaces()
    {
        double Key(Bot b) => b.Alive ? 1e9 + b.Health : b.DeathTick;
        return _bots.Select(b => 1 + _bots.Count(o => Key(o) > Key(b) + 1e-9)).ToList();
    }

    // Explosion damage is queued here during bullet stepping and applied once all bullets have resolved.
    private readonly List<(double X, double Y, double Power)> _pendingBlasts = new();

    private void StepRobots(int tick, List<ExplosionFrame> explosions)
    {
        foreach (var b in _bots.Where(b => b.Alive))
        {
            var state = new RobotState
            {
                Tick = tick, ArenaWidth = _rules.ArenaWidth, ArenaHeight = _rules.ArenaHeight,
                Health = b.Health, Energy = b.Energy, X = b.X, Y = b.Y, Heading = b.Heading, TurretHeading = b.Turret,
                Speed = b.Speed, LastScan = b.PendingScan, DamageTaken = b.DamageTaken, HitWall = b.HitWall,
            };
            b.PendingScan = null; b.DamageTaken = 0; b.HitWall = false; b.ScanDir = b.ScanArc = null;

            Actions? act = null;
            try { act = b.Controller.Tick(state); b.Faults = 0; }
            catch (ControllerFatalException) { b.Health = 0; b.Alive = false; continue; }
            catch { if (++b.Faults >= MaxConsecutiveFaults) { b.Health = 0; b.Alive = false; continue; } }
            if (act is null) act = new Actions();

            Apply(b, act);
        }
    }

    private static double Clamp(double v, double lim) => double.IsNaN(v) ? 0 : Math.Max(-lim, Math.Min(lim, v));

    private bool Spend(Bot b, double cost)
    {
        if (b.Energy < cost) return false;
        b.Energy -= cost;
        return true;
    }

    private void Apply(Bot b, Actions a)
    {
        b.Energy = Math.Max(0, b.Energy - _rules.IdleDrain);

        if (a.RechargeRequested)
        {
            // A recharge tick is spent entirely idle: stopped, no scan, no shot (the gun still cools down).
            b.Speed = 0;
            if (b.Cooldown > 0) b.Cooldown--;
            b.Energy = Math.Min(_rules.StartEnergy, b.Energy + _rules.StartEnergy * _rules.RechargeFraction);
            return;
        }

        double turn = Clamp(a.TurnDegrees, _rules.MaxTurn);
        if (Spend(b, Math.Abs(turn) * _rules.TurnCostPerDegree)) b.Heading = Norm(b.Heading + turn);

        double tt = Clamp(a.TurretDegrees, _rules.MaxTurretTurn);
        if (Spend(b, Math.Abs(tt) * _rules.TurretCostPerDegree)) b.Turret = Norm(b.Turret + tt);

        double speed = a.MoveSpeed.HasValue ? Clamp(a.MoveSpeed.Value, _rules.MaxSpeed) : b.Speed;
        if (!Spend(b, Math.Abs(speed) * _rules.MoveCostPerSpeed)) speed = 0;
        b.Speed = speed;
        Move(b);

        if (a.ScanRequest is { } sr && !double.IsNaN(sr.Direction) && !double.IsNaN(sr.Arc))
        {
            double arc = Math.Max(0, Math.Min(sr.Arc, _rules.MaxScanArc));
            if (Spend(b, arc * _rules.ScanCostPerDegree))
            {
                b.ScanDir = Norm(sr.Direction); b.ScanArc = arc;
                b.PendingScan = Scan(b, b.ScanDir.Value, arc);
            }
        }

        if (b.Cooldown > 0) b.Cooldown--;
        if (a.ShootRequest is { } sh && b.Cooldown == 0 && !double.IsNaN(sh.Distance) && !double.IsNaN(sh.Power))
        {
            double power = Math.Max(0.1, Math.Min(sh.Power, _rules.MaxPower));
            double dist = Math.Max(1, Math.Min(sh.Distance, _rules.MaxShotDistance));
            if (Spend(b, power * _rules.ShootCostPerPower + dist * _rules.ShootCostPerDistance))
            {
                double rad = b.Turret * Math.PI / 180;
                _bullets.Add(new Bullet
                {
                    X = b.X + Math.Cos(rad) * _rules.RobotRadius, Y = b.Y + Math.Sin(rad) * _rules.RobotRadius,
                    Dx = Math.Cos(rad), Dy = Math.Sin(rad), Remaining = Math.Max(1, dist - _rules.RobotRadius), Power = power,
                });
                b.Cooldown = _rules.GunCooldownTicks;
            }
        }
    }

    private void Move(Bot b)
    {
        if (b.Speed == 0) return;
        double rad = b.Heading * Math.PI / 180;
        double nx = b.X + Math.Cos(rad) * b.Speed, ny = b.Y + Math.Sin(rad) * b.Speed;
        double rr = _rules.RobotRadius;

        bool blocked = nx < rr || nx > _rules.ArenaWidth - rr || ny < rr || ny > _rules.ArenaHeight - rr;
        if (!blocked) blocked = _obstacles.Any(o => Dist(nx, ny, o.X, o.Y) < o.Radius + rr);
        if (!blocked) blocked = _bots.Any(o => o != b && o.Alive && Dist(nx, ny, o.X, o.Y) < 2 * rr);

        if (blocked)
        {
            b.Speed = 0; b.HitWall = true;
            b.Energy = Math.Max(0, b.Energy - _rules.WallHitCost);
            nx = Math.Max(rr, Math.Min(_rules.ArenaWidth - rr, b.X));
            ny = Math.Max(rr, Math.Min(_rules.ArenaHeight - rr, b.Y));
            return;
        }
        b.X = nx; b.Y = ny;
    }

    private ScanResult Scan(Bot b, double dir, double arc)
    {
        var hits = new List<ScanHit>();
        void Test(HitType type, double x, double y, double radius)
        {
            double d = Dist(b.X, b.Y, x, y);
            if (d < 1e-9 || d > _rules.MaxScanRange) return;
            double bearing = Math.Atan2(y - b.Y, x - b.X) * 180 / Math.PI;
            double half = d > radius ? Math.Asin(radius / d) * 180 / Math.PI : 90;
            double diff = Math.Abs(AngleDiff(bearing, dir));
            if (diff <= arc / 2 + half) hits.Add(new ScanHit(type, Math.Round(Norm(bearing), 2), Math.Round(d, 2), Math.Round(radius, 2)));
        }
        foreach (var o in _bots) if (o != b && o.Alive) Test(HitType.Robot, o.X, o.Y, _rules.RobotRadius);
        foreach (var o in _obstacles) Test(HitType.Obstacle, o.X, o.Y, o.Radius);
        hits.Sort((p, q) => p.Distance.CompareTo(q.Distance));
        return new ScanResult { Direction = dir, Arc = arc, Hits = hits };
    }

    private void StepBullets(List<ExplosionFrame> explosions)
    {
        for (int i = _bullets.Count - 1; i >= 0; i--)
        {
            var bu = _bullets[i];
            double step = Math.Min(_rules.BulletSpeed, bu.Remaining);
            bu.X += bu.Dx * step; bu.Y += bu.Dy * step; bu.Remaining -= step;

            bool detonate = bu.Remaining <= 1e-9
                || bu.X < 0 || bu.X > _rules.ArenaWidth || bu.Y < 0 || bu.Y > _rules.ArenaHeight
                || _obstacles.Any(o => Dist(bu.X, bu.Y, o.X, o.Y) < o.Radius);
            if (!detonate) continue;

            bu.X = Math.Max(0, Math.Min(_rules.ArenaWidth, bu.X));
            bu.Y = Math.Max(0, Math.Min(_rules.ArenaHeight, bu.Y));
            _pendingBlasts.Add((bu.X, bu.Y, bu.Power));
            explosions.Add(new ExplosionFrame(Math.Round(bu.X, 2), Math.Round(bu.Y, 2), _rules.ExplosionRadius));
            _bullets.RemoveAt(i);
        }
    }

    private void ApplyDamage(List<ExplosionFrame> _)
    {
        foreach (var (x, y, power) in _pendingBlasts)
            foreach (var b in _bots.Where(b => b.Alive))
            {
                double d = Dist(x, y, b.X, b.Y);
                if (d >= _rules.ExplosionRadius) continue;
                double dmg = power * _rules.DamagePerPower * (1 - d / _rules.ExplosionRadius);
                b.Health -= dmg; b.DamageTaken += dmg;
            }
        _pendingBlasts.Clear();
        foreach (var b in _bots.Where(b => b.Alive && b.Health <= 0)) { b.Health = 0; b.Alive = false; }
    }

    private Frame Snapshot(int tick, List<ExplosionFrame> explosions) => new(
        tick,
        _bots.Select(b => new RobotFrame(b.Id, Math.Round(b.X, 2), Math.Round(b.Y, 2), Math.Round(b.Heading, 2),
            Math.Round(b.Turret, 2), Math.Round(b.Health, 2), Math.Round(b.Energy, 2), b.Alive, b.ScanDir, b.ScanArc)).ToList(),
        _bullets.Select(u => new BulletFrame(Math.Round(u.X, 2), Math.Round(u.Y, 2))).ToList(),
        explosions);

    private static double Dist(double x1, double y1, double x2, double y2) => Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));
    private static double Norm(double a) { a %= 360; return a < 0 ? a + 360 : a; }
    private static double AngleDiff(double a, double b) { double d = Norm(a - b); return d > 180 ? d - 360 : d; }
}
