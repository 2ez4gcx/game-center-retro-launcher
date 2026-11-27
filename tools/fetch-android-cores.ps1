# Tải core libretro bản Android từ buildbot libretro vào src/GameCenter.Android/cores/<abi>/.
# Tên core lấy từ Defaults/platforms.json (androidCore nếu có, không thì core); file đổi tên thành
# lib<tên>_libretro_android.so để đóng được vào APK.
#   ./tools/fetch-android-cores.ps1                       # arm64-v8a (máy thật) và x86_64 (máy ảo)
#   ./tools/fetch-android-cores.ps1 -Abis arm64-v8a -Extra pcsx2_libretro
param(
    [string[]]$Abis = @('arm64-v8a', 'x86_64'),
    # Core thêm ngoài danh sách (ví dụ thử PS2: pcsx2_libretro)
    [string[]]$Extra = @(),
    [string]$BaseUrl = 'https://buildbot.libretro.com/nightly/android/latest'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$catalog = Get-Content (Join-Path $root 'Defaults/platforms.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$cores = @($catalog.platforms | Where-Object { $_.enabled -and $_.android -ne $false } | ForEach-Object {
    $c = if ($_.androidCore) { $_.androidCore } else { $_.core }
    [IO.Path]::GetFileNameWithoutExtension($c)
}) + $Extra | Sort-Object -Unique

Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($abi in $Abis) {
    $dir = Join-Path $root "src/GameCenter.Android/cores/$abi"
    New-Item -ItemType Directory -Force $dir | Out-Null
    foreach ($core in $cores) {
        $name = "${core}_android.so"
        $zip = Join-Path $env:TEMP "gc-$abi-$name.zip"
        Write-Host "$abi  $name"
        Invoke-WebRequest "$BaseUrl/$abi/$name.zip" -OutFile $zip -UseBasicParsing
        $archive = [IO.Compression.ZipFile]::OpenRead($zip)
        try {
            $entry = $archive.Entries | Where-Object { $_.Name -eq $name } | Select-Object -First 1
            if (-not $entry) { throw "Không thấy $name trong $zip" }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $dir "lib$name"), $true)
        } finally { $archive.Dispose() }
        Remove-Item $zip
    }
}
# PPSSPP (PSP) cần phông chữ và dữ liệu của nó trong <thư mục hệ thống>/PPSSPP: đóng vào APK (assets/System)
if ($cores -contains 'ppsspp_libretro') {
    $system = Join-Path $root 'src/GameCenter.Android/system'
    $zip = Join-Path $env:TEMP 'gc-PPSSPP.zip'
    Write-Host 'PPSSPP.zip (tài nguyên hệ thống)'
    Invoke-WebRequest 'https://buildbot.libretro.com/assets/system/PPSSPP.zip' -OutFile $zip -UseBasicParsing
    if (Test-Path (Join-Path $system 'PPSSPP')) { Remove-Item (Join-Path $system 'PPSSPP') -Recurse -Force }
    New-Item -ItemType Directory -Force $system | Out-Null
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $system)
    Remove-Item $zip
}

Get-ChildItem (Join-Path $root 'src/GameCenter.Android/cores') -Recurse -File | ForEach-Object { '{0,8:N1} MB  {1}' -f ($_.Length / 1MB), $_.FullName.Substring($root.Length + 1) }
