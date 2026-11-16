#requires -Version 7.0
<#
.SYNOPSIS
    Runs i1rr-unity-mcp package tests via Unity Test Runner CLI.

.DESCRIPTION
    Executes EditMode and/or PlayMode tests for the I1rr.UnityMcp assemblies.
    EditMode uses batch mode; PlayMode opens a graphical Editor for framebuffer
    and UI tests that require WaitForEndOfFrame. Requires PowerShell 7+.
    Outputs NUnit XML results suitable for CI consumption.

.PARAMETER UnityPath
    Full path to Unity.exe (e.g., "C:\Program Files\Unity\Hub\Editor\2022.3.19f1\Editor\Unity.exe")

.PARAMETER ProjectPath
    Full path to the Unity project containing the i1rr-unity-mcp package.

.PARAMETER Platform
    Which test platform to run: EditMode, PlayMode, or Both (default).

.PARAMETER Filter
    Optional test name filter (supports wildcards).

.PARAMETER OutputDir
    Directory for test result XML files. Defaults to ./TestResults~.

.EXAMPLE
    .\run-tests.ps1 -UnityPath "C:\Program Files\Unity\Hub\Editor\2022.3.19f1\Editor\Unity.exe" -ProjectPath "C:\Unity\Games"
#>
param(
    [Parameter(Mandatory=$true)]
    [string]$UnityPath,

    [Parameter(Mandatory=$true)]
    [string]$ProjectPath,

    [ValidateSet("EditMode", "PlayMode", "Both")]
    [string]$Platform = "Both",

    [string]$Filter = "",
    [ValidateSet("Default", "D3D11")]
    [string]$GraphicsApi = "Default",
    [string]$OutputDir = "./TestResults~"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $UnityPath)) {
    Write-Error "Unity not found at: $UnityPath"
    exit 1
}

if (-not (Test-Path $ProjectPath)) {
    Write-Error "Project not found at: $ProjectPath"
    exit 1
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

function Run-Tests($testPlatform) {
    $resultsFile = Join-Path (Resolve-Path $OutputDir).Path "$testPlatform-results.xml"
    $assembly = if ($testPlatform -eq "EditMode") { "I1rr.UnityMcp.Editor.Tests" } else { "I1rr.UnityMcp.Runtime.Tests" }
    $logFile = Join-Path $OutputDir "$testPlatform-log.txt"
    $unityArgs = @(
        "-runTests",
        "-projectPath", $ProjectPath,
        "-testPlatform", $testPlatform,
        "-testResults", $resultsFile,
        "-logFile", $logFile,
        "-assemblyNames", $assembly
    )
    if ($testPlatform -eq "EditMode") {
        $unityArgs += "-batchmode"
    }
    if ($Filter) {
        $unityArgs += "-testFilter"
        $unityArgs += $Filter
    }
    if ($GraphicsApi -eq "D3D11") { $unityArgs += "-force-d3d11" }

    Write-Host "[$testPlatform] Starting tests..." -ForegroundColor Cyan
    Write-Host "  Editor: $(if ($testPlatform -eq 'EditMode') { 'batch' } else { 'graphical (Game View must remain visible)' })"
    Write-Host "  Results: $resultsFile"
    Write-Host "  Log:     $logFile"

    $started = [DateTime]::UtcNow
    $startInfo = [Diagnostics.ProcessStartInfo]::new($UnityPath)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $testPlatform -eq "EditMode"
    foreach ($argument in $unityArgs) { $startInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($startInfo)
    $process.WaitForExit()

    $result = Get-Item -LiteralPath $resultsFile -ErrorAction SilentlyContinue
    if ($process.ExitCode -eq 0 -and $result -and $result.LastWriteTimeUtc -ge $started) {
        $run = ([xml](Get-Content -LiteralPath $resultsFile -Raw)).'test-run'
        if ([int]$run.passed -eq 0 -or [int]$run.failed -gt 0) { return $false }
        Write-Host "[$testPlatform] PASSED" -ForegroundColor Green
        return $true
    }
    else {
        Write-Host "[$testPlatform] FAILED (exit code: $($process.ExitCode))" -ForegroundColor Red
        return $false
    }
}

$success = $true

if ($Platform -eq "EditMode" -or $Platform -eq "Both") {
    if (-not (Run-Tests "EditMode")) { $success = $false }
}

if ($Platform -eq "PlayMode" -or $Platform -eq "Both") {
    if (-not (Run-Tests "PlayMode")) { $success = $false }
}

# Print summary from result XMLs
Write-Host "`n=== Test Summary ===" -ForegroundColor Yellow
Get-ChildItem $OutputDir -Filter "*-results.xml" -ErrorAction SilentlyContinue | ForEach-Object {
    if (Test-Path $_.FullName) {
        [xml]$xml = Get-Content $_.FullName
        $run = $xml."test-run"
        if ($run) {
            $name = $_.BaseName -replace '-results$', ''
            Write-Host "  $name`: Total=$($run.total) Passed=$($run.passed) Failed=$($run.failed) Skipped=$($run.skipped)"
        }
    }
}

if (-not $success) {
    Write-Host "`nOne or more test platforms failed." -ForegroundColor Red
    exit 1
}
else {
    Write-Host "`nAll tests passed." -ForegroundColor Green
    exit 0
}
