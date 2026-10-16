// Sandbox host: loads ONE compiled robot and serves ticks over stdin/stdout.
// usage: RobotBattle.Host <robot.dll> <RobotTypeName>
// Protocol: host writes a HandshakeDto line, then for each RobotState line read it writes one ActionsDto line.
using System.Reflection;
using System.Text.Json;
using RobotBattle.Api;
using RobotBattle.Engine;

// Keep the protocol channel private so nothing the robot does can write into it.
var protocol = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
Console.SetOut(TextWriter.Null);
Console.SetError(TextWriter.Null);

void Send(object o) => protocol.WriteLine(JsonSerializer.Serialize(o, Wire.Options));

Robot robot;
try
{
    if (args.Length != 2) throw new ArgumentException("usage: Host <dll> <type>");
    var asm = Assembly.LoadFrom(args[0]);
    var type = asm.GetType(args[1], throwOnError: true)!;
    robot = (Robot)Activator.CreateInstance(type)!;
    Send(new HandshakeDto { Name = robot.Name });
}
catch (Exception e)
{
    Send(new HandshakeDto { Error = e.GetType().Name + ": " + e.Message });
    return 1;
}

string? line;
while ((line = Console.In.ReadLine()) != null)
{
    try
    {
        var state = JsonSerializer.Deserialize<RobotState>(line, Wire.Options)!;
        var actions = new Actions();
        robot.Tick(state, actions);
        Send(ActionsDto.From(actions));
    }
    catch (OutOfMemoryException)
    {
        Send(new ActionsDto { Error = "OutOfMemoryException: memory limit exceeded", Fatal = true });
    }
    catch (Exception e)
    {
        Send(new ActionsDto { Error = e.GetType().Name + ": " + e.Message });
    }
}
return 0;
