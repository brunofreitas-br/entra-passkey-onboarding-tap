$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
dotnet restore (Join-Path $root 'TapPasskeyOnboarding.slnx')
dotnet build (Join-Path $root 'TapPasskeyOnboarding.slnx') --configuration Release --no-restore
dotnet test (Join-Path $root 'TapPasskeyOnboarding.slnx') --configuration Release --no-build --logger 'console;verbosity=normal'
