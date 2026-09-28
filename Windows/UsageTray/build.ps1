$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'Windows .NET Framework C# compiler was not found.'
}

$references = @('/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll')
Push-Location $PSScriptRoot
try {
    & $compiler /nologo /target:winexe /out:UsageTray.exe $references UsageTray.cs
    if ($LASTEXITCODE -ne 0) { throw 'UsageTray.exe build failed.' }
    & $compiler /nologo /target:exe /out:UsageTrayCli.exe $references UsageTray.cs
    if ($LASTEXITCODE -ne 0) { throw 'UsageTrayCli.exe build failed.' }
    & .\UsageTrayCli.exe --self-test
    if ($LASTEXITCODE -ne 0) { throw 'Self-test failed.' }
}
finally { Pop-Location }
