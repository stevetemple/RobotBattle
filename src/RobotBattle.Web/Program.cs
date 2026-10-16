using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using RobotBattle.Compiler;
using RobotBattle.Engine;
using RobotBattle.Web;

const int MaxRobotsPerPlayer = 20;
const int MaxQueuedBattles = 20;
const int MaxBattleRobots = 6;
const int DefaultRounds = 5;
const int MaxRounds = 10;
var nameRule = new Regex(@"^[\p{L}\p{N} _.\-]{2,24}$");

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 256 * 1024);

var dbPath = builder.Configuration["Database:Path"] ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "robotbattle.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
builder.Services.AddDbContext<AppDb>(o => o.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddSingleton(builder.Configuration.GetSection("Sandbox").Get<SandboxOptions>() ?? new SandboxOptions());
builder.Services.AddSingleton<BattleQueue>();
builder.Services.AddSingleton<RobotCompiler>();
builder.Services.AddHostedService<BattleRunner>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "rb.player";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.ExpireTimeSpan = TimeSpan.FromDays(14);
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var startupDb = scope.ServiceProvider.GetRequiredService<AppDb>();
    startupDb.Database.EnsureCreated();
    SchemaUpgrade.Apply(startupDb);   // bring databases created by earlier versions up to date
}

app.UseDefaultFiles();
// Revalidate on every load so edits to the page scripts show up without a hard refresh.
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache" });
app.UseAuthentication();
app.UseAuthorization();

static int PlayerId(ClaimsPrincipal u) => int.Parse(u.FindFirstValue(ClaimTypes.NameIdentifier)!);

// ---- session ("just a name" login: no password, so anyone can claim any name) ----
app.MapPost("/api/login", async (LoginRequest req, AppDb db, HttpContext http) =>
{
    var name = (req.Name ?? "").Trim();
    if (!nameRule.IsMatch(name)) return Results.BadRequest(new { error = "Name must be 2-24 letters, numbers, spaces, _ . or -." });
    var player = await db.Players.FirstOrDefaultAsync(p => p.Name == name);
    if (player == null)
    {
        player = new Player { Name = name };
        db.Players.Add(player);
        await db.SaveChangesAsync();
    }
    var id = new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.NameIdentifier, player.Id.ToString()), new Claim(ClaimTypes.Name, player.Name),
    }, CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(new ClaimsPrincipal(id));
    return Results.Ok(new { id = player.Id, name = player.Name });
});

app.MapPost("/api/logout", async (HttpContext http) => { await http.SignOutAsync(); return Results.NoContent(); });

app.MapGet("/api/me", (ClaimsPrincipal u) =>
    u.Identity?.IsAuthenticated == true ? Results.Ok(new { id = PlayerId(u), name = u.Identity.Name }) : Results.Unauthorized());

// ---- robots ----
app.MapPost("/api/robots", async (UploadRequest req, ClaimsPrincipal u, AppDb db, RobotCompiler compiler) =>
{
    var label = (req.Label ?? "").Trim();
    if (!nameRule.IsMatch(label)) return Results.BadRequest(new { error = "Robot name must be 2-24 letters, numbers, spaces, _ . or -." });
    var pid = PlayerId(u);
    if (await db.Robots.CountAsync(r => r.PlayerId == pid) >= MaxRobotsPerPlayer)
        return Results.BadRequest(new { error = $"You can have at most {MaxRobotsPerPlayer} robots; delete one first." });

    // Compiling runs Roslyn in this process but never loads or executes the result.
    var result = await Task.Run(() => compiler.Compile(req.Source ?? ""));
    if (!result.Success) return Results.BadRequest(new { error = "Compilation failed.", diagnostics = result.Diagnostics });

    var robot = new RobotEntry
    {
        PlayerId = pid, Label = label, Source = req.Source!, TypeName = result.RobotTypeName!,
        Assembly = result.Assembly!, CreatedUtc = DateTime.UtcNow,
    };
    db.Robots.Add(robot);
    await db.SaveChangesAsync();
    return Results.Ok(new { id = robot.Id, label = robot.Label });
}).RequireAuthorization();

app.MapGet("/api/robots", async (ClaimsPrincipal u, AppDb db) =>
{
    var me = PlayerId(u);
    return await db.Robots.OrderByDescending(r => r.Id)
        .Select(r => new { r.Id, r.Label, owner = r.Player!.Name, mine = r.PlayerId == me, r.CreatedUtc }).ToListAsync();
}).RequireAuthorization();

app.MapGet("/api/robots/{id:int}/source", async (int id, ClaimsPrincipal u, AppDb db) =>
{
    var r = await db.Robots.FindAsync(id);
    return r == null || r.PlayerId != PlayerId(u) ? Results.NotFound() : Results.Text(r.Source);
}).RequireAuthorization();

app.MapDelete("/api/robots/{id:int}", async (int id, ClaimsPrincipal u, AppDb db) =>
{
    var r = await db.Robots.FindAsync(id);
    if (r == null || r.PlayerId != PlayerId(u)) return Results.NotFound();
    db.Robots.Remove(r);
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization();

// ---- battles ----
app.MapPost("/api/battles", async (BattleRequest req, ClaimsPrincipal u, AppDb db, BattleQueue queue) =>
{
    var ids = (req.RobotIds ?? new()).ToList();
    if (ids.Count < 2 || ids.Count > MaxBattleRobots) return Results.BadRequest(new { error = $"Pick between 2 and {MaxBattleRobots} robots." });
    if (ids.Distinct().Count() != ids.Count) return Results.BadRequest(new { error = "Each robot can only appear once." });
    var rounds = req.Rounds ?? DefaultRounds;
    if (rounds < 1 || rounds > MaxRounds) return Results.BadRequest(new { error = $"Rounds must be between 1 and {MaxRounds}." });
    if (await db.Robots.CountAsync(r => ids.Contains(r.Id)) != ids.Count) return Results.BadRequest(new { error = "Unknown robot." });
    if (await db.Battles.CountAsync(b => b.Status == BattleStatus.Queued || b.Status == BattleStatus.Running) >= MaxQueuedBattles)
        return Results.StatusCode(429);

    var battle = new Battle
    {
        RequestedByPlayerId = PlayerId(u), CreatedUtc = DateTime.UtcNow,
        Seed = req.Seed ?? Random.Shared.Next(), RobotIds = string.Join(',', ids), Rounds = rounds,
    };
    db.Battles.Add(battle);
    await db.SaveChangesAsync();
    queue.Enqueue(battle.Id);
    return Results.Ok(new { id = battle.Id });
}).RequireAuthorization();

app.MapGet("/api/battles", async (AppDb db) =>
{
    var battles = await db.Battles.OrderByDescending(b => b.Id).Take(30)
        .Select(b => new { b.Id, b.Status, b.RobotIds, b.WinnerRobotId, b.Error, b.CreatedUtc, b.Rounds, b.RoundsDone, b.ResultJson }).ToListAsync();
    var allIds = battles.SelectMany(b => b.RobotIds.Split(',').Select(int.Parse)).Distinct().ToList();
    var names = await db.Robots.Where(r => allIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Label);
    var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    return battles.Select(b =>
    {
        var result = b.ResultJson == null ? null : JsonSerializer.Deserialize<MatchResult>(b.ResultJson, web);
        return new
        {
            b.Id, b.Status, b.Error, b.CreatedUtc, b.Rounds, b.RoundsDone,
            robots = b.RobotIds.Split(',').Select(int.Parse).Select(i => names.TryGetValue(i, out var n) ? n : "(deleted)"),
            winner = b.WinnerRobotId is int w && names.TryGetValue(w, out var wn) ? wn : null,
            // Points per robot, best first, e.g. Hunter 3.5, Dodger 1.5
            scores = result?.Robots.OrderBy(r => r.Place).Select(r => new { r.Label, r.Points }),
        };
    });
}).RequireAuthorization();

app.MapGet("/api/battles/{id:int}", async (int id, AppDb db) =>
{
    var b = await db.Battles.Where(x => x.Id == id).Select(x => new { x.Id, x.Status, x.Error, x.Rounds, x.RoundsDone }).FirstOrDefaultAsync();
    return b == null ? Results.NotFound() : Results.Ok(b);
}).RequireAuthorization();

app.MapGet("/api/leaderboard", async (ClaimsPrincipal u, AppDb db) =>
{
    var me = PlayerId(u);
    var rows = await db.Robots.Where(r => r.RoundsPlayed > 0)
        .OrderByDescending(r => r.Rating).ThenByDescending(r => r.RoundsPlayed).ThenBy(r => r.Id)
        .Select(r => new
        {
            r.Id, r.Label, owner = r.Player!.Name, mine = r.PlayerId == me, rating = r.Rating,
            r.Matches, r.MatchWins, r.RoundsPlayed, r.RoundWins, r.RoundDraws,
            roundLosses = r.RoundsPlayed - r.RoundWins - r.RoundDraws,
        }).ToListAsync();
    return rows.Select((r, i) => new { rank = i + 1, r.Id, r.Label, r.owner, r.mine, rating = Math.Round(r.rating), r.Matches, r.MatchWins, r.RoundsPlayed, r.RoundWins, r.RoundDraws, r.roundLosses });
}).RequireAuthorization();

app.MapGet("/api/battles/{id:int}/replay", async (int id, AppDb db, HttpContext http) =>
{
    var gz = await db.Battles.Where(x => x.Id == id).Select(x => x.ReplayGz).FirstOrDefaultAsync();
    if (gz == null) return Results.NotFound();
    http.Response.Headers.ContentEncoding = "gzip";
    return Results.Bytes(gz, "application/json");
}).RequireAuthorization();

// ---- sample robots (source files copied from /samples at build time) ----
string SamplesDir() => Path.Combine(AppContext.BaseDirectory, "samples");
app.MapGet("/api/samples", () =>
    Directory.Exists(SamplesDir())
        ? Directory.GetFiles(SamplesDir(), "*.cs").Select(f => Path.GetFileNameWithoutExtension(f)).OrderBy(n => n).ToArray()
        : Array.Empty<string>());
app.MapGet("/api/samples/{name}", (string name) =>
{
    // Only names that actually exist in the samples folder are served: no path tricks.
    var file = Directory.Exists(SamplesDir()) ? Directory.GetFiles(SamplesDir(), "*.cs").FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == name) : null;
    return file == null ? Results.NotFound() : Results.Text(File.ReadAllText(file));
});

// Built-in sample robots, handy before anyone has uploaded anything.
app.MapGet("/api/demo", (int? seed) =>
{
    var controllers = new IRobotController[]
    {
        new InProcessController(new SampleRobots.Hunter("Hunter")),
        new InProcessController(new SampleRobots.Spinner()),
        new InProcessController(new SampleRobots.Hunter("Hunter 2")),
    };
    return Results.Json(new Simulation(controllers, seed ?? Random.Shared.Next()).Run());
});

app.Run();

record LoginRequest(string? Name);
record UploadRequest(string? Label, string? Source);
record BattleRequest(List<int>? RobotIds, int? Seed, int? Rounds);
