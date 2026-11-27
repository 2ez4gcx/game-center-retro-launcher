<#
.SYNOPSIS
  Tạo danh sách game thùng (src/GameCenter.Core/Arcade/fbneo.dat.gz) từ DAT chính thức của FinalBurn Neo.

.DESCRIPTION
  Game Center dùng danh sách này để nhận ra game thùng (tên file zip là mã game, ví dụ kof98.zip),
  hiện tên đầy đủ và kiểm tra bộ ROM có đúng phiên bản core FBNeo cần, có thiếu BIOS / bộ gốc không.
  Chạy lại mỗi khi phát hành, để danh sách khớp với core FBNeo tải từ buildbot (bản nightly mới nhất).

  Định dạng (UTF-8, mỗi dòng một bộ ROM, các cột cách nhau bằng tab):
    tên  bộ-gốc(cloneof)  romof  cờ  năm  hãng  tên-đầy-đủ  CRC-riêng  CRC-lấy-từ-bộ-gốc
  - Cờ: b = BIOS (neogeo, pgm...), p = FBNeo chưa giả lập hoàn chỉnh (preliminary), d = cần thêm ảnh đĩa CHD.
  - CRC-riêng: ROM chỉ bộ này có (không có thuộc tính merge).
  - CRC-lấy-từ-bộ-gốc: ROM dùng chung với bộ gốc (cloneof). ROM của BIOS không ghi ở đây: chỉ cần có file BIOS.
  - ROM không có bản dump (nodump) bỏ qua.

.EXAMPLE
  ./tools/update-fbneo-dat.ps1                 # bản mới nhất trên nhánh master của libretro/FBNeo
  ./tools/update-fbneo-dat.ps1 -Ref 85c89235d5 # đúng một commit
#>
param(
    [string] $Ref = 'master',
    [string] $OutFile = "$PSScriptRoot/../src/GameCenter.Core/Arcade/fbneo.dat.gz"
)
$ErrorActionPreference = 'Stop'

$dats = 'Arcade only', 'Neogeo only'
$work = Join-Path ([IO.Path]::GetTempPath()) "gc-fbneo-dat-$PID"
New-Item -ItemType Directory -Force $work | Out-Null

# Commit cụ thể để ghi vào đầu file (biết danh sách ứng với bản FBNeo nào)
$commit = (Invoke-RestMethod "https://api.github.com/repos/libretro/FBNeo/commits/$Ref" -UseBasicParsing).sha

$sets = [ordered]@{}
$version = ''
foreach ($dat in $dats) {
    $file = Join-Path $work "$dat.dat"
    $url = "https://raw.githubusercontent.com/libretro/FBNeo/$commit/dats/" + [Uri]::EscapeDataString("FinalBurn Neo (ClrMame Pro XML, $dat).dat")
    Write-Host "Tải $url"
    Invoke-WebRequest $url -OutFile $file -UseBasicParsing

    $settings = New-Object System.Xml.XmlReaderSettings
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Ignore
    $reader = [System.Xml.XmlReader]::Create($file, $settings)
    $cur = $null
    # ReadElementContentAsString đã chuyển sang nút kế tiếp: lần lặp sau không gọi Read() nữa
    $advance = $true
    try {
        while ($true) {
            if ($advance -and -not $reader.Read()) { break }
            $advance = $true
            if ($reader.NodeType -ne [System.Xml.XmlNodeType]::Element) { continue }
            switch ($reader.Name) {
                'version' { if (-not $version) { $version = $reader.ReadElementContentAsString(); $advance = $false } }
                'game' {
                    $name = $reader.GetAttribute('name')
                    # Bộ ROM có ở cả hai DAT (Neo Geo nằm trong cả DAT arcade): giữ bản đọc trước
                    if ($sets.Contains($name)) { $cur = $null; break }
                    $cur = [pscustomobject]@{
                        Name = $name; CloneOf = [string]$reader.GetAttribute('cloneof'); RomOf = [string]$reader.GetAttribute('romof')
                        Bios = $reader.GetAttribute('isbios') -eq 'yes'; Preliminary = $false; Disk = $false
                        Year = ''; Maker = ''; Title = ''
                        Own = New-Object System.Collections.Generic.List[string]
                        Merged = New-Object System.Collections.Generic.List[string]
                    }
                    $sets[$name] = $cur
                }
                'description' { if ($cur) { $cur.Title = $reader.ReadElementContentAsString(); $advance = $false } }
                'year' { if ($cur) { $cur.Year = $reader.ReadElementContentAsString(); $advance = $false } }
                'manufacturer' { if ($cur) { $cur.Maker = $reader.ReadElementContentAsString(); $advance = $false } }
                'rom' {
                    if (-not $cur) { break }
                    $crc = $reader.GetAttribute('crc')
                    if (-not $crc -or $reader.GetAttribute('status') -eq 'nodump') { break }
                    if ($reader.GetAttribute('merge')) { $cur.Merged.Add($crc.ToLowerInvariant()) } else { $cur.Own.Add($crc.ToLowerInvariant()) }
                }
                'disk' { if ($cur) { $cur.Disk = $true } }
                'driver' { if ($cur -and $reader.GetAttribute('status') -ne 'good') { $cur.Preliminary = $true } }
            }
        }
    }
    finally { $reader.Close() }
}

# BIOS của một bộ: đi theo romof (bộ gốc → BIOS) tới bộ có isbios
function Get-BiosOf($set) {
    $seen = @{}
    $s = $set
    while ($s -and $s.RomOf -and -not $seen.ContainsKey($s.Name)) {
        $seen[$s.Name] = $true
        $next = $sets[$s.RomOf]
        if (-not $next) { return $null }
        if ($next.Bios) { return $next }
        $s = $next
    }
    return $null
}

function Clean([string] $text) { ($text -replace '[\t\r\n]+', ' ').Trim() }

$sb = New-Object System.Text.StringBuilder
[void]$sb.Append("# FinalBurn Neo $version, libretro/FBNeo $commit. Tạo bởi tools/update-fbneo-dat.ps1, không sửa tay.`n")
foreach ($s in $sets.Values) {
    $parentCrcs = @()
    if ($s.Merged.Count -gt 0) {
        $bios = Get-BiosOf $s
        $biosCrcs = @{}
        if ($bios) { foreach ($c in $bios.Own) { $biosCrcs[$c] = $true } }
        $parentCrcs = @($s.Merged | Where-Object { -not $biosCrcs.ContainsKey($_) } | Select-Object -Unique)
    }
    $flags = ''
    if ($s.Bios) { $flags += 'b' }
    if ($s.Preliminary) { $flags += 'p' }
    if ($s.Disk) { $flags += 'd' }
    $cols = $s.Name, $s.CloneOf, $s.RomOf, $flags, (Clean $s.Year), (Clean $s.Maker), (Clean $s.Title),
        (($s.Own | Select-Object -Unique) -join ' '), ($parentCrcs -join ' ')
    [void]$sb.Append(($cols -join "`t")).Append("`n")
}

$out = [IO.Path]::GetFullPath($OutFile)
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
$bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($sb.ToString())
$fs = [IO.File]::Create($out)
try {
    $gz = New-Object System.IO.Compression.GZipStream($fs, [System.IO.Compression.CompressionLevel]::Optimal)
    $gz.Write($bytes, 0, $bytes.Length)
    $gz.Dispose()
}
finally { $fs.Dispose() }
Remove-Item -Recurse -Force $work

Write-Host ("Xong: {0} bộ ROM (FBNeo {1}, {2}) → {3} ({4:N0} KB)" -f $sets.Count, $version, $commit.Substring(0, 10), $out, ((Get-Item $out).Length / 1KB))
