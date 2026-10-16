using RobotBattle.Compiler;
using Xunit;

namespace RobotBattle.Compiler.Tests;

public class CompilerTests
{
    private static readonly RobotCompiler C = new();

    private static string Robot(string body, string usings = "") => $@"
using System; using System.Collections.Generic; using System.Linq; {usings}
using RobotBattle.Api;
public class Bot : Robot
{{
    public override string Name => ""Bot"";
    public override void Tick(RobotState s, Actions a) {{ {body} }}
}}";

    [Fact]
    public void ValidRobotCompiles()
    {
        var r = C.Compile(Robot("a.Move(2); var l = new List<int>{1,2,3}; var x = l.Where(i => i > 1).Select(i => $\"{i}\").ToArray(); a.Turn(Math.Sin(s.Heading));"));
        Assert.True(r.Success, string.Join("; ", r.Diagnostics.Select(d => d.Message)));
        Assert.Equal("Bot", r.RobotTypeName);
    }

    [Fact]
    public void SyntaxErrorsAreReportedWithLineNumbers()
    {
        var r = C.Compile(Robot("a.Move(;"));
        Assert.False(r.Success);
        Assert.All(r.Diagnostics, d => Assert.True(d.Line > 0));
    }

    [Theory]
    [InlineData("System.IO.File.ReadAllText(\"x\");")]
    [InlineData("new System.Net.Http.HttpClient();")]
    [InlineData("System.Diagnostics.Process.Start(\"cmd\");")]
    [InlineData("Environment.Exit(0);")]
    [InlineData("Console.WriteLine(1);")]
    [InlineData("var t = typeof(string); t.GetMethods();")]
    [InlineData("var t = s.GetType(); t.GetMethods();")]
    [InlineData("Activator.CreateInstance(typeof(object));")]
    [InlineData("System.Threading.Thread.Sleep(1);")]
    [InlineData("System.Reflection.Assembly.GetExecutingAssembly();")]
    [InlineData("GC.Collect();")]
    [InlineData("System.Runtime.InteropServices.Marshal.AllocHGlobal(1);")]
    [InlineData("var d = DateTime.Now;")]
    public void ForbiddenApisAreRejected(string body)
    {
        var r = C.Compile(Robot(body));
        Assert.False(r.Success);
    }

    [Fact]
    public void UnsafeCodeIsRejected()
    {
        Assert.False(C.Compile(Robot("unsafe { int x = 1; int* p = &x; }")).Success);
    }

    [Fact]
    public void PInvokeIsRejected()
    {
        var src = @"using System.Runtime.InteropServices; using RobotBattle.Api;
public class Bot : Robot {
  [DllImport(""kernel32.dll"")] static extern int GetTickCount();
  public override string Name => ""x""; public override void Tick(RobotState s, Actions a) { GetTickCount(); } }";
        Assert.False(C.Compile(src).Success);
    }

    [Fact]
    public void MissingRobotClassIsRejected()
    {
        Assert.False(C.Compile("public class Foo { }").Success);
    }

    [Fact]
    public void OversizedSourceIsRejected()
    {
        Assert.False(C.Compile(new string('/', RobotCompiler.MaxSourceBytes + 1)).Success);
    }

    [Fact]
    public void OwnClassesAndHelpersAreAllowed()
    {
        var src = @"using System.Collections.Generic; using RobotBattle.Api;
public class Helper { public int N; public static int Twice(int x) => x * 2; }
public class Bot : Robot {
  Dictionary<string,int> d = new();
  public override string Name => ""x"";
  public override void Tick(RobotState s, Actions a) { d[""k""] = Helper.Twice(3); a.Move(d[""k""]); } }";
        var r = C.Compile(src);
        Assert.True(r.Success, string.Join("; ", r.Diagnostics.Select(x => x.Message)));
    }
}
