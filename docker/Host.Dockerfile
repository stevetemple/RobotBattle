# Sandbox image for untrusted robots. Build context is the `dotnet publish` output of RobotBattle.Host:
#   dotnet publish src/RobotBattle.Host -c Release -o artifacts/host
#   docker build -f docker/Host.Dockerfile -t robot-battle-host artifacts/host
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY . /app
# No diagnostics IPC socket, no telemetry, no tiered-PGO instrumentation writing to disk.
ENV DOTNET_EnableDiagnostics=0 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_TieredPGO=0 DOTNET_gcServer=0 DOTNET_GCHeapHardLimitPercent=70
USER 65534:65534
ENTRYPOINT ["dotnet", "/app/RobotBattle.Host.dll"]
