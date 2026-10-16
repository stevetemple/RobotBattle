using System.Reflection;
using RobotBattle.Api;
using RobotBattle.Compiler;
using RobotBattle.Engine;
using Xunit;
using Xunit.Abstractions;

namespace RobotBattle.Sandbox.Tests;

/// <summary>The robots in /samples are the player-facing examples, so they must compile and actually work.</summary>
public class SampleRobotTests
{
    private readonly ITestOutputHelper _out;
    public SampleRobotTests(ITestOutputHelper output) => _out = output;

    private static string SamplesDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "samples"))) return Path.Combine(d.FullName, "samples");
        throw new DirectoryNotFoundException("samples directory not found");
    }

    private static readonly Lazy<Dictionary<string, Type>> Samples = new(() =>
    {
        var compiler = new RobotCompiler();
        var map = new Dictionary<string, Type>();
        foreach (var file in Directory.GetFiles(SamplesDir(), "*.cs"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var result = compiler.Compile(File.ReadAllText(file), "Sample_" + name);
            Assert.True(result.Success, $"{name}: " + string.Join("; ", result.Diagnostics.Select(d => $"{d.Line}:{d.Column} {d.Message}")));
            // Trusted repo samples, so loading in-process is fine here (players' robots never are).
            map[name] = Assembly.Load(result.Assembly!).GetType(result.RobotTypeName!)!;
        }
        return map;
    });

    private static IRobotController Make(string name) => new InProcessController((Robot)Activator.CreateInstance(Samples.Value[name])!);

    [Fact]
    public void AllSamplesCompile() => Assert.True(Samples.Value.Count >= 6);

    [Fact]
    public void DocsQuickStartExampleCompiles()
    {
        string docs = "";
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && docs == ""; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "src", "RobotBattle.Web", "wwwroot", "docs.html");
            if (File.Exists(f)) docs = File.ReadAllText(f);
        }
        Assert.NotEqual("", docs);
        var quick = docs.Substring(docs.IndexOf("id=\"quickstart\"", StringComparison.Ordinal));
        var start = quick.IndexOf("<pre><code>", StringComparison.Ordinal) + "<pre><code>".Length;
        var code = System.Net.WebUtility.HtmlDecode(quick.Substring(start, quick.IndexOf("</code></pre>", start, StringComparison.Ordinal) - start));
        var result = new RobotCompiler().Compile(code, "DocsQuickStart");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => $"{d.Line}: {d.Message}")));
    }

    [Theory]
    // Spinner is the deliberately crude beginner example and is not expected to reliably beat anything.
    [InlineData("Hunter")]
    [InlineData("Sentry")]
    [InlineData("Dodger")]
    [InlineData("Sniper")]
    [InlineData("Lurker")]
    public void SampleBeatsSittingDuckMostOfTheTime(string name)
    {
        int wins = 0, losses = 0;
        const int rounds = 20;
        for (int seed = 1; seed <= rounds; seed++)
        {
            var replay = new Simulation(new[] { Make(name), Make("SittingDuck") }, seed).Run();
            if (replay.WinnerId == 0) wins++; else if (replay.WinnerId == 1) losses++;
        }
        _out.WriteLine($"{name} vs Sitting Duck: {wins} wins, {losses} losses, {rounds - wins - losses} draws");
        // Obstacles can legitimately block a stationary target for a whole battle, so this is a majority, not a sweep.
        Assert.True(wins >= rounds * 0.65, $"{name} only won {wins}/{rounds} against a sitting duck");
    }

    [Fact]
    public void RoundRobinReport()
    {
        var names = Samples.Value.Keys.Where(n => n != "SittingDuck").OrderBy(n => n).ToList();
        foreach (var a in names)
            foreach (var b in names.Where(b => string.CompareOrdinal(a, b) < 0))
            {
                int wa = 0, wb = 0;
                for (int seed = 1; seed <= 20; seed++)
                {
                    var r = new Simulation(new[] { Make(a), Make(b) }, seed).Run();
                    if (r.WinnerId == 0) wa++; else if (r.WinnerId == 1) wb++;
                }
                _out.WriteLine($"{a} {wa} - {wb} {b}");
            }
    }
}
