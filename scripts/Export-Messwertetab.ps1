# ============================================================================
# Export-Messwertetab.ps1  （FR-2-9）
# 从 MEVD174K.prg 的反编译源码 doc\材料\MEVD174K.b1v 中全量导出 MESSWERTETAB
# 测量值目录表为 CSV，供：
#   1) 人工定位二期需求文档 §3.3 “待确认”采样项；
#   2) 作为运行期 ARG → RESULTNAME / EINHEIT 映射的权威数据源（嵌入 ObdLogger.exe）。
#
# 表结构（b1v 行 4900 起，TBEG "MESSWERTETAB" … TEND，共 483 条 LINE）：
#   HEAD "ARG","ID","RESULTNAME","INFO","EINHEIT","LABEL","L/H","DATENTYP","NAME","MUL","DIV","ADD"
#   换算：物理值 = 原始值 × MUL ÷ DIV + ADD（Job 返回的已是换算后物理值）。
#
# b1v 为遗留编码（德语变音字符按 Windows-1252 存储），故以 cp1252 流式读取，
# 输出为 UTF-8。文件 >20MB，使用 StreamReader 逐行流式处理，不整体载入内存。
#
# 用法： powershell -ExecutionPolicy Bypass -File scripts\Export-Messwertetab.ps1
# ============================================================================
[CmdletBinding()]
param(
    # 仓库根目录（默认由脚本位置推断：scripts\ 的上一级）
    [string]$RepoRoot
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}

# ---- 定位 b1v（避免在命令行硬编码含中文的路径） ----
$b1v = Get-ChildItem -Path (Join-Path $RepoRoot "doc") -Recurse -Filter "MEVD174K.b1v" -ErrorAction SilentlyContinue |
       Select-Object -First 1
if (-not $b1v) { throw "未找到 MEVD174K.b1v（应位于 doc\材料\ 下）" }
Write-Host "== 源文件: $($b1v.FullName) ($([math]::Round($b1v.Length/1MB,1)) MB) =="

# ---- 解析一行 TBEG/HEAD/LINE 中的全部双引号字段（字段内逗号安全；"" 视为转义引号） ----
function Get-QuotedFields([string]$line) {
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($m in [regex]::Matches($line, '"((?:[^"]|"")*)"')) {
        $val = $m.Groups[1].Value -replace '""', '"'
        $out.Add($val)
    }
    return ,$out.ToArray()
}

# ---- CSV 字段转义（始终加引号，内部引号翻倍） ----
function ConvertTo-CsvField([string]$v) {
    if ($null -eq $v) { $v = "" }
    return '"' + ($v -replace '"', '""') + '"'
}

$cp1252 = [System.Text.Encoding]::GetEncoding(1252)
$reader = New-Object System.IO.StreamReader($b1v.FullName, $cp1252)

$header = $null
$rows   = New-Object System.Collections.Generic.List[string]
$inTab  = $false
$lineNo = 0
$badRows = 0

try {
    while (($line = $reader.ReadLine()) -ne $null) {
        $lineNo++
        if (-not $inTab) {
            if ($line -match '^\s*TBEG\s+"MESSWERTETAB"') { $inTab = $true }
            continue
        }
        if ($line -match '^\s*TEND\b') { break }
        if ($line -match '^\s*HEAD\b') {
            $header = Get-QuotedFields $line
            continue
        }
        if ($line -match '^\s*LINE\b') {
            $f = Get-QuotedFields $line
            if ($header -and $f.Count -ne $header.Count) { $badRows++ }
            $rows.Add((($f | ForEach-Object { ConvertTo-CsvField $_ }) -join ','))
        }
    }
}
finally { $reader.Close() }

if (-not $header) { throw "未能定位 MESSWERTETAB 表头（HEAD 行）" }
Write-Host "== 表头 ($($header.Count) 列): $($header -join ', ') =="
Write-Host "== 导出条目: $($rows.Count) 条；TEND 于行 $lineNo；字段数异常行: $badRows =="

# ---- 组装 CSV 文本 ----
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine((($header | ForEach-Object { ConvertTo-CsvField $_ }) -join ','))
foreach ($r in $rows) {
    [void]$sb.AppendLine($r)
}
$csvText = $sb.ToString()

# ---- 输出 1：FR-2-9 交付物 doc\材料\messwertetab.csv（UTF-8 with BOM，Excel 直接打开德语变音正确） ----
$docDir = $b1v.Directory.FullName
$docOut = Join-Path $docDir "messwertetab.csv"
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllText($docOut, $csvText, $utf8Bom)
Write-Host "== 已写出交付物: $docOut =="

# ---- 输出 2：运行期嵌入资源 src\ObdLogger\Logging\messwertetab.csv（UTF-8 无 BOM） ----
$resOut = Join-Path $RepoRoot "src\ObdLogger\Logging\messwertetab.csv"
$resDir = Split-Path -Parent $resOut
if (-not (Test-Path $resDir)) { New-Item -ItemType Directory -Path $resDir -Force | Out-Null }
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($resOut, $csvText, $utf8NoBom)
Write-Host "== 已写出嵌入资源: $resOut =="

Write-Host "== 完成：两份 CSV 由同一次导出生成，内容一致（仅 BOM 差异）==" -ForegroundColor Green
