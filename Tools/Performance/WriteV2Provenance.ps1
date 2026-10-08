[CmdletBinding()]
param(
    [string]$RepoRoot = '',
    [string]$OutputDirectory = 'Logs/V2-Implementation/Provenance',
    [ValidateSet('SHA256')]
    [string]$Algorithm = 'SHA256'
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
}
else {
    $RepoRoot = (Resolve-Path $RepoRoot).Path
}

$records = New-Object 'System.Collections.Generic.List[object]'
$missing = New-Object 'System.Collections.Generic.List[string]'

function Get-RepoRelativePath {
    param([string]$FullPath)
    $rootPrefix = $script:RepoRoot.TrimEnd('\') + '\'
    if ($FullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $FullPath.Substring($rootPrefix.Length).Replace('\', '/')
    }
    return $FullPath.Replace('\', '/')
}

function Add-ManifestFile {
    param(
        [string]$Path,
        [string]$Category,
        [string]$Role,
        [string]$Note,
        [bool]$FrozenBinary = $false
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $relativePath = Get-RepoRelativePath $fullPath
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        [void]$script:missing.Add($relativePath)
        [void]$script:records.Add([pscustomobject]@{
            Path = $relativePath
            Category = $Category
            Role = $Role
            Exists = $false
            Bytes = 0
            SHA256 = 'MISSING'
            FileLastWriteUtc = $null
            FrozenBinary = $FrozenBinary
            Note = $Note
        })
        return
    }

    $item = Get-Item -LiteralPath $fullPath
    $hash = Get-FileHash -LiteralPath $fullPath -Algorithm $Algorithm
    [void]$script:records.Add([pscustomobject]@{
        Path = $relativePath
        Category = $Category
        Role = $Role
        Exists = $true
        Bytes = [int64]$item.Length
        SHA256 = $hash.Hash
        FileLastWriteUtc = $item.LastWriteTimeUtc.ToString('o')
        FrozenBinary = $FrozenBinary
        Note = $Note
    })
}

function Add-DirectoryFiles {
    param(
        [string]$Root,
        [string]$Filter,
        [string]$Category,
        [string]$Role,
        [string]$Note,
        [bool]$FrozenBinary = $false
    )

    $fullRoot = Join-Path $script:RepoRoot $Root
    if (-not (Test-Path -LiteralPath $fullRoot -PathType Container)) {
        [void]$script:missing.Add($Root.Replace('\', '/'))
        return
    }
    Get-ChildItem -LiteralPath $fullRoot -Recurse -File -Filter $Filter |
        Sort-Object FullName |
        ForEach-Object {
            Add-ManifestFile $_.FullName $Category $Role $Note $FrozenBinary
        }
}

# Binary provenance is explicit so a missing build is recorded instead of silently
# dropping a requested artifact. V2AllocationFinalCPU is a frozen CPU binary; its
# corresponding current source/harness may have changed after that build.
$builds = @(
    @{ Root = 'Builds/V2TimingFrozen'; Label = 'V2TimingFrozen'; Frozen = $true; Note = 'CPU timing-frozen build artifact; source correspondence is not inferred.' },
    @{ Root = 'Builds/V2AllocationFinalCPU'; Label = 'V2AllocationFinalCPU'; Frozen = $true; Note = 'CPU-frozen benchmark binary; current source/harness may have changed after this binary.' },
    @{ Root = 'Builds/V2Validation'; Label = 'V2Validation'; Frozen = $false; Note = 'validation build artifact; source correspondence is not inferred.' },
    @{ Root = 'Builds/V2Playable'; Label = 'V2Playable'; Frozen = $false; Note = 'playable artifact; this manifest does not claim final display correspondence.' }
)

foreach ($build in $builds) {
    $buildRoot = Join-Path $RepoRoot $build.Root
    Add-DirectoryFiles $build.Root '*.exe' 'executable' $build.Label $build.Note $build.Frozen

    foreach ($runtimeRoot in @(Get-ChildItem -LiteralPath $buildRoot -Directory -Filter '*_Data')) {
        Get-ChildItem -LiteralPath (Join-Path $runtimeRoot.FullName 'Managed') -File -Filter 'OpenOita*.dll' |
            Sort-Object FullName |
            ForEach-Object {
                Add-ManifestFile $_.FullName 'runtime-dll' $build.Label $build.Note $build.Frozen
            }
        $nativeRoot = Join-Path $runtimeRoot.FullName 'Plugins\x86_64'
        if (Test-Path -LiteralPath $nativeRoot -PathType Container) {
            Get-ChildItem -LiteralPath $nativeRoot -File -Filter '*.dll' |
                Sort-Object FullName |
                ForEach-Object {
                    Add-ManifestFile $_.FullName 'native-dll' $build.Label $build.Note $build.Frozen
                }
        }
    }
}

Add-DirectoryFiles 'Assets/Scripts/OpenOitaWorld/Render/V2' '*.cs' 'v2-render-source' 'current-source' 'current renderer source.'
Add-DirectoryFiles 'Assets/Shaders/V2' '*' 'v2-shader-source' 'current-source' 'current shader source.'
Add-DirectoryFiles 'Tools/Performance' '*' 'validation-tool-source' 'current-source' 'current benchmark/provenance tool source.'
foreach ($rawRoot in @('AcceptanceCpu','AcceptanceAllocationCpuFinal','AcceptanceDisplayVerified','AcceptanceAllocationDisplayVerified')) {
    Add-DirectoryFiles ('Logs/V2-Implementation/'+$rawRoot) '*' 'raw-acceptance' $rawRoot 'completed acceptance evidence; no rerun.'
}

# Current V2 source and schemas are hashed at execution time. The manifest records
# filesystem write times as metadata only; it does not invent a source capture date.
Add-DirectoryFiles 'Assets/Scripts/OpenOitaWorld/V2' '*.cs' 'v2-source' 'current-source' `
    'current working-tree source at manifest generation; no fabricated binary capture date.'
Add-DirectoryFiles 'Assets/Scripts/OpenOitaWorld/V2' '*.asmdef' 'v2-source' 'current-source' `
    'current working-tree assembly definition at manifest generation.'
Add-DirectoryFiles 'docs/schemas' '*' 'schema' 'v2-schema' `
    'schema file hashed as present at manifest generation.'
Add-DirectoryFiles 'docs/specs' 'V2-*.md' 'specification' 'v2-spec' `
    'V2 specification hashed as present at manifest generation.'
Add-DirectoryFiles 'Assets/Tests/OpenOita/EditMode/V2' '*.cs' 'v2-test-source' 'editmode-v2-tests' `
    'current V2 EditMode test source; no fabricated result date.'
Add-DirectoryFiles 'Assets/Tests/OpenOita/PlayMode/V2' '*.cs' 'v2-test-source' 'playmode-v2-tests' `
    'current V2 PlayMode test source; no fabricated result date.'

# Only test XML result files are included; no heap, process, or memory sampling is
# performed by this script. Compile intermediates are intentionally excluded.
foreach ($xmlRoot in @('Logs/V2-Implementation', 'Logs/Optimization-20261006')) {
    $fullRoot = Join-Path $RepoRoot $xmlRoot
    if (Test-Path -LiteralPath $fullRoot -PathType Container) {
        Get-ChildItem -LiteralPath $fullRoot -File -Filter '*.xml' |
            Sort-Object FullName |
            ForEach-Object {
                Add-ManifestFile $_.FullName 'test-xml' 'test-result' `
                    'existing test result XML; this script records it without re-running tests.'
            }
    }
}

$outputRoot = Join-Path $RepoRoot $OutputDirectory
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$manifest = [ordered]@{
    schema = 'OpenOita.V2.Provenance.v1'
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    algorithm = $Algorithm
    repoRoot = $RepoRoot
    heapSampling = $false
    notes = @(
        'SHA256 and filesystem metadata only; no heap or total-memory sampling.',
        'V2AllocationFinalCPU is marked as a frozen binary.',
        'Current V2 source/harness hashes are captured when this script is executed; no source capture date is fabricated.'
    )
    missingCount = $missing.Count
    missing = $missing.ToArray()
    files = $records.ToArray()
}

$jsonPath = Join-Path $outputRoot 'v2-provenance.json'
$csvPath = Join-Path $outputRoot 'v2-provenance.csv'
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
$records | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding UTF8
Write-Host "Wrote $jsonPath"
Write-Host "Wrote $csvPath"
if ($missing.Count -gt 0) {
    Write-Warning ("Manifest contains {0} missing requested paths." -f $missing.Count)
}
