
$ErrorActionPreference = "Stop"

$src = Join-Path $PSScriptRoot "EuroScopeRunwayPresets.cs"
$config = Join-Path $PSScriptRoot "runways.json"
$manifest = Join-Path $PSScriptRoot "EuroScopeRunwayPresets.exe.manifest"
$outDir = Join-Path $PSScriptRoot "build"
$out = Join-Path $outDir "EuroScopeRunwayPresets.exe"

if (!(Test-Path $config)) {
    throw "Could not find runway configuration: $config"
}
if (!(Test-Path $manifest)) {
    throw "Could not find application manifest: $manifest"
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Find-GacAssembly([string]$name) {
    $roots = @(
        "$env:WINDIR\Microsoft.NET\assembly\GAC_MSIL\$name",
        "$env:WINDIR\assembly\GAC_MSIL\$name"
    )
    foreach ($root in $roots) {
        if (Test-Path $root) {
            $dll = Get-ChildItem -Path $root -Recurse -Filter "$name.dll" -ErrorAction SilentlyContinue |
                   Select-Object -First 1
            if ($dll) { return $dll.FullName }
        }
    }
    return $null
}

$uiaClient = Find-GacAssembly "UIAutomationClient"
$uiaTypes  = Find-GacAssembly "UIAutomationTypes"
$windowsBase = Find-GacAssembly "WindowsBase"

if (!$uiaClient -or !$uiaTypes -or !$windowsBase) {
    throw "Could not locate UI Automation assemblies."
}

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (!(Test-Path $csc)) {
    $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (!(Test-Path $csc)) {
    throw "Could not find .NET Framework C# compiler."
}

& $csc /nologo /target:winexe /platform:anycpu `
    /win32manifest:"$manifest" `
    /reference:"$uiaClient" `
    /reference:"$uiaTypes" `
    /reference:"$windowsBase" `
    /reference:System.Runtime.Serialization.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Drawing.dll `
    /out:"$out" "$src"

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Copy-Item -Force $config (Join-Path $outDir "runways.json")

Write-Host ""
Write-Host "Built successfully:"
Write-Host "  $out"
Write-Host "  $(Join-Path $outDir 'runways.json')"
