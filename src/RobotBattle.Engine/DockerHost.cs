using System.Diagnostics;

namespace RobotBattle.Engine;

public sealed class DockerOptions
{
    public string Image { get; init; } = "robot-battle-host";
    public string? DockerPath { get; init; }
    public int MemoryMb { get; init; } = 192;
    public double Cpus { get; init; } = 1;
    public int PidsLimit { get; init; } = 32;
}

/// <summary>Builds <see cref="HostLaunch"/> recipes that run the host inside a locked-down container.</summary>
public static class DockerHost
{
    public static string ResolveDocker(DockerOptions o)
    {
        if (o.DockerPath != null) return o.DockerPath;
        const string desktop = @"C:\Program Files\Docker\Docker\resources\bin\docker.exe";
        return OperatingSystem.IsWindows() && File.Exists(desktop) ? desktop : "docker";
    }

    /// <param name="robotDir">Directory holding the compiled robot DLL; mounted read-only.</param>
    public static HostLaunch Launch(DockerOptions o, string robotDir, string dllFileName, string typeName)
    {
        var docker = ResolveDocker(o);
        var name = "rb-" + Guid.NewGuid().ToString("N");
        var args = new List<string>
        {
            "run", "--rm", "-i", "--name", name,
            "--network", "none",                       // no network at all
            "--read-only",                             // immutable root filesystem
            "--tmpfs", "/tmp:rw,noexec,nosuid,size=8m",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--memory", o.MemoryMb + "m", "--memory-swap", o.MemoryMb + "m",
            "--cpus", o.Cpus.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--pids-limit", o.PidsLimit.ToString(),
            "--ulimit", "nofile=64:64",
            "--mount", $"type=bind,source={robotDir},target=/robot,readonly",
            o.Image, "/robot/" + dllFileName, typeName,
        };
        return new HostLaunch(docker, args) { Cleanup = () => Stop(docker, name) };
    }

    private static void Stop(string docker, string name)
    {
        var psi = new ProcessStartInfo(docker) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "kill", name }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        p?.WaitForExit(5000);
    }
}
