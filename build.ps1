$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$compilerCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) {
    throw 'The .NET Framework C# compiler was not found. Install the .NET Framework 4 developer tools and try again.'
}

$outputDirectory = Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$outputPath = Join-Path $outputDirectory 'KeyboardOverlay.exe'
& $compiler /nologo /target:winexe /optimize+ /platform:x64 `
    /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
    "/win32icon:$(Join-Path $projectRoot 'smiley.ico')" "/out:$outputPath" `
    (Join-Path $projectRoot 'KeyboardOverlay.cs')
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}

Write-Output "Built $outputPath"
