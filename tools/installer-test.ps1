<#
.SYNOPSIS
  Thử bộ cài Windows trên máy sạch: cài tiếng Anh → tạo dữ liệu người dùng → gỡ cài đặt → dữ liệu còn nguyên →
  cài lại tiếng Việt → mở Game Center (tự quét, đúng ngôn ngữ, không lỗi).

.DESCRIPTION
  Chạy trên máy ảo GitHub Actions (job installer-test trong .github/workflows/release.yml) hoặc máy thử riêng.
  KHÔNG chạy trên máy đang dùng Game Center thật: bộ cài dùng chung AppId nên sẽ ghi đè thông tin gỡ cài đặt của bản
  đang dùng (muốn thử trên máy thật thì build bản thử: ISCC /DTestBuild installer\GameCenter.iss).
  Thư mục cài mặc định có dấu tiếng Việt và dấu cách để thử đường dẫn Unicode.

.EXAMPLE
  ./tools/installer-test.ps1 -Setup ./publish/GameCenter-Setup-0.3.0.exe
#>
param(
    [Parameter(Mandatory)] [string] $Setup,
    [string] $Dir = (Join-Path ([IO.Path]::GetTempPath()) 'Game Center thử')
)
$ErrorActionPreference = 'Stop'
$Setup = (Resolve-Path $Setup).Path

function Check([bool] $ok, [string] $what) {
    if (-not $ok) { throw "THẤT BẠI: $what" }
    Write-Host "  OK  $what"
}

function Install([string] $lang) {
    $log = Join-Path ([IO.Path]::GetTempPath()) "gc-setup-$lang.log"
    $arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LANG=$lang /DIR=`"$Dir`" /MERGETASKS=`"!desktopicon`" /LOG=`"$log`""
    $p = Start-Process $Setup -ArgumentList $arguments -Wait -PassThru
    Check ($p.ExitCode -eq 0) "cài bằng ngôn ngữ $lang (mã thoát $($p.ExitCode))"
}

function Hashes([string[]] $files) {
    $h = @{}
    foreach ($f in $files) { $h[$f] = (Get-FileHash (Join-Path $Dir $f) -Algorithm SHA256).Hash }
    return $h
}

Write-Host "== 1. Cài (English) vào $Dir"
Install 'en'
foreach ($f in 'GameCenter.exe', 'Emulators\RetroArch\retroarch.exe', 'Defaults\platforms.json', 'Licenses\Core-LICENSES\fbneo.txt') {
    Check (Test-Path (Join-Path $Dir $f)) "có $f"
}
$cores = 'mednafen_psx_hw', 'pcsx2', 'snes9x', 'mgba', 'genesis_plus_gx', 'fceumm', 'gambatte', 'fbneo'
foreach ($c in $cores) { Check (Test-Path (Join-Path $Dir "Emulators\RetroArch\cores\${c}_libretro.dll")) "có core $c" }
Check (-not (Get-ChildItem $Dir -Recurse -File | Where-Object { $_.Name -like '*duckstation*' -and $_.Extension -in '.exe', '.dll' })) 'không có DuckStation'
foreach ($d in 'Games\Arcade', 'Games\PS2', 'Saves', 'BIOS') { Check (Test-Path (Join-Path $Dir $d)) "có thư mục $d" }
Check ((Get-Content (Join-Path $Dir 'Defaults\installer.ini') -Raw) -match 'Language=en') 'installer.ini ghi Language=en'

Write-Host '== 2. Dữ liệu người dùng giả'
$nes = [byte[]]::new(0x6010); [Text.Encoding]::ASCII.GetBytes('NES' + [char]0x1A).CopyTo($nes, 0)
$data = [ordered]@{
    'Games\NES\Thử nghiệm.nes'                     = $nes
    'Saves\Beetle PSX HW\Game thử.srm'             = [byte[]](1..200 | ForEach-Object { $_ % 256 })
    'States\Beetle PSX HW\Game thử.state.auto'     = [byte[]](1..100)
    'BIOS\neogeo.zip'                              = [byte[]](7, 7, 7)
    'Backups\Auto\Game thử\2026-01-01_00-00-00-000\Beetle PSX HW\Game thử.srm' = [byte[]](9, 9)
}
foreach ($f in $data.Keys) {
    $p = Join-Path $Dir $f
    New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null
    [IO.File]::WriteAllBytes($p, $data[$f])
}
$before = Hashes ([string[]]$data.Keys)

Write-Host '== 3. Gỡ cài đặt'
$p = Start-Process (Join-Path $Dir 'unins000.exe') -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait -PassThru
# unins000.exe chép mình ra thư mục tạm rồi chạy tiếp ở đó: chờ tới khi chương trình bị xóa xong
$deadline = (Get-Date).AddMinutes(5)
while (((Test-Path (Join-Path $Dir 'GameCenter.exe')) -or (Test-Path (Join-Path $Dir 'unins000.exe'))) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
Check (-not (Test-Path (Join-Path $Dir 'GameCenter.exe'))) 'đã gỡ chương trình'
Check (-not (Test-Path (Join-Path $Dir 'Emulators\RetroArch\retroarch.exe'))) 'đã gỡ RetroArch'
foreach ($f in $data.Keys) {
    $path = Join-Path $Dir $f
    Check ((Test-Path $path) -and (Get-FileHash $path -Algorithm SHA256).Hash -eq $before[$f]) "giữ nguyên $f"
}

Write-Host '== 4. Cài lại (Tiếng Việt)'
Install 'vi'
Check ((Get-Content (Join-Path $Dir 'Defaults\installer.ini') -Raw) -match 'Language=vi') 'installer.ini ghi Language=vi'
foreach ($f in $data.Keys) { Check ((Get-FileHash (Join-Path $Dir $f) -Algorithm SHA256).Hash -eq $before[$f]) "vẫn giữ nguyên $f" }

Write-Host '== 5. Mở Game Center'
$app = Start-Process (Join-Path $Dir 'GameCenter.exe') -PassThru
$logFile = Join-Path $Dir 'Logs\gamecenter.log'
$deadline = (Get-Date).AddSeconds(60)
while (-not ((Test-Path $logFile) -and ((Get-Content $logFile -Raw) -match 'Quét xong')) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
$log = if (Test-Path $logFile) { Get-Content $logFile -Raw } else { '' }
Write-Host $log
Check (-not $app.HasExited) 'Game Center đang chạy'
Check ($log -match 'Ngôn ngữ: vi') 'dùng ngôn ngữ đã chọn khi cài (vi)'
Check ($log -match 'Quét xong: .*Added = 1') 'tự quét và nhận ra game NES'
Check ($log -notmatch '\bERROR\b') 'log không có lỗi'
Stop-Process -Id $app.Id -Force
Check (Test-Path (Join-Path $Dir 'Database\games.db')) 'tạo database trong thư mục cài'

Write-Host "Xong: bộ cài $([IO.Path]::GetFileName($Setup)) qua mọi bước."
