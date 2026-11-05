# OBD Logger 构建脚本（需要 .NET SDK 8+；输出固定为 32 位 / net48）
# 用法: powershell -ExecutionPolicy Bypass -File build.ps1 [-Configuration Debug]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$proj = Join-Path $PSScriptRoot "src\ObdLogger\ObdLogger.csproj"

Write-Host "== 构建 ObdLogger ($Configuration) =="
dotnet build $proj -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败，退出码 $LASTEXITCODE" }

$exe = Join-Path $PSScriptRoot "src\ObdLogger\bin\$Configuration\net48\ObdLogger.exe"
if (-not (Test-Path $exe)) { throw "未找到输出文件: $exe" }

# 校验输出确为 32 位 PE（FR-1：api32.dll 只能被 32 位进程加载）
$fs = [System.IO.File]::OpenRead($exe)
try {
    $buf = New-Object byte[] 4
    [void]$fs.Seek(0x3C, 'Begin'); [void]$fs.Read($buf, 0, 4)
    $peOffset = [BitConverter]::ToInt32($buf, 0)
    [void]$fs.Seek($peOffset + 4, 'Begin'); [void]$fs.Read($buf, 0, 2)
    $machine = [BitConverter]::ToUInt16($buf, 0)
} finally { $fs.Close() }

if ($machine -ne 0x014C) { throw ("输出不是 32 位 (x86) 程序! Machine=0x{0:X4}" -f $machine) }
Write-Host "== 构建成功: $exe （Machine=0x014C x86，可加载 api32.dll）==" -ForegroundColor Green
