# Publishes a self-contained, single-file Hindsight.App.exe into .\dist.
# The result runs on any Windows x64 machine without installing the .NET runtime.
param([string]$Runtime = "win-x64")
Set-Location $PSScriptRoot
dotnet publish src\Hindsight.App\Hindsight.App.csproj -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist
if ($LASTEXITCODE -eq 0) { Get-Item dist\Hindsight.App.exe | Select-Object FullName, @{n='MB';e={[math]::Round($_.Length/1MB,1)}} }
