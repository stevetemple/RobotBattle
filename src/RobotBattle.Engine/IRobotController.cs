using RobotBattle.Api;

namespace RobotBattle.Engine;

/// <summary>Abstraction over a robot, so the engine runs the same in-process or via a sandbox host.</summary>
public interface IRobotController
{
    string Name { get; }

    /// <summary>Return the robot's actions for this tick. May throw; the engine treats that as "no action".</summary>
    Actions Tick(RobotState state);
}

/// <summary>Runs a <see cref="Robot"/> directly in this process. For tests and trusted robots only.</summary>
public sealed class InProcessController : IRobotController
{
    private readonly Robot _robot;
    public InProcessController(Robot robot) => _robot = robot;
    public string Name => _robot.Name;

    public Actions Tick(RobotState state)
    {
        var actions = new Actions();
        _robot.Tick(state, actions);
        return actions;
    }
}
