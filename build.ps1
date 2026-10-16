$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x compiler is required.' }
$references = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Runtime.Serialization.dll','System.Xaml.dll','WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll')
$arguments = @('/nologo','/target:winexe','/platform:anycpu','/optimize+','/utf8output',('/out:' + (Join-Path $projectRoot 'Frieren.exe')),('/win32manifest:' + (Join-Path $projectRoot 'src\app.manifest')),('/resource:' + (Join-Path $projectRoot 'assets\frieren-atlas.png') + ',Frieren.Atlas'))
foreach ($reference in $references) {
    $file = Join-Path $framework $reference
    if (-not (Test-Path -LiteralPath $file)) { $file = Join-Path (Join-Path $framework 'WPF') $reference }
    $arguments += '/reference:' + $file
}
if (Test-Path -LiteralPath (Join-Path $projectRoot 'assets\frieren.ico')) { $arguments += '/win32icon:' + (Join-Path $projectRoot 'assets\frieren.ico') }
$arguments += Join-Path $projectRoot 'src\Frieren.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
Write-Host 'Built Frieren.exe. No Python, Node, API key, or internet is needed to run.'
