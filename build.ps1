param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Installer / vswhere fehlt.' }
$msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (!$msbuild) { throw 'MSBuild fehlt. Visual Studio Build Tools mit .NET-Desktop-Buildtools installieren.' }
& $msbuild (Join-Path $PSScriptRoot 'XwRemote.sln') /restore /t:Rebuild "/p:Configuration=$Configuration" '/p:Platform=Any CPU' /nologo /verbosity:minimal
if ($LASTEXITCODE -ne 0) { throw "Build fehlgeschlagen (Exitcode $LASTEXITCODE)." }
Write-Host "Build erfolgreich: $PSScriptRoot\XwRemote\bin\$Configuration"
