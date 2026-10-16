param(
    [string]$Server = "robots.gibe.digital",
    [string]$User = "azureuser",                       # change if your VM admin user differs
    [string]$Key = "C:\keys\RobotBattle001_key.pem",
    [string]$Rid = "linux-x64"                         # linux-arm64 for ARM VM sizes
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$target = "$User@$Server"
$sshOpts = @("-i", $Key, "-o", "StrictHostKeyChecking=accept-new")

dotnet test
if ($LASTEXITCODE) { throw "Tests failed" }

Remove-Item -Recurse -Force artifacts/host, artifacts/web -ErrorAction SilentlyContinue
dotnet publish src/RobotBattle.Host -c Release -r $Rid --self-contained false -o artifacts/host
if ($LASTEXITCODE) { throw "Host publish failed" }
dotnet publish src/RobotBattle.Web -c Release -r $Rid --self-contained false -o artifacts/web
if ($LASTEXITCODE) { throw "Web publish failed" }

tar -czf artifacts/host.tgz -C artifacts/host .
tar -czf artifacts/web.tgz  -C artifacts/web .
scp @sshOpts artifacts/host.tgz artifacts/web.tgz docker/Host.Dockerfile "${target}:/tmp/"
if ($LASTEXITCODE) { throw "Copy failed" }

$remote = @'
set -e
# sandbox image
rm -rf /tmp/host && mkdir /tmp/host && tar -xzf /tmp/host.tgz -C /tmp/host
sudo docker build -f /tmp/Host.Dockerfile -t robot-battle-host /tmp/host

# site
sudo systemctl stop robotbattle
sudo find /opt/robotbattle -mindepth 1 -delete
sudo tar -xzf /tmp/web.tgz -C /opt/robotbattle
sudo chown -R robotbattle:robotbattle /opt/robotbattle
sudo systemctl start robotbattle

sleep 3
systemctl is-active robotbattle
curl -s -o /dev/null -w "site: HTTP %{http_code}\n" http://127.0.0.1:5004/
'@
ssh @sshOpts $target ($remote -replace "`r", "")
if ($LASTEXITCODE) { throw "Remote step failed" }
