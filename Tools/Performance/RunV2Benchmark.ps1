param(
    [string]$Player = 'Builds/V2Validation/OpenOitaV2Benchmark.exe',
    [string[]]$Scenario = @('current40k','water1m-concentrated','water100k-dispersed',
        'steam1m-concentrated','steam100k-dispersed','burning1m-concentrated',
        'burning100k-dispersed','mixed1m-concentrated','mixed100k-dispersed',
        'bodies64-8192-rotating','bodies64-8192-sleeping',
        'mixed1m-bodies64-rotating','mixed1m-bodies64-sleeping','fracture8193-concentrated'),
    [string]$OutputDirectory = 'Logs/V2-Performance',
    [int]$Warmup = 1000,
    [int]$Samples = 10000,
    [switch]$Display,
    [switch]$AllocationValidation
)
$ErrorActionPreference = 'Stop'
if ($Warmup -le 0 -or $Samples -le 0) { throw 'Warmup和Samples必须大于0。' }
$taskPlayer = (Resolve-Path -LiteralPath $Player).Path
$taskOutput = [IO.Path]::GetFullPath((Join-Path (Get-Location) $OutputDirectory))
[IO.Directory]::CreateDirectory($taskOutput) | Out-Null
if ($Display -and !$PSBoundParameters.ContainsKey('Scenario')) { $Scenario = @('current40k') }
foreach ($taskScenario in $Scenario) {
    if ($taskScenario -notmatch '^[a-z0-9-]+$') { throw '场景名含非法字符。' }
    $taskLogPrefix = if ($Display) {'display-'} else {''}
    $taskLog = Join-Path $taskOutput ($taskLogPrefix+$taskScenario+'.log')
    if ($Display) {
        $taskArguments = @('-force-d3d11','-force-gfx-direct','-screen-fullscreen','0','-screen-width','1920',
            '-screen-height','1080','-openoita-v2-display-benchmark',
            '-openoita-v2-display-benchmark-exit',
            ('-openoita-v2-display-benchmark-scenario='+$taskScenario),
            ('-openoita-v2-display-benchmark-warmup='+$Warmup),
            ('-openoita-v2-display-benchmark-samples='+$Samples),
            ('-openoita-v2-display-benchmark-output="'+$taskOutput+'"'),
            '-logFile',('"'+$taskLog+'"'))
    } else {
        $taskArguments = @('-batchmode','-nographics','-openoita-v2-benchmark',
            '-openoita-v2-benchmark-exit',('-openoita-v2-benchmark-scenario='+$taskScenario),
            ('-openoita-v2-benchmark-warmup='+$Warmup),('-openoita-v2-benchmark-samples='+$Samples),
            ('-openoita-v2-benchmark-output="'+$taskOutput+'"'),'-logFile',('"'+$taskLog+'"'))
    }
    if ($AllocationValidation) { $taskArguments += '-openoita-v2-allocation-validation' }
    $taskProcess = Start-Process -FilePath $taskPlayer -ArgumentList $taskArguments -WindowStyle Hidden -PassThru
    Write-Output ('开始 '+$taskScenario+'，PID '+$taskProcess.Id)
    while (!$taskProcess.WaitForExit(30000)) { Write-Output ('仍在测量 '+$taskScenario) }
    if ($taskProcess.ExitCode -ne 0) { throw ($taskScenario+'进程失败，退出码 '+$taskProcess.ExitCode) }
    if (Select-String -LiteralPath $taskLog -Pattern 'Tick failed|初始化失败|fixture被拒绝|缺少完整|缺少 Resources' -Quiet) {
        throw ($taskScenario+'未完成，详情见 '+$taskLog)
    }
    Write-Output ('完成 '+$taskScenario+'；应检查CSV行数、activity_valid和metadata，不以进程退出码宣布达标。')
}
