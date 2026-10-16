using RobotBattle.Api;

// The smallest possible robot: it does nothing at all.
// Useful as a practice target, and as proof of the minimum a submission needs:
// one public class that derives from Robot, with a Name and a Tick method.
public class SittingDuck : Robot
{
    public override string Name => "Sitting Duck";

    public override void Tick(RobotState s, Actions a)
    {
    }
}
