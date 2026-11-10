# 一键构建脚本（Windows / .NET Framework 4.x 自带 csc，无需装 Visual Studio）
#   用法： powershell -ExecutionPolicy Bypass -File build.ps1
#   产物： dist\ 目录（主程序、安装包）

param([string]$OutDir = "")

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrEmpty($OutDir)) { $OutDir = Join-Path $root 'dist' }

$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "找不到 csc.exe：$csc" }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$src   = Join-Path $root 'src'
$inst  = Join-Path $root 'installer'
$asset = Join-Path $root 'assets'
$docs  = Join-Path $root 'docs'

$common = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001',
    '/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll'
)

Write-Host '[1/3] 编译主程序 …' -ForegroundColor Cyan
$mainExe = Join-Path $OutDir 'Co_cofe建模看图工具.exe'
& $csc $common `
    /win32icon:"$asset\app.ico" `
    /resource:"$asset\welcome.jpg",WELCOME_BG `
    /out:"$mainExe" `
    "$src\SwPin.cs" "$src\AssemblyInfo.cs"
if ($LASTEXITCODE -ne 0) { throw '主程序编译失败' }

Write-Host '[2/3] 编译卸载程序 …' -ForegroundColor Cyan
$unins = Join-Path $env:TEMP 'co_cofe_unins.exe'
& $csc $common `
    /win32manifest:"$inst\app.manifest" /win32icon:"$asset\app.ico" `
    /out:"$unins" "$inst\Setup.cs" /main:CoCofeSetup.UninstallProgram
if ($LASTEXITCODE -ne 0) { throw '卸载程序编译失败' }

Write-Host '[3/3] 编译安装程序 …' -ForegroundColor Cyan
$setupExe = Join-Path $OutDir 'Co_cofe建模看图工具_安装版.exe'
& $csc $common `
    /win32manifest:"$inst\app.manifest" /win32icon:"$asset\app.ico" `
    /out:"$setupExe" "$inst\Setup.cs" /main:CoCofeSetup.SetupProgram `
    /resource:"$mainExe",APP_EXE `
    /resource:"$asset\welcome.jpg",WELCOME `
    /resource:"$docs\使用说明.txt",HELP_TXT `
    /resource:"$docs\快速上手.txt",QUICK_TXT `
    /resource:"$asset\app.ico",APP_ICO `
    /resource:"$asset\logo.png",LOGO_PNG `
    /resource:"$unins",UNINS_EXE
if ($LASTEXITCODE -ne 0) { throw '安装程序编译失败' }

Remove-Item $unins -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host '构建完成：' -ForegroundColor Green
Get-ChildItem $OutDir -Filter *.exe | ForEach-Object {
    Write-Host ('  {0}  ({1} KB)' -f $_.Name, [math]::Round($_.Length / 1KB))
}
