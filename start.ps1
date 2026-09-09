# Starts Hindsight elevated (UAC prompt), building first if the executable is missing.
# Use -Show to open the window right away instead of starting into the tray only.
param([switch]$Show, [string]$Configuration = "Release")
$exe = Join-Path $PSScriptRoot "src\Hindsight.App\bin\$Configuration\net8.0-windows\Hindsight.App.exe"
if (-not (Test-Path $exe)) { dotnet build (Join-Path $PSScriptRoot "Hindsight.slnx") -c $Configuration | Out-Null }
$args = @(); if ($Show) { $args += "--show" }
Start-Process -FilePath $exe -Verb RunAs -WorkingDirectory $PSScriptRoot -ArgumentList $args
