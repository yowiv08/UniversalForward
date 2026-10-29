[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BaselineBin,
    [Parameter(Mandatory = $true)]
    [string]$FixedBin,
    [string]$ContractsBin,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../../artifacts/cpu-benchmarks'),
    [ValidateRange(0, 10000)]
    [int]$ClaudeMax = 10000
)

$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'UniversalForward.CpuBenchmarks.csproj'

function Resolve-BenchmarkBin {
    param([string]$Path, [string]$Assembly)
    $directory = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).ProviderPath
    if (-not (Test-Path -LiteralPath (Join-Path $directory $Assembly) -PathType Leaf)) {
        throw "$Path must contain $Assembly"
    }
    return $directory
}

$resolvedBaseline = Resolve-BenchmarkBin -Path $BaselineBin -Assembly 'Plugins.UniversalForward.dll'
$resolvedFixed = Resolve-BenchmarkBin -Path $FixedBin -Assembly 'Plugins.UniversalForward.dll'
if ([string]::IsNullOrWhiteSpace($ContractsBin)) { $ContractsBin = $BaselineBin }
$resolvedContracts = Resolve-BenchmarkBin -Path $ContractsBin -Assembly 'Router.Contracts.dll'
$outputRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

foreach ($variant in @('baseline', 'fixed')) {
    $pluginBin = if ($variant -eq 'baseline') { $resolvedBaseline } else { $resolvedFixed }
    $variantOutput = Join-Path $outputRoot "bin/$variant"
    $intermediatePath = (Join-Path $outputRoot "obj/$variant") + [IO.Path]::DirectorySeparatorChar
    # OutputPath is the final directory consumed below, without an extra framework/RID suffix.
    & dotnet build $projectPath -c Release "-p:PluginBin=$pluginBin" "-p:ContractsBin=$resolvedContracts" "-p:BaseIntermediateOutputPath=$intermediatePath" "-p:OutputPath=$variantOutput" -p:AppendTargetFrameworkToOutputPath=false -p:AppendRuntimeIdentifierToOutputPath=false -v minimal
    if ($LASTEXITCODE -ne 0) { throw "$variant benchmark build failed" }
    $jsonPath = Join-Path $outputRoot "$variant-results.json"
    $runDll = Join-Path $variantOutput 'UniversalForward.Tests.dll'
    & dotnet $runDll --variant $variant --output $jsonPath --claude-max $ClaudeMax
    if ($LASTEXITCODE -ne 0) { throw "$variant benchmark run failed" }
}

$baseline = Get-Content -Raw -LiteralPath (Join-Path $outputRoot 'baseline-results.json') | ConvertFrom-Json
$fixed = Get-Content -Raw -LiteralPath (Join-Path $outputRoot 'fixed-results.json') | ConvertFrom-Json
$comparisons = foreach ($before in $baseline.cases) {
    $after = @($fixed.cases | Where-Object { $_.id -eq $before.id })
    if ($after.Count -ne 1) { throw "Expected one fixed case for $($before.id)" }
    $after = $after[0]
    $hashEqual = $before.expectedSha256 -eq $after.expectedSha256 -and $before.allOutputsMatchExpected -and $after.allOutputsMatchExpected
    if (-not $hashEqual) { throw "Output hash mismatch across variants: $($before.id)" }
    [ordered]@{
        id = $before.id
        inputBytes = $before.inputBytes
        chunkBytes = $before.chunkBytes
        baselineMedianMs = $before.medianElapsedMs
        fixedMedianMs = $after.medianElapsedMs
        elapsedSpeedup = $before.medianElapsedMs / $after.medianElapsedMs
        baselineMedianAllocatedBytes = $before.medianAllocatedBytes
        fixedMedianAllocatedBytes = $after.medianAllocatedBytes
        allocationReductionFactor = $before.medianAllocatedBytes / $after.medianAllocatedBytes
        outputSha256 = $before.expectedSha256
        outputsMatch = $hashEqual
        baselineLog = $before.log
        fixedLog = $after.log
    }
}
$report = [ordered]@{
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    methodology = 'Already-built plugin DLLs in separate .NET 10 processes; purely in-memory SSE sources; input and expected hashes generated outside timing; one warmup and three measured samples per case; forced GC outside each timed region; GC.GetTotalAllocatedBytes(precise:true) measures managed allocation; journal includes real CapturedContent read, metadata completion, and queue flush; original incoming body is null for both log variants; no network requests.'
    limitations = 'Microbenchmark wall time is affected by workstation activity and JIT; process CPU time has coarse timer resolution; allocation values describe allocation traffic, not retained memory. Fixed journal intentionally stores no upstream request/response body; equal downstream hashes and metadata are checked.'
    baseline = $baseline
    fixed = $fixed
    comparisons = @($comparisons)
}
$resultJson = Join-Path $outputRoot 'benchmark-results.json'
$report | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $resultJson -Encoding utf8

$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# UniversalForward CPU microbenchmarks')
$lines.Add('')
$lines.Add($report.methodology)
$lines.Add('')
$lines.Add("Runtime: $($baseline.runtimeVersion); logical processors: $($baseline.processorCount); GC: $($baseline.gcSettings).")
$lines.Add("Plugin assembly versions: baseline $($baseline.pluginAssemblyVersion); fixed $($fixed.pluginAssemblyVersion).")
$lines.Add('')
$lines.Add("All values below are medians of three measured samples after one warmup. All $(@($comparisons).Count) downstream output hashes match the expected value and match across baseline/fixed.")
$lines.Add('')
$lines.Add('| Case | Baseline ms | Fixed ms | Speedup | Baseline allocated MiB | Fixed allocated MiB | Allocation reduction |')
$lines.Add('| --- | ---: | ---: | ---: | ---: | ---: | ---: |')
foreach ($entry in $comparisons) {
    $template = '| {0} | {1:F3} | {2:F3} | {3:F2}x | {4:F3} | {5:F3} | {6:F2}x |'
    $values = @($entry.id, $entry.baselineMedianMs, $entry.fixedMedianMs, $entry.elapsedSpeedup, ($entry.baselineMedianAllocatedBytes / 1MB), ($entry.fixedMedianAllocatedBytes / 1MB), $entry.allocationReductionFactor)
    $lines.Add([string]::Format([Globalization.CultureInfo]::InvariantCulture, $template, $values))
}
$log = $comparisons | Where-Object { $_.id -eq 'journal-sse-512KiB' }
$lines.Add('')
$lines.Add("Journal saved upstream response body: baseline $($log.baselineLog.savedResponseBodyBytes) bytes, fixed $($log.fixedLog.savedResponseBodyBytes) bytes. Saved request body: baseline $($log.baselineLog.savedRequestBodyBytes) bytes, fixed $($log.fixedLog.savedRequestBodyBytes) bytes. Both retain correct HTTP status 200, reported model upstream-bench, reasoning effort high, first-byte metadata, and trace.")
$lines.Add('')
$lines.Add($report.limitations)
$lines.Add('')
$lines.Add('Reproduction source: benchmarks/cpu/run-benchmarks.ps1. Supply -BaselineBin and -FixedBin pointing to already-built plugin DLL directories; -ContractsBin defaults to -BaselineBin. See benchmarks/cpu/README.md for checkout/build commands. -ClaudeMax 5000 omits the largest Claude case.')
$lines | Set-Content -LiteralPath (Join-Path $outputRoot 'benchmark-results.md') -Encoding utf8
Write-Output $resultJson
