[CmdletBinding()]
param(
    [string]$UnityEditorPath = $env:OPENOITA_UNITY_EDITOR,
    [string]$MsvcRoot = 'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\MSVC\14.44.35207',
    [string]$WindowsSdkVersion = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path -Path $PSScriptRoot -ChildPath '..\..')).Path
$source = Join-Path -Path $repoRoot -ChildPath 'Tools\Performance\NativeAllocationProbe.cpp'
$outputDir = Join-Path -Path $repoRoot -ChildPath 'Assets\Plugins\OpenOita\x86_64'
$output = Join-Path -Path $outputDir -ChildPath 'OpenOitaAllocationProbe.dll'

if ([string]::IsNullOrWhiteSpace($UnityEditorPath)) {
    $candidates = @(
        'C:\Program Files\Unity\Hub\Editor\6000.3.11f1\Editor\Unity.exe',
        'D:\Unity\Editors\6000.3.11f1\Editor\Unity.exe'
    )
    $UnityEditorPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($UnityEditorPath) -or -not (Test-Path -LiteralPath $UnityEditorPath)) {
    throw '找不到 Unity 6000.3.11f1 Unity.exe；可通过 -UnityEditorPath 或 OPENOITA_UNITY_EDITOR 指定。'
}
$unityEditorRoot = Split-Path -Parent $UnityEditorPath
$monoInclude = Join-Path -Path $unityEditorRoot -ChildPath 'Data\il2cpp\external\mono'
if (-not (Test-Path -LiteralPath (Join-Path -Path $monoInclude -ChildPath 'mono\metadata\profiler.h'))) {
    throw "找不到 Unity Mono profiler headers：$monoInclude"
}
if (-not (Test-Path -LiteralPath $source)) { throw "找不到源文件：$source" }

$cl = Join-Path -Path $MsvcRoot -ChildPath 'bin\Hostx64\x64\cl.exe'
if (-not (Test-Path -LiteralPath $cl)) { throw "找不到 MSVC cl.exe：$cl" }

$sdkRoot = 'C:\Program Files (x86)\Windows Kits\10'
$includeRoot = Join-Path -Path $sdkRoot -ChildPath 'Include'
$libRoot = Join-Path -Path $sdkRoot -ChildPath 'Lib'
if ([string]::IsNullOrWhiteSpace($WindowsSdkVersion)) {
    $WindowsSdkVersion = Get-ChildItem -LiteralPath $includeRoot -Directory |
        Where-Object { $_.Name -match '^10\.\d+\.\d+\.\d+$' } |
        Sort-Object Name -Descending |
        Select-Object -First 1 -ExpandProperty Name
}
if ([string]::IsNullOrWhiteSpace($WindowsSdkVersion)) { throw "找不到 Windows SDK：$includeRoot" }
$sdkInclude = Join-Path -Path $includeRoot -ChildPath $WindowsSdkVersion
$sdkLib = Join-Path -Path $libRoot -ChildPath $WindowsSdkVersion
$ucrtInclude = Join-Path -Path $sdkInclude -ChildPath 'ucrt'
$umInclude = Join-Path -Path $sdkInclude -ChildPath 'um'
$sharedInclude = Join-Path -Path $sdkInclude -ChildPath 'shared'
$ucrtLib = Join-Path -Path $sdkLib -ChildPath 'ucrt\x64'
$umLib = Join-Path -Path $sdkLib -ChildPath 'um\x64'
$msvcLib = Join-Path -Path $MsvcRoot -ChildPath 'lib\x64'
foreach ($path in @($sdkInclude, $ucrtInclude, $umInclude, $sharedInclude, $ucrtLib, $umLib, $msvcLib)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "找不到 Windows SDK 路径：$path" }
}

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$nativeBuildDir = Join-Path -Path $repoRoot -ChildPath 'Logs\V2-Implementation\NativeAllocation'
New-Item -ItemType Directory -Force -Path $nativeBuildDir | Out-Null
$buildOutput = Join-Path -Path $nativeBuildDir -ChildPath 'OpenOitaAllocationProbe.dll'
$obj = Join-Path -Path $nativeBuildDir -ChildPath 'NativeAllocationProbe.obj'
$pch = Join-Path -Path $nativeBuildDir -ChildPath 'NativeAllocationProbe.pch'
$importLibrary = Join-Path -Path $nativeBuildDir -ChildPath 'OpenOitaAllocationProbe.lib'
$exportFile = Join-Path -Path $nativeBuildDir -ChildPath 'OpenOitaAllocationProbe.exp'
$args = @(
    '/nologo', '/LD', '/O2', '/EHsc', '/std:c++17', '/MD',
    "/I$MsvcRoot\include", "/I$monoInclude", "/I$ucrtInclude", "/I$umInclude", "/I$sharedInclude",
    "/Fo$obj", "/Fp$pch", "/Fe$buildOutput", $source,
    '/link', "/IMPLIB:$importLibrary", "/LIBPATH:$msvcLib", "/LIBPATH:$ucrtLib", "/LIBPATH:$umLib"
)
Write-Host "Building OpenOitaAllocationProbe.dll with $cl"
& $cl @args
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $buildOutput)) {
    throw "Native allocation probe build failed (exit=$LASTEXITCODE)."
}
Remove-Item -LiteralPath $obj -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $pch -Force -ErrorAction SilentlyContinue
Copy-Item -LiteralPath $buildOutput -Destination $output -Force
Write-Host "Built $output"
