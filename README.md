# Robot Battle

Players write a C# robot, upload it to a website, and watch it fight other players' robots in an arena. The server compiles each submission, runs every robot in its own locked-down container, simulates the battle, and the browser replays it.

Player-facing rules and API reference: `src/RobotBattle.Web/wwwroot/docs.html` (served at `/docs.html`).

## Layout

| Project | Purpose |
| --- | --- |
| `src/RobotBattle.Api` | The contract robot authors compile against: `Robot`, `RobotState`, `Actions`, `ScanResult`, `ScanHit`. |
| `src/RobotBattle.Engine` | Deterministic simulation (`Simulation`), tunable constants (`Rules`), replay model, and the controllers that talk to robots (`InProcessController`, `ProcessController`, `DockerHost`). No web or I/O dependencies beyond launching processes. |
| `src/RobotBattle.Compiler` | Roslyn compile with a namespace allowlist and an IL re-check. First line of defence only. |
| `src/RobotBattle.Host` | Tiny console app that loads one compiled robot and serves ticks over stdin/stdout. Runs inside the container. |
| `src/RobotBattle.Web` | ASP.NET Core app: name login, upload, battle queue, SQLite storage, replay viewer (`wwwroot`). |
| `samples/` | Player-facing sample robots. Copied into the web app and tested in CI like any other code. |
| `docker/Host.Dockerfile` | The sandbox image. |
| `tests/` | Engine, compiler, sandbox (process, Docker, samples) tests. |

## How a battle runs

1. `POST /api/robots` compiles the source (never loads it in the web process) and stores the DLL.
2. `POST /api/battles` queues a battle of N rounds (default 5, max 10). `BattleRunner` takes it and writes each robot's DLL to its own directory under `App_Data/work`.
3. For **each round** the runner starts a fresh sandbox per robot (in parallel), runs a `Simulation` with a new seed derived from the battle seed, and disposes the sandboxes. `Simulation` calls each robot every tick through `ProcessController` (one JSON line each way). Timeouts, crashes, bad output and out-of-memory kill the robot's sandbox and disqualify it for that round.
4. Each round ranks the robots (`Replay.Places`). `Scoring` turns that into round scores (match points) and pairwise Elo changes (`Scoring.EloDeltas`, K=32, start 1000). Ratings and stats on `RobotEntry` are updated once, when the match finishes.
5. All round replays plus the match result are gzipped into SQLite. The browser fetches `/api/battles/{id}/replay` and `wwwroot/viewer.js` plays each round; `/api/leaderboard` feeds the leaderboard table.

## Running it

Requirements: .NET 10 SDK. Docker for the real sandbox.

```powershell
# one-off: build the sandbox image
dotnet publish src/RobotBattle.Host -c Release -o artifacts/host
docker build -f docker/Host.Dockerfile -t robot-battle-host artifacts/host

# run the site
dotnet run --project src/RobotBattle.Web --urls http://localhost:5004
```

Rebuild the image whenever `RobotBattle.Host`, `RobotBattle.Api` or the `Engine` protocol changes.

### Configuration (`appsettings.json`)

```json
{
  "Sandbox": { "Mode": "Docker", "Image": "robot-battle-host", "TickBudgetMs": 200, "MemoryMb": 192 },
  "Database": { "Path": "App_Data/robotbattle.db" }
}
```

`"Mode": "Process"` runs robots as plain child processes. That is **not a security boundary**: use it only for local development without Docker.

### Container limits (`DockerHost.Launch`)

No network, read-only root filesystem, all capabilities dropped, no new privileges, non-root user, memory and swap capped (default 192 MB), 1 CPU, 32 processes, 64 open files, robot DLL mounted read-only.

## Tests

```powershell
dotnet test
```

The Docker tests skip themselves if Docker or the `robot-battle-host` image is missing.

## Known limitations

- Login is a name field with no password: anyone can use any name. Fine for a trusted event, not for the open internet.
- Battles run one at a time on a single machine.
- The leaderboard ranks robots, not players, and every battle counts toward ratings (no separate practice mode).
- Databases created by earlier versions are upgraded in place on startup (`SchemaUpgrade`); there are no migrations beyond adding columns.
- The compiler allowlist is not a security boundary; the container is.
