param([string]$Tag = '')

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$build = Join-Path $root 'XwRemote/bin/Release'
$exe = Join-Path $build 'XwRemote.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'Build Release first using build.ps1.' }
$version = [Reflection.AssemblyName]::GetAssemblyName($exe).Version.ToString()
if ($version -eq '0.0.0.0') { throw 'Debug binaries cannot be published.' }
if ($Tag -and $Tag -cne "v$version") { throw "Tag '$Tag' does not match assembly version v$version." }

$required = @('XwRemote.exe', 'XwRemote.exe.config', 'Newtonsoft.Json.dll',
    'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll',
    'x86/SQLite.Interop.dll', 'x64/SQLite.Interop.dll',
    'runtimes/win-x86/native/WebView2Loader.dll', 'runtimes/win-x64/native/WebView2Loader.dll',
    'putty/putty.exe', 'putty/plink.exe', 'putty/puttygen.exe')
foreach ($file in $required) {
    if (!(Test-Path -LiteralPath (Join-Path $build $file))) { throw "Missing release file: $file" }
}

$artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$stage = Join-Path $root ('obj/release-package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
# Allowlist application files: never ship local databases, credentials or test executables.
Copy-Item -LiteralPath $exe,(Join-Path $build 'XwRemote.exe.config') -Destination $stage
Get-ChildItem -LiteralPath $build -File -Filter '*.dll' | Copy-Item -Destination $stage
foreach ($directory in @('x86', 'x64', 'runtimes', 'putty')) {
    $source = Join-Path $build $directory
    Get-ChildItem -LiteralPath $source -File -Recurse | Where-Object { $_.Extension -in '.dll', '.exe' } | ForEach-Object {
        $relative = $_.FullName.Substring($build.Length + 1)
        $destination = Join-Path $stage $relative
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $destination
    }
}
Copy-Item -LiteralPath (Join-Path $root 'LICENSE'),(Join-Path $root 'README.md'),(Join-Path $root 'XwRemote/Credits.rtf') -Destination $stage
# Existing in-app updater expects this filename and XwRemote.exe at archive root.
$zip = Join-Path $artifacts "XwRemote.v$version.zip"
if (Test-Path -LiteralPath $zip) { throw "Package already exists: $zip" }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$zip.sha256" -Encoding ASCII -Value "$hash  $([IO.Path]::GetFileName($zip))"
Write-Host "Release package: $zip"
