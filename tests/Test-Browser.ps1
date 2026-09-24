$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& (Join-Path $root 'build.ps1')
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$compiler = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\Roslyn\csc.exe' | Select-Object -First 1
$output = Join-Path $root 'XwRemote/bin/Release'
$references = @('XwRemote.exe', 'XwMaxLib.dll', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'Newtonsoft.Json.dll') | ForEach-Object { '/reference:' + (Join-Path $output $_) }
$testExe = Join-Path $output 'BrowserSmokeTests.exe'
& $compiler /nologo /target:exe "/out:$testExe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll @references (Join-Path $PSScriptRoot 'BrowserSmokeTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Browser test compilation failed.' }
try {
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw 'Browser smoke tests failed.' }
} finally {
    Remove-Item -LiteralPath $testExe -ErrorAction SilentlyContinue
}
