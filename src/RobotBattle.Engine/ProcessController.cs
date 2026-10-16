using System.Diagnostics;
using System.Text.Json;
using RobotBattle.Api;

namespace RobotBattle.Engine;

/// <summary>Launch recipe for the sandboxed host. A container runner would supply a different one (e.g. docker run ...).</summary>
public sealed record HostLaunch(string FileName, IReadOnlyList<string> Arguments)
{
    /// <summary>Extra teardown run when the robot is killed (e.g. stopping a container, since killing the docker CLI does not).</summary>
    public Action? Cleanup { get; init; }
}

/// <summary>
/// Drives a robot living in a separate host process. Any timeout, crash or protocol violation is fatal for that robot:
/// the process is killed and the engine disqualifies it.
/// </summary>
public sealed class ProcessController : IRobotController, IDisposable
{
    private readonly Process _proc;
    private Action? _cleanup;
    private readonly TimeSpan _tickBudget;
    private readonly TimeSpan _firstTickBudget;
    private bool _first = true;
    private bool _dead;
    private readonly Queue<string> _stderr = new();

    public string Name { get; }

    public ProcessController(HostLaunch launch, TimeSpan? tickBudget = null, TimeSpan? startupBudget = null)
    {
        _cleanup = launch.Cleanup;
        _tickBudget = tickBudget ?? TimeSpan.FromMilliseconds(100);
        _firstTickBudget = TimeSpan.FromSeconds(3); // JIT warm-up
        var psi = new ProcessStartInfo(launch.FileName)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in launch.Arguments) psi.ArgumentList.Add(a);
        _proc = Process.Start(psi) ?? throw new ControllerFatalException("Could not start host process.");
        // Drain stderr so a chatty process can't block on a full pipe; keep the last few lines to explain a startup failure.
        _proc.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (_stderr) { _stderr.Enqueue(e.Data.Length > 200 ? e.Data[..200] : e.Data); while (_stderr.Count > 3) _stderr.Dequeue(); }
        };
        _proc.BeginErrorReadLine();

        var hs = ReadLine(startupBudget ?? TimeSpan.FromSeconds(10));
        var dto = JsonSerializer.Deserialize<HandshakeDto>(hs, Wire.Options)!;
        if (dto.Error != null) { Kill(); throw new ControllerFatalException("Robot failed to start: " + dto.Error); }
        // Names are untrusted: bound the length and strip control characters before they reach the UI.
        var name = new string(dto.Name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        Name = name.Length == 0 ? "Robot" : name[..Math.Min(name.Length, 32)];
    }

    public Actions Tick(RobotState state)
    {
        if (_dead) throw new ControllerFatalException("Robot process is gone.");
        try
        {
            _proc.StandardInput.WriteLine(JsonSerializer.Serialize(state, Wire.Options));
            _proc.StandardInput.Flush();
        }
        catch (IOException e) { Kill(); throw new ControllerFatalException("Robot process closed its input.", e); }

        var line = ReadLine(_first ? _firstTickBudget : _tickBudget);
        _first = false;

        ActionsDto? dto;
        try { dto = JsonSerializer.Deserialize<ActionsDto>(line, Wire.Options); }
        catch (JsonException e) { Kill(); throw new ControllerFatalException("Malformed response from robot.", e); }
        if (dto is null) { Kill(); throw new ControllerFatalException("Empty response from robot."); }
        if (dto.Fatal) { Kill(); throw new ControllerFatalException(dto.Error ?? "Robot exceeded a resource limit."); }
        if (dto.Error != null) throw new InvalidOperationException(dto.Error); // robot threw: recoverable, counts as a fault
        return dto.ToActions();
    }

    private string ReadLine(TimeSpan budget)
    {
        var task = _proc.StandardOutput.ReadLineAsync();
        if (!task.Wait(budget))
        {
            Kill();
            throw new ControllerFatalException($"Robot exceeded its {budget.TotalMilliseconds:0} ms time budget.");
        }
        var line = task.Result;
        if (line is null) { Kill(); throw new ControllerFatalException("Robot process exited." + StderrSuffix()); }
        if (line.Length > 64 * 1024) { Kill(); throw new ControllerFatalException("Robot response too large."); }
        return line;
    }

    private string StderrSuffix()
    {
        Thread.Sleep(200); // let the last stderr lines arrive
        lock (_stderr) return _stderr.Count == 0 ? "" : " (" + string.Join(" | ", _stderr) + ")";
    }

    private void Kill()
    {
        _dead = true;
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
        try { _cleanup?.Invoke(); } catch { }
        _cleanup = null;
    }

    public void Dispose()
    {
        Kill();
        _proc.Dispose();
    }
}
