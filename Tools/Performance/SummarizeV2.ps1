param(
    [Parameter(Mandatory=$true)][string]$Directory,
    [int]$MinimumSamples = 10000,
    [string]$OutputPath
)
$taskCulture = [Globalization.CultureInfo]::InvariantCulture
function Get-Percentile($taskValues, [double]$taskFraction) {
    $taskSorted = @($taskValues | Sort-Object)
    if ($taskSorted.Count -eq 0) { return $null }
    return $taskSorted[[math]::Max(0,[int][math]::Ceiling($taskSorted.Count * $taskFraction)-1)]
}
$taskSummaries = @(Get-ChildItem -LiteralPath $Directory -Filter '*.csv' -File | ForEach-Object {
    $taskFile = $_
    $taskRows = @(Import-Csv -LiteralPath $taskFile.FullName)
    if ($taskRows.Count -eq 0) { return }
    $taskDisplay = $taskRows[0].PSObject.Properties.Name -contains 'display_prepare_ms'
    if (!$taskDisplay -and !($taskRows[0].PSObject.Properties.Name -contains 'wall_tick_ms')) { return }
    $taskField = if ($taskDisplay) {'display_prepare_ms'} else {'wall_tick_ms'}
    $taskValues = @($taskRows | Where-Object { -not [string]::IsNullOrWhiteSpace($_.$taskField) -and $_.$taskField -ne 'NA' } | ForEach-Object { [double]::Parse($_.$taskField,$taskCulture) })
    $taskAllocField = if ($taskDisplay) {'display_prepare_allocated_bytes'} else {'allocated_bytes_thread'}
    $taskAlloc = @($taskRows | Where-Object { $_.$taskAllocField -ne 'NA' } | ForEach-Object { [long]$_.$taskAllocField })
    $taskInvalid = if ($taskDisplay) { @($taskRows | Where-Object render_frame -ne '1').Count } else { @($taskRows | Where-Object activity_valid -ne '1').Count }
    $taskResult = [ordered]@{
        file = $taskFile.Name; scenario = $taskRows[0].scenario; samples = $taskRows.Count
        validSamples = $taskValues.Count; invalidActivityOrRender = $taskInvalid
        complete = $taskRows.Count -ge $MinimumSamples -and $taskValues.Count -eq $taskRows.Count; metric = $taskField
        meanMs = ($taskValues | Measure-Object -Average).Average
        p95Ms = Get-Percentile $taskValues .95; p99Ms = Get-Percentile $taskValues .99
        maxMs = ($taskValues | Measure-Object -Maximum).Maximum
        allocatedBytesTotal = ($taskAlloc | Measure-Object -Sum).Sum
        allocatedBytesMax = ($taskAlloc | Measure-Object -Maximum).Maximum
        allocationSamples = $taskAlloc.Count
        allocationSupported = $taskAlloc.Count -eq $taskRows.Count
    }
    if ($taskDisplay) {
        $taskGpu = @($taskRows | Where-Object { -not [string]::IsNullOrWhiteSpace($_.gpu_frame_ms) -and $_.gpu_frame_ms -ne 'NA' } | ForEach-Object { [double]::Parse($_.gpu_frame_ms,$taskCulture) })
        $taskResult.gpuSamples = $taskGpu.Count
        $taskResult.gpuP95Ms = Get-Percentile $taskGpu .95
        $taskResult.gpuP99Ms = Get-Percentile $taskGpu .99
        $taskMetadataPath = $taskFile.FullName + '.metadata.json'
        if (Test-Path -LiteralPath $taskMetadataPath) {
            $taskMetadata = Get-Content -LiteralPath $taskMetadataPath -Raw | ConvertFrom-Json
            $taskResult.renderRequestSubmitScopeRelation = $taskMetadata.renderRequestSubmitScopeRelation
            $taskResult.renderRequestSubmitIncludesDisplayPreparation = $taskMetadata.renderRequestSubmitIncludesDisplayPreparation
            if ($taskMetadata.gpuTimingMode -eq 'global_continuous_render_window') {
                $taskResult.gpuMetricMode = $taskMetadata.gpuTimingMode
                $taskResult.gpuSamples = $taskMetadata.gpuSamples
                $taskResult.gpuP95Ms = if ($taskMetadata.gpuP95Ms -ne 'NA') { [double]::Parse($taskMetadata.gpuP95Ms,$taskCulture) } else { $null }
                $taskResult.gpuP99Ms = if ($taskMetadata.gpuP99Ms -ne 'NA') { [double]::Parse($taskMetadata.gpuP99Ms,$taskCulture) } else { $null }
            }
            if ($taskMetadata.nativeGpuSamples -gt 0) {
                $taskResult.gpuMetricMode = 'native_d3d11_camera_scope'
                $taskResult.gpuSamples = $taskMetadata.nativeGpuSamples
                $taskResult.gpuP95Ms = [double]::Parse($taskMetadata.nativeGpuP95Ms,$taskCulture)
                $taskResult.gpuP99Ms = [double]::Parse($taskMetadata.nativeGpuP99Ms,$taskCulture)
                $taskResult.gpuDroppedScopes = $taskMetadata.nativeGpuDropCount
            }
        }
    } else {
        $taskResult.meanRuleAttempts = ($taskRows | Measure-Object rule_attempts -Average).Average
        $taskResult.meanMoves = ($taskRows | Measure-Object moves -Average).Average
        $taskResult.meanBurningCells = ($taskRows | Measure-Object burning_cells -Average).Average
    }
    [pscustomobject]$taskResult
})
if ($OutputPath) { $taskSummaries | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutputPath -Encoding utf8 }
$taskSummaries
