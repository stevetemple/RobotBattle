using Microsoft.EntityFrameworkCore;
using RobotBattle.Engine;

namespace RobotBattle.Web;

public class Player
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class RobotEntry
{
    public int Id { get; set; }
    public int PlayerId { get; set; }
    public Player? Player { get; set; }
    public string Label { get; set; } = "";
    public string Source { get; set; } = "";
    public string TypeName { get; set; } = "";
    public byte[] Assembly { get; set; } = Array.Empty<byte>();
    public DateTime CreatedUtc { get; set; }

    // Leaderboard stats, updated when a battle completes.
    public double Rating { get; set; } = Scoring.StartRating;
    public int Matches { get; set; }
    public int MatchWins { get; set; }
    public int RoundsPlayed { get; set; }
    public int RoundWins { get; set; }
    public int RoundDraws { get; set; }
}

public static class BattleStatus
{
    public const string Queued = "queued", Running = "running", Done = "done", Failed = "failed";
}

public class Battle
{
    public int Id { get; set; }
    public int RequestedByPlayerId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string Status { get; set; } = BattleStatus.Queued;
    public int Seed { get; set; }
    /// <summary>Comma separated robot ids, in spawn/slot order.</summary>
    public string RobotIds { get; set; } = "";
    public int? WinnerRobotId { get; set; }
    public string? EndReason { get; set; }
    public string? Error { get; set; }
    public byte[]? ReplayGz { get; set; }
    /// <summary>A battle is a match of this many rounds.</summary>
    public int Rounds { get; set; } = 1;
    public int RoundsDone { get; set; }
    /// <summary>JSON <see cref="MatchResult"/> once the battle is done.</summary>
    public string? ResultJson { get; set; }
}

public class AppDb : DbContext
{
    public AppDb(DbContextOptions<AppDb> options) : base(options) { }
    public DbSet<Player> Players => Set<Player>();
    public DbSet<RobotEntry> Robots => Set<RobotEntry>();
    public DbSet<Battle> Battles => Set<Battle>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Player>().HasIndex(p => p.Name).IsUnique();
        b.Entity<Battle>().HasIndex(x => x.CreatedUtc);
    }
}

/// <summary>EnsureCreated never alters an existing database, so add columns introduced since the first release.</summary>
public static class SchemaUpgrade
{
    private static readonly (string Table, string Column, string Ddl)[] Columns =
    {
        ("Robots", "Rating", "REAL NOT NULL DEFAULT 1000"),
        ("Robots", "Matches", "INTEGER NOT NULL DEFAULT 0"),
        ("Robots", "MatchWins", "INTEGER NOT NULL DEFAULT 0"),
        ("Robots", "RoundsPlayed", "INTEGER NOT NULL DEFAULT 0"),
        ("Robots", "RoundWins", "INTEGER NOT NULL DEFAULT 0"),
        ("Robots", "RoundDraws", "INTEGER NOT NULL DEFAULT 0"),
        ("Battles", "Rounds", "INTEGER NOT NULL DEFAULT 1"),
        ("Battles", "RoundsDone", "INTEGER NOT NULL DEFAULT 0"),
        ("Battles", "ResultJson", "TEXT NULL"),
    };

    public static void Apply(AppDb db)
    {
        var conn = db.Database.GetDbConnection();
        conn.Open();
        try
        {
            foreach (var (table, column, ddl) in Columns)
            {
                using var check = conn.CreateCommand();
                check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
                if (Convert.ToInt32(check.ExecuteScalar()) > 0) continue;
                using var alter = conn.CreateCommand();
                alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {ddl}";
                alter.ExecuteNonQuery();
            }
        }
        finally { conn.Close(); }
    }
}
