param(
    [Parameter(Mandatory = $true)][string]$GameDirectory,
    [string]$ReferenceMission
)
$ErrorActionPreference = 'Stop'
$taskCompiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskGame = (Resolve-Path -LiteralPath $GameDirectory).Path
$taskReference = Join-Path $taskGame 'ScriptHookVDotNet3.dll'
& $taskCompiler /nologo /target:library /optimize+ /platform:x64 "/reference:$taskReference" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/out:$PSScriptRoot\LongRangeTargeting.dll" "$PSScriptRoot\MissionPatch.cs" "$PSScriptRoot\LongRangeTargeting.cs"
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
& $taskCompiler /nologo /target:exe "/out:$PSScriptRoot\Verify.exe" "$PSScriptRoot\MissionPatch.cs" "$PSScriptRoot\Verify.cs"
if ($LASTEXITCODE -ne 0) { throw 'Verifier compilation failed.' }
if ($ReferenceMission) {
    & "$PSScriptRoot\Verify.exe" $ReferenceMission "$taskGame\GTA5.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Verification failed.' }
} else {
    Write-Output 'Compilation passed. Reference mission checks skipped (no -ReferenceMission supplied).'
}
