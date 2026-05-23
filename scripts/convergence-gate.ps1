#!/usr/bin/env pwsh
#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Write-Host "=== Convergence Gate ===" -ForegroundColor Cyan

$repoRoot = (Resolve-Path "$PSScriptRoot/..").Path
Set-Location $repoRoot

$failed = $false

function Fail($msg) {
    Write-Host "❌ $msg" -ForegroundColor Red
    $script:failed = $true
}

# 1. DxParser must only be referenced in DxDispatcher.cs
Write-Host "`n[1/4] Checking DxParser usage..." -ForegroundColor Yellow
$parserRefs = Get-ChildItem -Recurse -Filter *.cs -Path src |
    Where-Object { $_.Name -ne 'DxParser.cs' -and $_.Name -ne 'DxDispatcher.cs' } |
    Select-String -Pattern 'DxParser\.(Parse|TryParse)' |
    Select-Object Path,LineNumber,Line -Unique

if ($parserRefs) {
    Fail "DxParser referenced outside DxDispatcher.cs"
    $parserRefs | ForEach-Object { Write-Host "  $($_.Path):$($_.LineNumber) $($_.Line.Trim())" }
} else {
    Write-Host "✓ No illegal DxParser references" -ForegroundColor Green
}

# 2. CLI must not reference DxRuntime execution methods
# OpenAsync, IsWorkspace, Create are ALLOWED (lifecycle only)
Write-Host "`n[2/4] Checking CLI DxRuntime execution references..." -ForegroundColor Yellow
$runtimeRefs = Get-ChildItem -Recurse -Filter *.cs -Path src/Dx.Cli |
    Select-String -Pattern 'DxRuntime\.(ApplyAsync|CheckoutAsync|Materialize|MaterializeSnap|ToolAsync|Execute)' |
    Select-Object Path,LineNumber,Line -Unique

if ($runtimeRefs) {
    Fail "CLI references DxRuntime execution methods"
    $runtimeRefs | ForEach-Object { Write-Host "  $($_.Path):$($_.LineNumber) $($_.Line.Trim())" }
} else {
    Write-Host "✓ No CLI DxRuntime execution references (OpenAsync allowed)" -ForegroundColor Green
}

# 3. CLI must not branch on document semantics
Write-Host "`n[3/4] Checking CLI semantic branching..." -ForegroundColor Yellow
$semanticPatterns = @(
    'IsDryRun',
    '\.IsMutating\b',
    'if\s*\(\s*document',
    'OnBaseMismatch\s*==',
    'if\s*\(\s*isDryRun'
)
$semanticRefs = Get-ChildItem -Recurse -Filter *.cs -Path src/Dx.Cli |
    Select-String -Pattern ($semanticPatterns -join '|') -AllMatches |
    Where-Object { $_.Line -notmatch '^\s*//' -and $_.Line -notmatch '^\s*\*' } |
    Select-Object Path,LineNumber,Line -Unique

if ($semanticRefs) {
    Fail "CLI contains semantic branching"
    $semanticRefs | ForEach-Object { Write-Host "  $($_.Path):$($_.LineNumber) $($_.Line.Trim())" }
} else {
    Write-Host "✓ No CLI semantic branching" -ForegroundColor Green
}

# 4. Dispatcher must write intent + result logs (dual-log invariant)
Write-Host "`n[4/4] Checking Dispatcher dual-log invariant..." -ForegroundColor Yellow
$dispatcher = Get-Content "src/Dx.Core/Protocol/DxDispatcher.cs" -Raw

$hasIntentLog = $dispatcher -match 'WriteSessionLogAsync.*SessionLogEntry'  # line 106
$hasResultLogMethod = $dispatcher -match 'WriteResultLogAsync'              # line 155, 305
$hasFinally = $dispatcher -match 'finally\s*\{'

if ($hasIntentLog -and $hasResultLogMethod -and $hasFinally) {
    Write-Host "✓ Dual-log invariant present (intent + result + finally)" -ForegroundColor Green
} else {
    Fail "Dispatcher missing dual-log pattern (intent=$hasIntentLog, result=$hasResultLogMethod, finally=$hasFinally)"
}

Write-Host "`n=== Summary ===" -ForegroundColor Cyan
if ($failed) {
    Write-Host "Convergence Gate FAILED" -ForegroundColor Red
    exit 1
} else {
    Write-Host "Convergence Gate PASSED" -ForegroundColor Green
    exit 0
}

