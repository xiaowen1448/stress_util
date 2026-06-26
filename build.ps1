param(
  [ValidateSet("Release","Debug")]
  [string]$Configuration = "Release",

  [switch]$Clean
)

# Target: .NET Framework 4.8 (compatible with Windows Server 2012 / 2008 R2 SP1 and newer).
# Output is framework-dependent exe + dll; the target machine only needs the
# .NET Framework 4.8 runtime installed. No RID / self-contained / single-file
# (those are .NET Core/5+ concepts and do not apply to .NET Framework).

$ErrorActionPreference = "Stop"
function Write-Info([string]$msg) { Write-Host "[build] $msg" }

$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$Dist = Join-Path $RepoRoot "dist"

$Projects = @(
  @{ Name = "StressUtil";      Proj = "StressUtil\StressUtil.csproj" },
  @{ Name = "CpuStressWin";    Proj = "CpuStressWin\CpuStressWin.csproj" },
  @{ Name = "CpuStressWinGui"; Proj = "CpuStressWinGui\CpuStressWinGui.csproj" }
)

if ($Clean) {
  Write-Info "clean bin/obj/dist"
  Get-ChildItem -Force -ErrorAction SilentlyContinue -Path $RepoRoot -Directory |
    Where-Object { $_.Name -in @("CpuStressCore","CpuStressWin","CpuStressWinGui","StressUtil") } |
    ForEach-Object {
      Remove-Item -Recurse -Force -ErrorAction SilentlyContinue (Join-Path $_.FullName "bin")
      Remove-Item -Recurse -Force -ErrorAction SilentlyContinue (Join-Path $_.FullName "obj")
    }
  Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $Dist
}

$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$outRoot = Join-Path $Dist ("package_" + $stamp)
New-Item -ItemType Directory -Force -Path $outRoot | Out-Null

Write-Info "configuration=$Configuration target=net48 (Windows Server 2012+)"

foreach ($p in $Projects) {
  $outDir = Join-Path $outRoot $p.Name
  New-Item -ItemType Directory -Force -Path $outDir | Out-Null
  Write-Info ("dotnet publish " + $p.Name)
  & dotnet publish (Join-Path $RepoRoot $p.Proj) -c $Configuration -f net48 -o $outDir
  if ($LASTEXITCODE -ne 0) { throw "build failed: $($p.Name)" }
}

$zipPath = Join-Path $Dist ("stress_util_net48_" + $stamp + ".zip")
Write-Info "create zip: $zipPath"
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
Compress-Archive -Path (Join-Path $outRoot "*") -DestinationPath $zipPath

Write-Info "done"
Write-Info "output folder: $outRoot"
Write-Info "output zip:    $zipPath"
Write-Info "Target machine needs the .NET Framework 4.8 runtime (built-in on Win10 / Server 2019+)."
