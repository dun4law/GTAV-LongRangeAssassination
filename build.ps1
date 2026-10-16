param(
    [string]$GameDirectory,
    [string]$ReferenceMission
)
$ErrorActionPreference = 'Stop'
$taskCompiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not $GameDirectory) {
    # One source builds one DLL that runs on both editions. The reference only needs
    # ScriptHookVDotNet3.dll. Build against the Legacy (..GTAV) copy (API 3.7.0): a DLL built against the Enhanced copy (3.9.0) fails to load on Legacy.
    $parent = Split-Path -Parent $PSScriptRoot
    foreach ($candidate in @((Join-Path $parent 'GTAV'), (Join-Path $parent 'GTAVEnhanced'))) {
        if (Test-Path -LiteralPath (Join-Path $candidate 'ScriptHookVDotNet3.dll')) { $GameDirectory = $candidate; break }
    }
    if (-not $GameDirectory) { throw 'Pass -GameDirectory (a GTA V Legacy or Enhanced folder that contains ScriptHookVDotNet3.dll).' }
}
$taskGame = (Resolve-Path -LiteralPath $GameDirectory).Path
$taskReference = Join-Path $taskGame 'ScriptHookVDotNet3.dll'
& $taskCompiler /nologo /target:library /optimize+ /platform:x64 "/reference:$taskReference" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/out:$PSScriptRoot\LongRangeTargeting.dll" "$PSScriptRoot\MissionPatch.cs" "$PSScriptRoot\LongRangeTargeting.cs"
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
& $taskCompiler /nologo /target:exe "/out:$PSScriptRoot\Verify.exe" "$PSScriptRoot\MissionPatch.cs" "$PSScriptRoot\Verify.cs"
if ($LASTEXITCODE -ne 0) { throw 'Verifier compilation failed.' }
if ($ReferenceMission) {
    $taskExe = Join-Path $taskGame 'GTA5_Enhanced.exe'
    if (-not (Test-Path -LiteralPath $taskExe)) { $taskExe = Join-Path $taskGame 'GTA5.exe' }
    & "$PSScriptRoot\Verify.exe" $ReferenceMission $taskExe
    if ($LASTEXITCODE -ne 0) { throw 'Verification failed.' }
} else {
    Write-Output 'Compilation passed. Reference mission checks skipped (no -ReferenceMission supplied).'
}