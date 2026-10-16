using RobotBattle.Api;

namespace RobotBattle.Web;

/// <summary>Trusted built-in robots used for demos. Player robots never run in-process.</summary>
public static class SampleRobots
{
    public sealed class Spinner : Robot
    {
        public override string Name => "Spinner";
        public override void Tick(RobotState s, Actions a)
        {
            a.Move(2); a.Turn(3); a.TurnTurret(10);
            var hit = s.LastScan?.Hits.FirstOrDefault(h => h.Type == HitType.Robot);
            if (hit != null) a.Shoot(hit.Distance, 1);
            a.Scan(s.TurretHeading, 40);
        }
    }

    public sealed class Hunter : Robot
    {
        private readonly string _name;
        private double _dir;
        public Hunter(string name) => _name = name;
        public override string Name => _name;

        public override void Tick(RobotState s, Actions a)
        {
            var hit = s.LastScan?.Hits.FirstOrDefault(h => h.Type == HitType.Robot);
            if (hit != null)
            {
                double diff = ((hit.Bearing - s.TurretHeading) % 360 + 540) % 360 - 180;
                a.TurnTurret(diff);
                if (Math.Abs(diff) < 12) a.Shoot(hit.Distance, 2);
                a.Scan(hit.Bearing, 20);
                // Keep some distance: drive sideways relative to the target.
                a.Turn(((hit.Bearing + 90 - s.Heading) % 360 + 540) % 360 - 180);
                a.Move(hit.Distance > 200 ? 3 : -2);
            }
            else
            {
                _dir += 30; a.TurnTurret(30); a.Scan(_dir, 60); a.Move(1.5); a.Turn(2);
            }
        }
    }
}
