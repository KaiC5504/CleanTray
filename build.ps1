# Builds the hook DLL (and its native tests), then the .NET solution, all Release x64.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

cmake -S . -B build -A x64
if ($LASTEXITCODE) { exit $LASTEXITCODE }

cmake --build build --config Release
if ($LASTEXITCODE) { exit $LASTEXITCODE }

dotnet build CleanTray.sln -c Release
exit $LASTEXITCODE
