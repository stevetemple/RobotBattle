using System.Text.Json;
using RobotBattle.Api;
using RobotBattle.Engine;
using Xunit;

namespace RobotBattle.Engine.Tests;

public class SimulationTests
{
    private sealed class Idle : Robot
    {
        public override string Name => "Idle";
        public override void Tick(RobotState s, Actions a) { }
    }

    // Spins turret scanning and fires at the nearest robot hit.
    private sealed class Hunter : Robot
    {
        private double _dir;
        public override string Name => "Hunter";
        public override void Tick(RobotState s, Actions a)
        {
            var hit = s.LastScan?.Hits.FirstOrDefault(h => h.Type == HitType.Robot);
            if (hit != null)
            {
                _dir = hit.Bearing;
                double diff = ((hit.Bearing - s.TurretHeading) % 360 + 540) % 360 - 180;
                a.TurnTurret(diff);
                if (Math.Abs(diff) < 12) a.Shoot(hit.Distance, 2);
                a.Scan(hit.Bearing, 20);
            }
            else
            {
                _dir += 30; a.TurnTurret(30); a.Scan(_dir, 60);
            }
        }
    }

    private sealed class Thrower : Robot
    {
        public override string Name => "Thrower";
        public override void Tick(RobotState s, Actions a) => throw new InvalidOperationException();
    }

    private sealed class Recharger : Robot
    {
        public readonly List<(double Energy, double X)> Seen = new();
        public override string Name => "Recharger";
        public override void Tick(RobotState s, Actions a)
        {
            if (s.Tick < 4) Seen.Add((s.Energy, s.X));
            if (s.Tick == 0) a.Shoot(100, 3);
            else if (s.Tick == 1) { a.Move(4); a.Recharge(); }   // the move must be ignored
        }
    }

    [Fact]
    public void RechargeRestoresOnePercentAndSkipsOtherActions()
    {
        var r = new Recharger();
        new Simulation(new IRobotController[] { new InProcessController(r), new InProcessController(new Idle()) }, 3).Run();
        Assert.True(r.Seen[1].Energy < 99);                       // paid for the shot
        Assert.Equal(Math.Min(100, r.Seen[1].Energy + 1), r.Seen[2].Energy, 6);
        Assert.Equal(r.Seen[1].X, r.Seen[2].X);                   // did not move
        Assert.Equal(r.Seen[2].X, r.Seen[3].X);                   // and speed was reset
    }

    private static Replay Battle(int seed, params Robot[] robots) =>
        new Simulation(robots.Select(r => (IRobotController)new InProcessController(r)).ToList(), seed).Run();

    [Fact]
    public void SameSeedGivesIdenticalReplay()
    {
        var a = JsonSerializer.Serialize(Battle(7, new Hunter(), new Hunter()));
        var b = JsonSerializer.Serialize(Battle(7, new Hunter(), new Hunter()));
        Assert.Equal(a, b);
    }

    [Fact]
    public void HunterBeatsIdle()
    {
        int wins = 0;
        var rules = new Rules { ObstacleCount = 0 };
        for (int seed = 1; seed <= 10; seed++)
        {
            var sim = new Simulation(new IRobotController[] { new InProcessController(new Hunter()), new InProcessController(new Idle()) }, seed, rules);
            if (sim.Run().WinnerId == 0) wins++;
        }
        Assert.True(wins >= 8, $"hunter won only {wins}/10");
    }

    [Fact]
    public void FaultingRobotIsDisqualified()
    {
        var r = Battle(3, new Thrower(), new Idle());
        Assert.Equal(1, r.WinnerId);
        Assert.False(r.Frames[^1].Robots[0].Alive);
    }

    [Fact]
    public void IdleBattleEndsAtTimeLimitAsDraw()
    {
        var r = Battle(1, new Idle(), new Idle());
        Assert.Equal("time-limit", r.EndReason);
        Assert.Null(r.WinnerId);
    }

    [Fact]
    public void ExplosionDamageFallsOffWithDistance()
    {
        // A shooter at a fixed spot detonates close to vs. far from an idle target; closer = more damage.
        double Damage(double offset)
        {
            var rules = new Rules { ObstacleCount = 0, MaxTicks = 300 };
            var shooter = new FixedShooter(offset);
            var sim = new Simulation(new IRobotController[] { new InProcessController(shooter), new InProcessController(new Idle()) }, 5, rules);
            var replay = sim.Run();
            return 100 - replay.Frames[^1].Robots[1].Health;
        }
        // offset = how far short of the target the bullet explodes
        Assert.True(Damage(5) > Damage(40));
    }

    // Aims turret at the other robot using ground truth injected after first tick via scan, shoots short by `offset`.
    private sealed class FixedShooter : Robot
    {
        private readonly double _offset; private bool _fired;
        public FixedShooter(double offset) => _offset = offset;
        public override string Name => "Shooter";
        public override void Tick(RobotState s, Actions a)
        {
            var hit = s.LastScan?.Hits.FirstOrDefault(h => h.Type == HitType.Robot);
            if (hit == null) { a.TurnTurret(30); a.Scan(s.TurretHeading, 60); return; }
            double diff = ((hit.Bearing - s.TurretHeading) % 360 + 540) % 360 - 180;
            if (Math.Abs(diff) > 0.001) { a.TurnTurret(diff); a.Scan(hit.Bearing, 10); return; }
            if (!_fired) { _fired = true; a.Shoot(hit.Distance - _offset, 3); }
            a.Scan(hit.Bearing, 10);
        }
    }
}

public class ScoringTests
{
    [Fact]
    public void TwoRobotRoundScoresAreWinDrawLoss()
    {
        Assert.Equal(new[] { 1.0, 0.0 }, Scoring.RoundScores(new[] { 1, 2 }));
        Assert.Equal(new[] { 0.5, 0.5 }, Scoring.RoundScores(new[] { 1, 1 }));
    }

    [Fact]
    public void FourRobotScoresUseTiesAndSumToHalfTheField()
    {
        var s = Scoring.RoundScores(new[] { 1, 2, 2, 4 });   // 1st, joint 2nd, 4th
        Assert.Equal(1.0, s[0], 9);
        Assert.Equal(0.5, s[1], 9);   // loses to 1st (0), ties the other 2nd (0.5), beats 4th (1) -> 1.5 / 3
        Assert.Equal(s[1], s[2], 9);
        Assert.Equal(0.0, s[3], 9);
        Assert.Equal(2.0, s.Sum(), 9);
    }

    [Fact]
    public void EloIsZeroSumAndRewardsTheUpset()
    {
        var even = Scoring.EloDeltas(new[] { 1000.0, 1000.0 }, new[] { 1, 2 });
        Assert.Equal(16, even[0], 9);
        Assert.Equal(0, even.Sum(), 9);

        var favouriteWins = Scoring.EloDeltas(new[] { 1400.0, 1000.0 }, new[] { 1, 2 });
        var upset = Scoring.EloDeltas(new[] { 1400.0, 1000.0 }, new[] { 2, 1 });
        Assert.True(upset[1] > favouriteWins[0] * 5, "an upset should move ratings far more than an expected win");
        Assert.Equal(0, upset.Sum(), 9);
    }

    [Fact]
    public void ThreeWayDrawBetweenEqualRatingsChangesNothing()
    {
        Assert.All(Scoring.EloDeltas(new[] { 1000.0, 1000.0, 1000.0 }, new[] { 1, 1, 1 }), d => Assert.Equal(0, d, 9));
    }
}

public class PlacingTests
{
    private sealed class Idle : Robot
    {
        public override string Name => "Idle";
        public override void Tick(RobotState s, Actions a) { }
    }

    private sealed class Thrower : Robot
    {
        public override string Name => "Thrower";
        public override void Tick(RobotState s, Actions a) => throw new ControllerFatalException("dead");
    }

    private static IRobotController C(Robot r) => new InProcessController(r);

    [Fact]
    public void DisqualifiedRobotPlacesBelowSurvivor()
    {
        var r = new Simulation(new[] { C(new Idle()), C(new Thrower()) }, 1, new Rules { MaxTicks = 20 }).Run();
        Assert.Equal(new[] { 1, 2 }, r.Places);
    }

    [Fact]
    public void IdleTimeLimitRoundIsAFirstPlaceTie()
    {
        var r = new Simulation(new[] { C(new Idle()), C(new Idle()) }, 1, new Rules { MaxTicks = 20 }).Run();
        Assert.Equal(new[] { 1, 1 }, r.Places);
    }

    [Fact]
    public void SimultaneousDisqualificationTiesLastPlace()
    {
        var r = new Simulation(new[] { C(new Idle()), C(new Thrower()), C(new Thrower()) }, 1, new Rules { MaxTicks = 20 }).Run();
        Assert.Equal(new[] { 1, 2, 2 }, r.Places);
    }
}
