param(
    [string]$TimingDirectory = 'Logs/V2-Implementation/AcceptanceCpu',
    [string]$AllocationDirectory = 'Logs/V2-Implementation/AcceptanceAllocationCpuFinal',
    [string]$DisplayDirectory = 'Logs/V2-Implementation/AcceptanceDisplayVerified',
    [string]$AllocationDisplayDirectory = 'Logs/V2-Implementation/AcceptanceAllocationDisplayVerified',
    [string]$OutputJson = 'Logs/V2-Implementation/AcceptanceSummary.json',
    [string]$OutputTable = 'Logs/V2-Implementation/AcceptanceSummary.md',
    [string]$SummarizeScript = 'Tools/Performance/SummarizeV2.ps1',
    [int]$Warmup = 1000,
    [int]$Samples = 10000,
    [switch]$VerifyGpu
)

$ErrorActionPreference = 'Stop'
$invariant = [Globalization.CultureInfo]::InvariantCulture
$numberStyles = [Globalization.NumberStyles]::Float
$ExpectedTimingScenarios = [string[]]@(
    'current40k',
    'water1m-concentrated', 'water100k-dispersed',
    'steam1m-concentrated', 'steam100k-dispersed',
    'burning1m-concentrated', 'burning100k-dispersed',
    'mixed1m-concentrated', 'mixed100k-dispersed',
    'mixed1m-bodies64-rotating', 'mixed1m-bodies64-sleeping',
    'bodies64-8192-rotating', 'bodies64-8192-sleeping',
    'fracture8193-concentrated'
)
$ExpectedAllocationScenarios = [string[]]@(
    'current40k',
    'water1m-concentrated', 'water100k-dispersed',
    'steam1m-concentrated', 'steam100k-dispersed',
    'burning1m-concentrated', 'burning100k-dispersed',
    'mixed1m-concentrated', 'mixed100k-dispersed',
    'mixed1m-bodies64-rotating', 'mixed1m-bodies64-sleeping',
    'bodies64-8192-rotating', 'bodies64-8192-sleeping'
)
$ExpectedDisplayScenarios = [string[]]@(
    'current40k',
    'mixed1m-concentrated',
    'mixed1m-bodies64-rotating'
)

function Resolve-RepoPath([string]$path) {
    if ([IO.Path]::IsPathRooted($path)) { return [IO.Path]::GetFullPath($path) }
    return [IO.Path]::GetFullPath((Join-Path (Get-Location) $path))
}

function Get-Field($object, [string]$name) {
    if ($null -eq $object) { return $null }
    if ($object -is [System.Collections.IDictionary] -and $object.Contains($name)) { return $object[$name] }
    $property = $object.PSObject.Properties[$name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Convert-InvariantDouble($value) {
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) { return $null }
    try { return [double]::Parse([string]$value, $numberStyles, $invariant) }
    catch { return $null }
}

function Convert-InvariantUInt64($value) {
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) { return $null }
    try { return [UInt64]::Parse([string]$value, [Globalization.NumberStyles]::Integer, $invariant) }
    catch { return $null }
}

function Get-Percentile([object[]]$values, [double]$percentile) {
    $numbers = @($values | ForEach-Object { Convert-InvariantDouble $_ } | Where-Object { $null -ne $_ })
    if ($numbers.Count -eq 0) { return $null }
    $sorted = @($numbers | Sort-Object)
    $rank = [int][Math]::Ceiling(($percentile / 100.0) * $sorted.Count) - 1
    if ($rank -lt 0) { $rank = 0 }
    if ($rank -ge $sorted.Count) { $rank = $sorted.Count - 1 }
    return [double]$sorted[$rank]
}

function Test-Truthy($value) {
    if ($value -is [bool]) { return [bool]$value }
    return ([string]$value).ToLowerInvariant() -eq 'true' -or [string]$value -eq '1'
}

function Add-Error([System.Collections.Generic.List[string]]$errors, [string]$message) {
    [void]$errors.Add($message)
}

function New-Result([string]$kind, [string]$directory) {
    return [ordered]@{
        kind = $kind
        directory = $directory
        status = 'pending'
        files = @()
        errors = @()
    }
}

function Get-Metadata($csvFile, [System.Collections.Generic.List[string]]$errors) {
    $metadataPath = $csvFile.FullName + '.metadata.json'
    if (!(Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
        Add-Error $errors ('缺少metadata：' + $metadataPath)
        return $null
    }
    try { return (Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json) }
    catch {
        Add-Error $errors ('metadata无法解析：' + $metadataPath + '；' + $_.Exception.Message)
        return $null
    }
}

function Validate-RunMetadata($metadata, [System.Collections.Generic.List[string]]$errors) {
    if ($null -eq $metadata) { return }
    if (!(Test-Truthy (Get-Field $metadata 'complete'))) { Add-Error $errors 'metadata.complete不是true' }
    $warmup = Convert-InvariantDouble (Get-Field $metadata 'warmup')
    $requested = Convert-InvariantDouble (Get-Field $metadata 'requestedSamples')
    $completed = Convert-InvariantDouble (Get-Field $metadata 'completedSamples')
    $invalid = Convert-InvariantDouble (Get-Field $metadata 'invalidSamples')
    if ($null -eq $warmup -or $warmup -ne $Warmup) { Add-Error $errors ('warmup应为' + $Warmup) }
    if ($null -eq $requested -or $requested -ne $Samples) { Add-Error $errors ('requestedSamples应为' + $Samples) }
    if ($null -eq $completed -or $completed -ne $Samples) { Add-Error $errors ('completedSamples应为' + $Samples) }
    if ($null -eq $invalid -or $invalid -ne 0) { Add-Error $errors 'invalidSamples应为0' }
}

function Get-ScenarioRule([string]$scenario) {
    if ($scenario -eq 'current40k' -or $scenario -eq 'current') {
        return [ordered]@{ category = 'current'; p95 = 4.0; p99 = 8.0; max = $null }
    }
    if ($scenario -match '^fracture') {
        return [ordered]@{ category = 'fracture'; p95 = $null; p99 = $null; max = 50.0 }
    }
    return [ordered]@{ category = 'million-or-body'; p95 = 10.0; p99 = 16.0; max = $null }
}

function Validate-ScenarioRows($rows, $metadata, [System.Collections.Generic.List[string]]$errors) {
    if ($null -eq $metadata) { return }
    $scenario = [string](Get-Field $metadata 'scenario')
    if ([string]::IsNullOrWhiteSpace($scenario) -and $rows.Count -gt 0) { $scenario = [string](Get-Field $rows[0] 'scenario') }
    $rule = Get-ScenarioRule $scenario

    $nonempty = Convert-InvariantDouble (Get-Field $metadata 'nonemptyCells')
    if ($scenario -eq 'current40k' -and ($null -eq $nonempty -or $nonempty -ne 33803)) {
        Add-Error $errors 'current40k metadata.nonemptyCells应为33803'
    }
    if ($scenario -match '1m' -and ($null -eq $nonempty -or $nonempty -ne 1000000)) {
        Add-Error $errors '百万场景 metadata.nonemptyCells应为1000000'
    }

    $bodyScenario = $scenario -match 'bodies64'
    $fracture = $rule.category -eq 'fracture'
    $mixed = $scenario -match '^mixed'
    $bodyErrors = 0
    $mixedErrors = 0
    $activityErrors = 0
    foreach ($row in $rows) {
        $activity = Convert-InvariantDouble (Get-Field $row 'activity_valid')
        if ($null -eq $activity -or $activity -ne 1) { $activityErrors++ }
        if ($bodyScenario -or $scenario -match 'mixed1m-bodies64') {
            $bodyCount = Convert-InvariantDouble (Get-Field $row 'body_count')
            $bodyCells = Convert-InvariantDouble (Get-Field $row 'body_material_cells')
            if ($null -eq $bodyCount -or $bodyCount -ne 64 -or $null -eq $bodyCells -or $bodyCells -ne 8192) { $bodyErrors++ }
            if ($scenario -match 'rotating' -and (Convert-InvariantDouble (Get-Field $row 'rotating_body_count')) -ne 64) { $bodyErrors++ }
            if ($scenario -match 'sleeping' -and (Convert-InvariantDouble (Get-Field $row 'sleeping_body_count')) -ne 64) { $bodyErrors++ }
        }
        if ($fracture) {
            $bodyCount = Convert-InvariantDouble (Get-Field $row 'body_count')
            $bodyCells = Convert-InvariantDouble (Get-Field $row 'body_material_cells')
            if ($null -eq $bodyCount -or $bodyCount -ne 2 -or $null -eq $bodyCells -or $bodyCells -ne 8192) { $bodyErrors++ }
        }
        if ($mixed) {
            $burning = Convert-InvariantDouble (Get-Field $row 'burning_cells')
            $moves = Convert-InvariantDouble (Get-Field $row 'moves')
            if ($null -eq $burning -or $burning -lt 2000 -or $null -eq $moves -or $moves -lt 98000) { $mixedErrors++ }
        }
    }
    if ($activityErrors -gt 0) { Add-Error $errors ('activity_valid不是全1，行数=' + $activityErrors) }
    if ($bodyErrors -gt 0) { Add-Error $errors ('64体/8192格或fracture 2体/8192格数量不符，行错误=' + $bodyErrors) }
    if ($mixedErrors -gt 0) { Add-Error $errors ('mixed要求burning_cells>=2000且moves>=98000，行错误=' + $mixedErrors) }
    return $rule
}

function Invoke-TimingSummary([string]$directory) {
    if (!(Test-Path -LiteralPath $SummarizerPath -PathType Leaf)) {
        throw ('找不到SummarizeV2：' + $SummarizerPath)
    }
    return @(& $SummarizerPath -Directory $directory -MinimumSamples $Samples)
}

function Validate-TimingFile($csvFile, $summaries) {
    $errors = New-Object 'System.Collections.Generic.List[string]'
    $metadata = Get-Metadata $csvFile $errors
    if ($null -eq $metadata) { return [ordered]@{ file = $csvFile.Name; status = 'fail'; errors = @($errors); kind = 'timing' } }
    Validate-RunMetadata $metadata $errors
    $rows = @(Import-Csv -LiteralPath $csvFile.FullName)
    if ($rows.Count -ne $Samples) { Add-Error $errors ('CSV行数应为' + $Samples + '，实际' + $rows.Count) }
    $rule = Validate-ScenarioRows $rows $metadata $errors
    $summary = @($summaries | Where-Object { $_.file -eq $csvFile.Name }) | Select-Object -First 1
    $p95 = $null; $p99 = $null; $max = $null
    if ($null -eq $summary) { Add-Error $errors 'SummarizeV2没有返回该CSV摘要' }
    else {
        $p95 = Convert-InvariantDouble $summary.p95Ms
        $p99 = Convert-InvariantDouble $summary.p99Ms
        $max = Convert-InvariantDouble $summary.maxMs
        if ($null -eq $p95 -or $null -eq $p99 -or $null -eq $max) { Add-Error $errors 'CPU计时摘要缺少p95/p99/max' }
        if ($null -ne $rule -and $rule.p95 -ne $null -and ($null -eq $p95 -or $p95 -gt $rule.p95)) { Add-Error $errors ('p95超过目标' + $rule.p95 + 'ms') }
        if ($null -ne $rule -and $rule.p99 -ne $null -and ($null -eq $p99 -or $p99 -gt $rule.p99)) { Add-Error $errors ('p99超过目标' + $rule.p99 + 'ms') }
        if ($null -ne $rule -and $rule.max -ne $null -and ($null -eq $max -or $max -gt $rule.max)) { Add-Error $errors ('max超过目标' + $rule.max + 'ms') }
    }
    return [ordered]@{
        file = $csvFile.Name; scenario = [string](Get-Field $metadata 'scenario'); status = if ($errors.Count -eq 0) { 'pass' } else { 'fail' }
        samples = $rows.Count; metric = if ($null -eq $summary) { $null } else { [string]$summary.metric }
        targetCategory = if ($null -eq $rule) { $null } else { $rule.category }
        targetP95Ms = if ($null -eq $rule) { $null } else { $rule.p95 }
        targetP99Ms = if ($null -eq $rule) { $null } else { $rule.p99 }
        targetMaxMs = if ($null -eq $rule) { $null } else { $rule.max }
        p95Ms = $p95; p99Ms = $p99; maxMs = $max; errors = @($errors)
        metadataPath = $csvFile.FullName + '.metadata.json'
    }
}

function Validate-TimingDirectory([string]$directory) {
    $result = New-Result 'timing' $directory
    if (!(Test-Path -LiteralPath $directory -PathType Container)) { return $result }
    $summaries = @()
    try { $summaries = Invoke-TimingSummary $directory }
    catch { $result.status = 'fail'; $result.errors = @('SummarizeV2失败：' + $_.Exception.Message); return $result }
    $files = @(Get-ChildItem -LiteralPath $directory -Filter '*.csv' -File | Where-Object { $_.Name -notlike '*.gpu.csv' })
    if ($files.Count -eq 0) { return $result }
    $fileResults = @($files | ForEach-Object { Validate-TimingFile $_ $summaries })
    $result.files = $fileResults
    $result.status = if (@($fileResults | Where-Object status -eq 'fail').Count -gt 0) { 'fail' } elseif (@($fileResults | Where-Object status -eq 'pending').Count -gt 0) { 'pending' } else { 'pass' }
    Add-ExpectedScenarioErrors $result $ExpectedTimingScenarios
    return $result
}

function Validate-AllocationFile($csvFile) {
    $errors = New-Object 'System.Collections.Generic.List[string]'
    $metadata = Get-Metadata $csvFile $errors
    if ($null -eq $metadata) { return [ordered]@{ file = $csvFile.Name; status = 'fail'; errors = @($errors); kind = 'allocation' } }
    Validate-RunMetadata $metadata $errors
    if (!(Test-Truthy (Get-Field $metadata 'allocationSupported')) -or !(Test-Truthy (Get-Field $metadata 'allocationInstrumented'))) { Add-Error $errors 'allocationSupported/allocationInstrumented必须为true' }
    if ([string](Get-Field $metadata 'allocationStatus') -ne 'supported') { Add-Error $errors 'allocationStatus必须为supported' }
    if ([string](Get-Field $metadata 'allocationMetric') -ne 'native_mono_allocation_profiler') { Add-Error $errors 'allocationMetric必须为native_mono_allocation_profiler' }
    if ((Convert-InvariantDouble (Get-Field $metadata 'allocationPositiveControlBytes')) -ne 4128) { Add-Error $errors 'allocationPositiveControlBytes必须为4128' }
    if ((Convert-InvariantDouble (Get-Field $metadata 'allocationEmptyScopeBytes')) -ne 0) { Add-Error $errors 'allocationEmptyScopeBytes必须为0' }
    $rows = @(Import-Csv -LiteralPath $csvFile.FullName)
    if ($rows.Count -ne $Samples) { Add-Error $errors ('CSV行数应为' + $Samples + '，实际' + $rows.Count) }
    $scenario = [string](Get-Field $metadata 'scenario')
    $fracture = $scenario -match '^fracture'
    foreach ($row in $rows) {
        if ([string](Get-Field $row 'allocation_status') -ne 'supported') { Add-Error $errors '存在allocation_status不是supported的行'; break }
        $bytes = Convert-InvariantDouble (Get-Field $row 'allocated_bytes_thread')
        if ($null -eq $bytes) { Add-Error $errors '存在allocated_bytes_thread不是数字的行'; break }
        if (!$fracture -and $bytes -ne 0) { Add-Error $errors '普通稳态allocated_bytes_thread必须全为0'; break }
    }
    Validate-ScenarioRows $rows $metadata $errors | Out-Null
    return [ordered]@{
        file = $csvFile.Name; scenario = $scenario; status = if ($errors.Count -eq 0) { 'pass' } else { 'fail' }
        samples = $rows.Count; allocationSupported = Test-Truthy (Get-Field $metadata 'allocationSupported')
        positiveControlBytes = Convert-InvariantDouble (Get-Field $metadata 'allocationPositiveControlBytes')
        emptyScopeBytes = Convert-InvariantDouble (Get-Field $metadata 'allocationEmptyScopeBytes'); errors = @($errors)
    }
}

function Validate-AllocationDirectory([string]$directory) {
    $result = New-Result 'allocation' $directory
    if (!(Test-Path -LiteralPath $directory -PathType Container)) { return $result }
    $files = @(Get-ChildItem -LiteralPath $directory -Filter '*.csv' -File | Where-Object { $_.Name -notlike '*.gpu.csv' })
    if ($files.Count -eq 0) { return $result }
    $fileResults = @($files | ForEach-Object { Validate-AllocationFile $_ })
    $result.files = $fileResults
    $result.status = if (@($fileResults | Where-Object status -eq 'fail').Count -gt 0) { 'fail' } elseif (@($fileResults | Where-Object status -eq 'pending').Count -gt 0) { 'pending' } else { 'pass' }
    Add-ExpectedScenarioErrors $result $ExpectedAllocationScenarios
    return $result
}

function Add-ExpectedScenarioErrors($result, [string[]]$expectedScenarios) {
    if ($null -eq $result -or @($result.files).Count -eq 0) { return }
    $seen = @{}
    foreach ($file in @($result.files)) {
        $scenario = [string](Get-Field $file 'scenario')
        if ([string]::IsNullOrWhiteSpace($scenario)) { continue }
        if ($seen.ContainsKey($scenario)) { $seen[$scenario]++ } else { $seen[$scenario] = 1 }
    }
    $setErrors = New-Object 'System.Collections.Generic.List[string]'
    foreach ($scenario in $expectedScenarios) {
        if (!$seen.ContainsKey($scenario)) { [void]$setErrors.Add('缺少expected scenario：' + $scenario) }
    }
    foreach ($scenario in @($seen.Keys)) {
        if (@($expectedScenarios | Where-Object { $_ -eq $scenario }).Count -eq 0) {
            [void]$setErrors.Add('存在未列入expected scenario的文件：' + $scenario)
        }
        elseif ($seen[$scenario] -gt 1) {
            [void]$setErrors.Add('expected scenario重复：' + $scenario)
        }
    }
    if ($setErrors.Count -gt 0) {
        $result.errors = @($result.errors) + @($setErrors)
        $result.status = 'fail'
    }
}

function Validate-GpuFile($csvFile, [bool]$requireAllocation) {
    $errors = New-Object 'System.Collections.Generic.List[string]'
    $metadata = Get-Metadata $csvFile $errors
    if ($null -eq $metadata) { return [ordered]@{ file = $csvFile.Name; status = 'fail'; errors = @($errors); kind = 'display' } }
    $timingChecks = !$requireAllocation
    $scenario = [string](Get-Field $metadata 'scenario')
    $logPath = Join-Path $csvFile.DirectoryName ('display-' + $scenario + '.log')
    $threadingModeDirect = $false
    if (!(Test-Path -LiteralPath $logPath -PathType Leaf)) {
        Add-Error $errors ('缺少display运行日志：' + $logPath)
    }
    else {
        try {
            $logText = Get-Content -LiteralPath $logPath -Raw
            $threadingModeDirect = $logText -match 'kGfxThreadingModeDirect'
            if (!$threadingModeDirect) { Add-Error $errors 'display日志未证明kGfxThreadingModeDirect' }
        }
        catch { Add-Error $errors ('display日志无法读取：' + $logPath + '；' + $_.Exception.Message) }
    }
    $warmupTicks = Convert-InvariantDouble (Get-Field $metadata 'warmupTicks')
    $requestedSamples = Convert-InvariantDouble (Get-Field $metadata 'requestedSamples')
    $completedTicks = Convert-InvariantDouble (Get-Field $metadata 'completedTicks')
    if ($null -eq $warmupTicks -or $warmupTicks -ne $Warmup) { Add-Error $errors ('display warmupTicks应为' + $Warmup) }
    if ($null -eq $requestedSamples -or $requestedSamples -ne $Samples) { Add-Error $errors ('display requestedSamples应为' + $Samples) }
    if ($null -eq $completedTicks -or $completedTicks -ne ($Warmup + $Samples)) { Add-Error $errors ('display completedTicks应为' + ($Warmup + $Samples)) }
    $rows = @(Import-Csv -LiteralPath $csvFile.FullName)
    if ($rows.Count -ne $Samples) { Add-Error $errors ('render CSV行数应为' + $Samples + '，实际' + $rows.Count) }
    $renderRows = @($rows | Where-Object { [string](Get-Field $_ 'render_frame') -eq '1' })
    if ($renderRows.Count -ne $Samples) { Add-Error $errors ('render rows应为' + $Samples + '，实际' + $renderRows.Count) }
    if ($requireAllocation) {
        if (!(Test-Truthy (Get-Field $metadata 'allocationSupported')) -or !(Test-Truthy (Get-Field $metadata 'allocationInstrumented'))) { Add-Error $errors 'display allocationSupported/allocationInstrumented必须为true' }
        if ([string](Get-Field $metadata 'allocationStatus') -ne 'supported') { Add-Error $errors 'display allocationStatus必须为supported' }
        if ([string](Get-Field $metadata 'allocationMetric') -ne 'native_mono_allocation_profiler') { Add-Error $errors 'display allocationMetric必须为native_mono_allocation_profiler' }
        if ((Convert-InvariantDouble (Get-Field $metadata 'allocationPositiveControlBytes')) -ne 4128) { Add-Error $errors 'display allocation正向校验必须为4128' }
        if ((Convert-InvariantDouble (Get-Field $metadata 'allocationEmptyScopeBytes')) -ne 0) { Add-Error $errors 'display allocation空作用域必须为0' }
        foreach ($row in $renderRows) {
            $bytes = Convert-InvariantDouble (Get-Field $row 'display_prepare_allocated_bytes')
            if ($null -eq $bytes -or $bytes -ne 0) { Add-Error $errors '普通显示稳态display_prepare_allocated_bytes必须全为0'; break }
        }
    }
    $nativeSamples = Convert-InvariantDouble (Get-Field $metadata 'nativeGpuSamples')
    $nativeRows = Convert-InvariantDouble (Get-Field $metadata 'nativeGpuRowsWritten')
    $nativeInvalid = Convert-InvariantDouble (Get-Field $metadata 'nativeGpuInvalidSamples')
    $nativeDrops = Convert-InvariantDouble (Get-Field $metadata 'nativeGpuDropCount')
    if (!(Test-Truthy (Get-Field $metadata 'nativeGpuSupported'))) { Add-Error $errors 'nativeGpuSupported必须为true' }
    if (!(Test-Truthy (Get-Field $metadata 'renderAvailable'))) { Add-Error $errors 'renderAvailable必须为true' }
    if (!(Test-Truthy (Get-Field $metadata 'activeNativeGpuTimingFeature'))) { Add-Error $errors 'activeNativeGpuTimingFeature必须为true' }
    if (!(Test-Truthy (Get-Field $metadata 'nativeGpuEndQueryAsyncFlush'))) { Add-Error $errors 'nativeGpuEndQueryAsyncFlush必须为true' }
    $scopeEndPoint = [string](Get-Field $metadata 'nativeGpuScopeEndPoint')
    if ([string]::IsNullOrWhiteSpace($scopeEndPoint) -or $scopeEndPoint -notmatch '(?i)after_camera_submit_graphics_execute_command_buffer') { Add-Error $errors 'nativeGpuScopeEndPoint必须为camera submit command buffer尾点' }
    $cameraTail = Convert-InvariantDouble (Get-Field $metadata 'nativeGpuCameraTailEndCommands')
    if ($null -eq $cameraTail -or $cameraTail -lt $Samples) { Add-Error $errors 'nativeGpuCameraTailEndCommands必须覆盖全部采样范围' }
    if (!(Test-Truthy (Get-Field $metadata 'nativeGpuIncludesFinalBlit'))) { Add-Error $errors 'nativeGpuIncludesFinalBlit必须为true' }
    $scopeCoverage = [string](Get-Field $metadata 'nativeGpuScopeCoverage')
    if ($scopeCoverage -notmatch '(?i)final.?blit') { Add-Error $errors 'nativeGpuScopeCoverage必须声明FinalBlit' }
    if ([string](Get-Field $metadata 'renderMode') -eq 'single_camera_request') {
        if (!(Test-Truthy (Get-Field $metadata 'renderRequestSubmitIncludesDisplayPreparation'))) {
            Add-Error $errors 'renderRequestSubmit必须标记为包含PrepareForCamera；禁止把display_prepare_ms与其相加'
        }
        $scopeRelation = [string](Get-Field $metadata 'renderRequestSubmitScopeRelation')
        if ($scopeRelation -notmatch 'do_not_add_display_prepare_ms') {
            Add-Error $errors 'metadata未声明renderRequestSubmit与display_prepare_ms的嵌套关系'
        }
    }
    if ($nativeSamples -ne $Samples -or $nativeRows -ne $Samples) { Add-Error $errors 'native GPU samples/rows必须严格为10000' }
    if ($nativeInvalid -ne 0) { Add-Error $errors 'nativeGpuInvalidSamples必须为0' }
    if ($nativeDrops -ne 0) { Add-Error $errors 'nativeGpuDropCount必须为0' }
    if ((Convert-InvariantDouble (Get-Field $metadata 'sampleRowsWritten')) -ne $Samples) { Add-Error $errors 'sampleRowsWritten必须为10000' }

    $start = Convert-InvariantUInt64 (Get-Field $metadata 'nativeGpuScopeStartInclusive')
    $end = Convert-InvariantUInt64 (Get-Field $metadata 'nativeGpuScopeEndExclusive')
    if ($null -eq $start -or $null -eq $end -or $end -le $start -or ($end - $start) -ne $Samples) { Add-Error $errors 'native GPU sampleID范围必须是[start,end)且长度10000' }
    $rawPathValue = [string](Get-Field $metadata 'nativeGpuRawScopesPath')
    $rawPath = if (![string]::IsNullOrWhiteSpace($rawPathValue) -and $rawPathValue -ne 'NA' -and (Test-Path -LiteralPath $rawPathValue -PathType Leaf)) { $rawPathValue } else { $csvFile.FullName + '.gpu.csv' }
    if (!(Test-Path -LiteralPath $rawPath -PathType Leaf)) {
        Add-Error $errors ('缺少native GPU raw scope CSV：' + $rawPath)
    }
    else {
        $rawRows = @(Import-Csv -LiteralPath $rawPath)
        $ids = New-Object 'System.Collections.Generic.HashSet[UInt64]'
        $rawGpuValues = New-Object 'System.Collections.Generic.List[double]'
        foreach ($raw in $rawRows) {
            $id = Convert-InvariantUInt64 (Get-Field $raw 'scope_id')
            if ($null -eq $id) { Add-Error $errors 'native raw scope_id无效'; continue }
            [void]$ids.Add($id)
            if ((Convert-InvariantDouble (Get-Field $raw 'flags')) -ne 0) { Add-Error $errors 'native raw存在invalid flags'; break }
            $phase = [string](Get-Field $raw 'phase')
            if ($phase -ne 'sample' -and $phase -ne 'drain') { Add-Error $errors 'native raw phase必须为sample或drain'; break }
            $gpuMs = Convert-InvariantDouble (Get-Field $raw 'gpu_ms')
            if ($null -eq $gpuMs) { Add-Error $errors 'native raw gpu_ms无效'; break }
            [void]$rawGpuValues.Add([double]$gpuMs)
        }
        if ($rawRows.Count -ne $Samples -or $ids.Count -ne $Samples) { Add-Error $errors 'native raw scope ID必须严格10000行且唯一' }
        if ($null -ne $start -and $null -ne $end) {
            foreach ($id in $ids) { if ($id -lt $start -or $id -ge $end) { Add-Error $errors 'native raw scope_id超出metadata[start,end)'; break } }
            if ($ids.Count -eq $Samples) {
                for ([UInt64]$expected = $start; $expected -lt $end; $expected++) {
                    if (!$ids.Contains($expected)) { Add-Error $errors 'native raw scope ID不是完整连续[start,end)'; break }
                }
            }
        }
    }
    $rawGpuP95 = if ($null -eq $rawGpuValues) { $null } else { Get-Percentile @($rawGpuValues) 95 }
    $displayPrepareValues = @($renderRows | ForEach-Object { Convert-InvariantDouble (Get-Field $_ 'display_prepare_ms') })
    $renderSubmitValues = @($renderRows | ForEach-Object { Convert-InvariantDouble (Get-Field $_ 'render_request_submit_ms') })
    $displayPrepareP95 = Get-Percentile $displayPrepareValues 95
    $renderSubmitP95 = Get-Percentile $renderSubmitValues 95
    if ($timingChecks) {
        if ($null -eq $rawGpuP95 -or $rawGpuP95 -gt 4.0) { Add-Error $errors 'native GPU p95必须≤4ms' }
        if ($null -eq $displayPrepareP95 -or $displayPrepareP95 -gt 4.0) { Add-Error $errors 'display_prepare_ms p95必须≤4ms' }
        if ($null -eq $renderSubmitP95 -or $renderSubmitP95 -gt 4.0) { Add-Error $errors 'render_request_submit_ms p95必须≤4ms' }
    }
    return [ordered]@{
        file = $csvFile.Name; scenario = $scenario; logPath = $logPath
        threadingModeDirect = $threadingModeDirect
        status = if ($errors.Count -eq 0) { 'pass' } else { 'fail' }
        samples = $rows.Count; nativeGpuSamples = $nativeSamples; nativeGpuRowsWritten = $nativeRows
        nativeGpuScopeStartInclusive = $start; nativeGpuScopeEndExclusive = $end
        nativeGpuScopeEndPoint = $scopeEndPoint; nativeGpuCameraTailEndCommands = $cameraTail
        nativeGpuIncludesFinalBlit = Test-Truthy (Get-Field $metadata 'nativeGpuIncludesFinalBlit')
        nativeGpuEndQueryAsyncFlush = Test-Truthy (Get-Field $metadata 'nativeGpuEndQueryAsyncFlush')
        timingChecksApplied = $timingChecks; nativeGpuP95Ms = $rawGpuP95
        displayPrepareP95Ms = $displayPrepareP95; renderRequestSubmitP95Ms = $renderSubmitP95
        errors = @($errors)
    }
}

function Validate-GpuDirectory([string]$directory, [bool]$requireAllocation) {
    $result = New-Result $(if ($requireAllocation) { 'allocation-display' } else { 'display' }) $directory
    if (!(Test-Path -LiteralPath $directory -PathType Container)) { return $result }
    $files = @(Get-ChildItem -LiteralPath $directory -Filter '*.csv' -File | Where-Object { $_.Name -notlike '*.gpu.csv' })
    if ($files.Count -eq 0) { return $result }
    $fileResults = @($files | ForEach-Object { Validate-GpuFile $_ $requireAllocation })
    $result.files = $fileResults
    $result.status = if (@($fileResults | Where-Object status -eq 'fail').Count -gt 0) { 'fail' } elseif (@($fileResults | Where-Object status -eq 'pending').Count -gt 0) { 'pending' } else { 'pass' }
    Add-ExpectedScenarioErrors $result $ExpectedDisplayScenarios
    return $result
}

function Ensure-ParentDirectory([string]$path) {
    $parent = Split-Path -Parent $path
    if (![string]::IsNullOrWhiteSpace($parent)) { [IO.Directory]::CreateDirectory($parent) | Out-Null }
}

function Add-TableResult([System.Collections.Generic.List[string]]$lines, $result) {
    foreach ($file in @($result.files)) {
        $errorText = if (@($file.errors).Count -eq 0) { '' } else { (@($file.errors) -join '；') }
        $p95 = if ($null -eq $file.p95Ms) { '' } else { ([double]$file.p95Ms).ToString('0.####', $invariant) }
        $p99 = if ($null -eq $file.p99Ms) { '' } else { ([double]$file.p99Ms).ToString('0.####', $invariant) }
        $max = if ($null -eq $file.maxMs) { '' } else { ([double]$file.maxMs).ToString('0.####', $invariant) }
        $target = if ($null -ne $file.targetP95Ms) { 'p95≤' + $file.targetP95Ms + '/p99≤' + $file.targetP99Ms } elseif ($null -ne $file.targetMaxMs) { 'max≤' + $file.targetMaxMs } else { '' }
        [void]$lines.Add('| ' + $result.kind + ' | ' + $file.file + ' | ' + $file.status + ' | ' + $file.samples + ' | ' + $target + ' | ' + $p95 + ' | ' + $p99 + ' | ' + $max + ' | ' + $errorText.Replace('|','/').Replace("`r",' ').Replace("`n",' ') + ' |')
    }
    if (@($result.files).Count -eq 0) {
        $pendingReason = if (@($result.errors).Count -gt 0) { (@($result.errors) -join '；') } else { $result.directory.Replace('|','/') }
        [void]$lines.Add('| ' + $result.kind + ' | （目录或CSV尚未出现） | pending | 0 |  |  |  |  | ' + $pendingReason.Replace('|','/') + ' |')
    }
}

if ($Warmup -le 0 -or $Samples -le 0) { throw 'Warmup和Samples必须大于0。' }
$TimingPath = Resolve-RepoPath $TimingDirectory
$AllocationPath = Resolve-RepoPath $AllocationDirectory
$DisplayPath = Resolve-RepoPath $DisplayDirectory
$AllocationDisplayPath = Resolve-RepoPath $AllocationDisplayDirectory
$SummarizerPath = Resolve-RepoPath $SummarizeScript
$jsonPath = Resolve-RepoPath $OutputJson
$tablePath = Resolve-RepoPath $OutputTable

$timingResult = Validate-TimingDirectory $TimingPath
$allocationResult = Validate-AllocationDirectory $AllocationPath
if ($VerifyGpu) {
    $displayResult = Validate-GpuDirectory $DisplayPath $false
    $allocationDisplayResult = Validate-GpuDirectory $AllocationDisplayPath $true
}
else {
    $displayResult = New-Result 'display' $DisplayPath
    $displayResult.errors = @('GPU验证待最终采样；如需核验请显式传入 -VerifyGpu')
    $allocationDisplayResult = New-Result 'allocation-display' $AllocationDisplayPath
    $allocationDisplayResult.errors = @('GPU分配验证待最终采样；如需核验请显式传入 -VerifyGpu')
}

$topErrors = New-Object 'System.Collections.Generic.List[string]'
if ($TimingPath -eq $AllocationPath) { Add-Error $topErrors 'Timing与GC目录必须分开' }
if ($DisplayPath -eq $AllocationDisplayPath) { Add-Error $topErrors 'Display与AllocationDisplay目录必须分开' }
$allStatuses = @($timingResult.status, $allocationResult.status, $displayResult.status, $allocationDisplayResult.status)
$overall = if (@($allStatuses | Where-Object { $_ -eq 'fail' }).Count -gt 0 -or $topErrors.Count -gt 0) { 'fail' } elseif (@($allStatuses | Where-Object { $_ -eq 'pending' }).Count -gt 0) { 'pending' } else { 'pass' }

$report = [ordered]@{
    schema = 'OpenOita.V2.AcceptanceVerification.v1'
    generatedUtc = [DateTime]::UtcNow.ToString('O', $invariant)
    status = $overall
    policy = [ordered]@{
        warmup = $Warmup; samples = $Samples
        timingDirectory = $TimingPath; allocationDirectory = $AllocationPath
        displayDirectory = $DisplayPath; allocationDisplayDirectory = $AllocationDisplayPath
        allocationPositiveControlBytes = 4128; allocationEmptyScopeBytes = 0
        allocationTimingExcludedFromPerformance = $true
        gpuVerificationEnabled = [bool]$VerifyGpu
        expectedTimingScenarios = $ExpectedTimingScenarios
        expectedAllocationScenarios = $ExpectedAllocationScenarios
        expectedDisplayScenarios = $ExpectedDisplayScenarios
        gpuPercentileMethod = 'nearest_rank_from_raw_csv; metadata GPU percentiles are informational only'
        missingDirectoriesArePending = $true
    }
    errors = @($topErrors)
    timing = $timingResult
    allocation = $allocationResult
    display = $displayResult
    allocationDisplay = $allocationDisplayResult
}
Ensure-ParentDirectory $jsonPath
($report | ConvertTo-Json -Depth 12) | Set-Content -LiteralPath $jsonPath -Encoding utf8

$tableLines = New-Object 'System.Collections.Generic.List[string]'
[void]$tableLines.Add('# V2 最终验收验证结果')
[void]$tableLines.Add('')
[void]$tableLines.Add('- overall status: `' + $overall + '`')
[void]$tableLines.Add('- warmup/samples: ' + $Warmup + '/' + $Samples)
[void]$tableLines.Add('- `allocated_bytes_thread` 与显示分配耗时不用于 CPU 性能分位数；GC/Allocation 目录独立验证。')
[void]$tableLines.Add('- GPU p95 使用 raw GPU CSV 的 nearest-rank（最近秩）计算；metadata 中的 GPU p95 仅作对照，不参与通过判定。')
[void]$tableLines.Add('')
[void]$tableLines.Add('| 类别 | 文件 | 状态 | 行数 | 目标 | p95 ms | p99 ms | max ms | 诊断 |')
[void]$tableLines.Add('| --- | --- | --- | ---: | --- | ---: | ---: | ---: | --- |')
Add-TableResult $tableLines $timingResult
Add-TableResult $tableLines $allocationResult
Add-TableResult $tableLines $displayResult
Add-TableResult $tableLines $allocationDisplayResult
if ($topErrors.Count -gt 0) { [void]$tableLines.Add(''); [void]$tableLines.Add('顶层错误：' + (@($topErrors) -join '；')) }
Ensure-ParentDirectory $tablePath
$tableLines | Set-Content -LiteralPath $tablePath -Encoding utf8

$report | ConvertTo-Json -Depth 4
