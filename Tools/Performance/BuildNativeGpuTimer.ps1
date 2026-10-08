[CmdletBinding()]
param(
    [string]$UnityEditorPath = $env:OPENOITA_UNITY_EDITOR,
    [string]$MsvcRoot = 'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\MSVC\14.44.35207',
    [string]$WindowsSdkVersion = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path -Path $PSScriptRoot -ChildPath '..\..')).Path
$source = Join-Path -Path $repoRoot -ChildPath 'Tools\Performance\NativeGpuTimer.cpp'
$outputDir = Join-Path -Path $repoRoot -ChildPath 'Assets\Plugins\OpenOita\x86_64'
$output = Join-Path -Path $outputDir -ChildPath 'OpenOitaGpuTimer.dll'
$importLibrary = Join-Path -Path $outputDir -ChildPath 'OpenOitaGpuTimer.lib'
$exportFile = Join-Path -Path $outputDir -ChildPath 'OpenOitaGpuTimer.exp'

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
$pluginApi = Join-Path -Path (Split-Path -Parent $UnityEditorPath) -ChildPath 'Data\PluginAPI'
if (-not (Test-Path -LiteralPath (Join-Path -Path $pluginApi -ChildPath 'IUnityGraphicsD3D11.h'))) {
    throw "找不到 Unity PluginAPI：$pluginApi"
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
$obj = Join-Path -Path $outputDir -ChildPath 'NativeGpuTimer.obj'
$pch = Join-Path -Path $outputDir -ChildPath 'NativeGpuTimer.pch'
$args = @(
    '/nologo', '/LD', '/O2', '/EHsc', '/std:c++17', '/MD',
    '/DUNICODE', '/D_UNICODE',
    "/I$MsvcRoot\include", "/I$pluginApi", "/I$ucrtInclude", "/I$umInclude", "/I$sharedInclude",
    "/Fo$obj", "/Fp$pch", "/Fe$output", $source,
    '/link', "/LIBPATH:$msvcLib", "/LIBPATH:$ucrtLib", "/LIBPATH:$umLib", 'd3d11.lib', 'dxgi.lib'
)
Write-Host "Building OpenOitaGpuTimer.dll with $cl"
& $cl @args
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $output)) {
    throw "Native GPU timer build failed (exit=$LASTEXITCODE)."
}
Remove-Item -LiteralPath $obj -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $pch -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $importLibrary -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $exportFile -Force -ErrorAction SilentlyContinue
Write-Host "Built $output"
