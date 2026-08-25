[CmdletBinding()]
param(
    [ValidateSet("Install", "Uninstall", "Verify")]
    [string]$Mode = "Install",

    [Parameter(Position = 0)]
    [string]$GamePath,

    [string]$LocalizationRoot,

    [switch]$SkipProcessCheck
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$PatchVersion = "1.0.0"
$GameVersion = "1.7.4"
$PackageRoot = Split-Path -Parent $PSScriptRoot
$LocalizationSource = Join-Path $PackageRoot "localization\ko"
$AssetPatch = Join-Path $PackageRoot "patches\sharedassets0.assets.kpatch.gz"
$AssemblyPatch = Join-Path $PackageRoot "patches\Assembly-CSharp.dll.kpatch.gz"
$MarkerName = ".decktamer-korean-patch.json"

$OriginalAssetHash = "ba21274137c1f6a8b896cc25d2a316228b6ca9861b3c25e349395ea13a4fa6cf"
$PatchedAssetHash = "2027eeecc0f3657ecc08cc483db48be942b582b0a7cec509b6b2f23d4350c050"
$OriginalAssemblyHash = "ee5dc47461f2776fa83d4acbb17d0434e12e76f0ac54ebc631a8c7cbb3225b5b"
$PatchedAssemblyHash = "8a0a8e93d51c707d77f69e69624dc8191df4373d5aea4c60f2f1255961ba3f85"

function Get-LowerHash([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Test-GameRoot([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    $candidate = $Path.Trim('"')
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        $candidate = Split-Path -Parent $candidate
    }
    if ((Split-Path -Leaf $candidate) -eq "Decktamer_Data") {
        $candidate = Split-Path -Parent $candidate
    }
    return (
        (Test-Path -LiteralPath (Join-Path $candidate "Decktamer_Data\sharedassets0.assets") -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $candidate "Decktamer_Data\Managed\Assembly-CSharp.dll") -PathType Leaf)
    )
}

function Get-SteamCandidates {
    $steamRoots = [System.Collections.Generic.List[string]]::new()
    foreach ($registryPath in @(
        "HKCU:\Software\Valve\Steam",
        "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam",
        "HKLM:\SOFTWARE\Valve\Steam"
    )) {
        try {
            $value = Get-ItemProperty -LiteralPath $registryPath -ErrorAction Stop
            foreach ($property in @("SteamPath", "InstallPath")) {
                if ($value.PSObject.Properties.Name -contains $property) {
                    $steamRoots.Add([string]$value.$property)
                }
            }
        } catch { }
    }

    $libraries = [System.Collections.Generic.List[string]]::new()
    foreach ($steamRoot in $steamRoots) {
        if (-not [string]::IsNullOrWhiteSpace($steamRoot)) {
            $libraries.Add($steamRoot)
            $vdf = Join-Path $steamRoot "steamapps\libraryfolders.vdf"
            if (Test-Path -LiteralPath $vdf -PathType Leaf) {
                foreach ($line in Get-Content -LiteralPath $vdf) {
                    if ($line -match '"path"\s+"([^"]+)"') {
                        $libraries.Add(($Matches[1] -replace '\\\\', '\'))
                    }
                }
            }
        }
    }

    foreach ($library in $libraries | Select-Object -Unique) {
        $common = Join-Path $library "steamapps\common"
        if (-not (Test-Path -LiteralPath $common -PathType Container)) { continue }
        $exact = Join-Path $common "Decktamer"
        if (Test-GameRoot $exact) { $exact }
        Get-ChildItem -LiteralPath $common -Directory -Filter "*Decktamer*" -ErrorAction SilentlyContinue |
            ForEach-Object {
                if (Test-GameRoot $_.FullName) { $_.FullName }
                $nested = Join-Path $_.FullName "game"
                if (Test-GameRoot $nested) { $nested }
            }
    }
}

function Resolve-GameRoot([string]$RequestedPath) {
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        if (-not (Test-GameRoot $RequestedPath)) {
            throw "지정한 경로에서 Decktamer 1.7.4 게임 파일을 찾지 못했습니다: $RequestedPath"
        }
        $resolved = $RequestedPath.Trim('"')
        if (Test-Path -LiteralPath $resolved -PathType Leaf) { $resolved = Split-Path -Parent $resolved }
        if ((Split-Path -Leaf $resolved) -eq "Decktamer_Data") { $resolved = Split-Path -Parent $resolved }
        return (Resolve-Path -LiteralPath $resolved).Path
    }

    $localCandidates = @((Get-Location).Path, $PackageRoot, (Split-Path -Parent $PackageRoot))
    foreach ($candidate in $localCandidates + @(Get-SteamCandidates)) {
        if (Test-GameRoot $candidate) { return (Resolve-Path -LiteralPath $candidate).Path }
    }

    try {
        Add-Type -AssemblyName System.Windows.Forms
        $dialog = [System.Windows.Forms.FolderBrowserDialog]::new()
        $dialog.Description = "Decktamer.exe와 Decktamer_Data 폴더가 있는 게임 폴더를 선택하세요."
        $dialog.ShowNewFolderButton = $false
        if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK -and (Test-GameRoot $dialog.SelectedPath)) {
            return (Resolve-Path -LiteralPath $dialog.SelectedPath).Path
        }
    } catch { }

    throw '게임 폴더를 자동으로 찾지 못했습니다. 배치 파일에 게임 폴더를 끌어다 놓거나 -GamePath "경로"로 실행하세요.'
}

function Get-DefaultLocalizationRoot {
    $local = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    $localLow = Join-Path (Split-Path -Parent $local) "LocalLow"
    return Join-Path $localLow "Horizon Edge\Decktamer\Localization"
}

function Convert-HashBytes([byte[]]$Bytes) {
    return (($Bytes | ForEach-Object { $_.ToString("x2") }) -join "")
}

function Read-PatchHeader([string]$PatchPath) {
    $file = [IO.File]::OpenRead($PatchPath)
    try {
        $gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionMode]::Decompress)
        try {
            $reader = [IO.BinaryReader]::new($gzip, [Text.Encoding]::UTF8, $true)
            try {
                $magic = [Text.Encoding]::ASCII.GetString($reader.ReadBytes(8))
                if ($magic -ne "DKTKO174") { throw "지원하지 않는 패치 형식입니다: $PatchPath" }
                $formatVersion = $reader.ReadUInt32()
                if ($formatVersion -ne 1) { throw "지원하지 않는 패치 버전입니다: $formatVersion" }
                return [pscustomobject]@{
                    SegmentCount = $reader.ReadUInt32()
                    OriginalSize = $reader.ReadInt64()
                    PatchedSize = $reader.ReadInt64()
                    OriginalHash = Convert-HashBytes $reader.ReadBytes(32)
                    PatchedHash = Convert-HashBytes $reader.ReadBytes(32)
                }
            } finally { $reader.Dispose() }
        } finally { $gzip.Dispose() }
    } finally { $file.Dispose() }
}

function Apply-BinaryDelta([string]$TargetPath, [string]$PatchPath) {
    $header = Read-PatchHeader $PatchPath
    $currentHash = Get-LowerHash $TargetPath
    if ($currentHash -ne $header.OriginalHash) {
        throw "패치 입력 파일의 해시가 원본과 다릅니다: $TargetPath"
    }
    if ((Get-Item -LiteralPath $TargetPath).Length -ne $header.OriginalSize) {
        throw "패치 입력 파일의 크기가 원본과 다릅니다: $TargetPath"
    }

    $tempPath = "$TargetPath.decktamer-ko-$([Guid]::NewGuid().ToString('N')).tmp"
    Copy-Item -LiteralPath $TargetPath -Destination $tempPath
    try {
        $patchFile = [IO.File]::OpenRead($PatchPath)
        $gzip = [IO.Compression.GZipStream]::new($patchFile, [IO.Compression.CompressionMode]::Decompress)
        $reader = [IO.BinaryReader]::new($gzip, [Text.Encoding]::UTF8, $true)
        $output = [IO.File]::Open($tempPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        try {
            [void]$reader.ReadBytes(8)
            [void]$reader.ReadUInt32()
            $segmentCount = $reader.ReadUInt32()
            [void]$reader.ReadInt64()
            [void]$reader.ReadInt64()
            [void]$reader.ReadBytes(32)
            [void]$reader.ReadBytes(32)
            $lastEnd = 0L
            for ($index = 0; $index -lt $segmentCount; $index++) {
                $offset = $reader.ReadInt64()
                $length = $reader.ReadInt32()
                if ($offset -lt $lastEnd -or $length -le 0 -or ($offset + $length) -gt $header.PatchedSize) {
                    throw "패치 조각 정보가 올바르지 않습니다: $PatchPath"
                }
                $payload = $reader.ReadBytes($length)
                if ($payload.Length -ne $length) { throw "패치 데이터가 중간에서 끝났습니다: $PatchPath" }
                $output.Position = $offset
                $output.Write($payload, 0, $payload.Length)
                $lastEnd = $offset + $length
            }
            $output.SetLength($header.PatchedSize)
            $output.Flush($true)
        } finally {
            $output.Dispose()
            $reader.Dispose()
            $gzip.Dispose()
            $patchFile.Dispose()
        }

        $resultHash = Get-LowerHash $tempPath
        if ($resultHash -ne $header.PatchedHash) {
            throw "패치 결과 검증에 실패했습니다: $TargetPath"
        }
        Move-Item -LiteralPath $tempPath -Destination $TargetPath -Force
    } finally {
        if (Test-Path -LiteralPath $tempPath) { Remove-Item -LiteralPath $tempPath -Force }
    }
}

function Install-PatchedFile(
    [string]$TargetPath,
    [string]$PatchPath,
    [string]$BackupPath,
    [string]$ExpectedOriginal,
    [string]$ExpectedPatched
) {
    if (-not (Test-Path -LiteralPath $TargetPath -PathType Leaf)) { throw "게임 파일이 없습니다: $TargetPath" }
    if (-not (Test-Path -LiteralPath $PatchPath -PathType Leaf)) { throw "배포 패치 파일이 없습니다: $PatchPath" }
    $header = Read-PatchHeader $PatchPath
    if ($header.OriginalHash -ne $ExpectedOriginal -or $header.PatchedHash -ne $ExpectedPatched) {
        throw "배포 패치의 무결성 정보가 예상값과 다릅니다: $PatchPath"
    }

    $currentHash = Get-LowerHash $TargetPath
    if ($currentHash -eq $ExpectedPatched) {
        if (-not (Test-Path -LiteralPath $BackupPath -PathType Leaf) -or (Get-LowerHash $BackupPath) -ne $ExpectedOriginal) {
            throw "이미 패치된 파일의 정상 원본 백업을 찾지 못했습니다: $BackupPath"
        }
        Write-Host "이미 적용됨: $([IO.Path]::GetFileName($TargetPath))"
        return
    }
    if ($currentHash -ne $ExpectedOriginal) {
        throw "지원하지 않는 파일입니다. Decktamer 1.7.4 원본이 아니거나 다른 모드가 적용되어 있습니다:`n$TargetPath`nSHA-256: $currentHash"
    }

    if (Test-Path -LiteralPath $BackupPath -PathType Leaf) {
        if ((Get-LowerHash $BackupPath) -ne $ExpectedOriginal) {
            throw "기존 백업 파일이 검증된 원본과 다릅니다. 덮어쓰지 않습니다: $BackupPath"
        }
    } else {
        Copy-Item -LiteralPath $TargetPath -Destination $BackupPath
    }

    Apply-BinaryDelta $TargetPath $PatchPath
    if ((Get-LowerHash $TargetPath) -ne $ExpectedPatched) { throw "설치 후 파일 검증에 실패했습니다: $TargetPath" }
    Write-Host "적용 완료: $([IO.Path]::GetFileName($TargetPath))"
}

function Install-Localization([string]$TargetRoot, [string]$BackupRoot) {
    if (-not (Test-Path -LiteralPath $LocalizationSource -PathType Container)) {
        throw "배포 번역 폴더가 없습니다: $LocalizationSource"
    }
    $sourceFiles = @(Get-ChildItem -LiteralPath $LocalizationSource -Filter "*.csv" -File)
    if ($sourceFiles.Count -ne 37) { throw "배포 번역 표가 37개가 아닙니다." }

    New-Item -ItemType Directory -Path $TargetRoot -Force | Out-Null
    $target = Join-Path $TargetRoot "ko"
    $prePatch = Join-Path $BackupRoot "Localization_ko_before_patch"
    $marker = Join-Path $target $MarkerName

    if (Test-Path -LiteralPath $target -PathType Container) {
        if (Test-Path -LiteralPath $marker -PathType Leaf) {
            Remove-Item -LiteralPath $target -Recurse -Force
        } else {
            if (Test-Path -LiteralPath $prePatch) {
                throw "기존 한국어 번역 백업이 이미 있어 현재 ko 폴더를 덮어쓸 수 없습니다: $prePatch"
            }
            Move-Item -LiteralPath $target -Destination $prePatch
        }
    }

    Copy-Item -LiteralPath $LocalizationSource -Destination $target -Recurse
    $markerData = [ordered]@{
        patch = "Decktamer Korean Patch"
        patch_version = $PatchVersion
        game_version = $GameVersion
        installed_at = [DateTimeOffset]::Now.ToString("o")
        tables = 37
        rows = 3039
    }
    $markerData | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $target $MarkerName) -Encoding UTF8
    Write-Host "번역 적용 완료: $target"
}

function Restore-PatchedFile(
    [string]$TargetPath,
    [string]$BackupPath,
    [string]$ExpectedOriginal,
    [string]$ExpectedPatched
) {
    if (-not (Test-Path -LiteralPath $BackupPath -PathType Leaf)) {
        Write-Warning "원본 백업이 없어 건너뜁니다: $BackupPath"
        return
    }
    if ((Get-LowerHash $BackupPath) -ne $ExpectedOriginal) {
        throw "백업 파일이 검증된 원본과 다릅니다: $BackupPath"
    }
    $currentHash = Get-LowerHash $TargetPath
    if ($currentHash -eq $ExpectedOriginal) {
        Write-Host "이미 원본 상태: $([IO.Path]::GetFileName($TargetPath))"
        return
    }
    if ($currentHash -ne $ExpectedPatched) {
        throw "현재 파일이 한글패치본과 달라 덮어쓰지 않습니다: $TargetPath"
    }
    Copy-Item -LiteralPath $BackupPath -Destination $TargetPath -Force
    if ((Get-LowerHash $TargetPath) -ne $ExpectedOriginal) { throw "원본 복구 검증에 실패했습니다: $TargetPath" }
    Write-Host "원본 복구 완료: $([IO.Path]::GetFileName($TargetPath))"
}

function Restore-Localization([string]$TargetRoot, [string]$BackupRoot) {
    $target = Join-Path $TargetRoot "ko"
    $marker = Join-Path $target $MarkerName
    $prePatch = Join-Path $BackupRoot "Localization_ko_before_patch"
    if (Test-Path -LiteralPath $target -PathType Container) {
        if (Test-Path -LiteralPath $marker -PathType Leaf) {
            Remove-Item -LiteralPath $target -Recurse -Force
            Write-Host "한글패치 번역 제거 완료"
        } else {
            Write-Warning "ko 폴더가 한글패치 설치본으로 확인되지 않아 삭제하지 않습니다: $target"
        }
    }
    if (Test-Path -LiteralPath $prePatch -PathType Container) {
        if (Test-Path -LiteralPath $target) {
            throw "기존 번역을 복원할 위치가 이미 사용 중입니다: $target"
        }
        Move-Item -LiteralPath $prePatch -Destination $target
        Write-Host "설치 전 ko 폴더 복원 완료"
    }
}

function Assert-Installed([string]$AssetPath, [string]$AssemblyPath, [string]$TargetRoot) {
    if ((Get-LowerHash $AssetPath) -ne $PatchedAssetHash) { throw "폰트 패치 파일 검증 실패: $AssetPath" }
    if ((Get-LowerHash $AssemblyPath) -ne $PatchedAssemblyHash) { throw "언어 전환 패치 파일 검증 실패: $AssemblyPath" }
    $target = Join-Path $TargetRoot "ko"
    $sourceFiles = @(Get-ChildItem -LiteralPath $LocalizationSource -Filter "*.csv" -File)
    $targetFiles = @(Get-ChildItem -LiteralPath $target -Filter "*.csv" -File)
    if ($sourceFiles.Count -ne 37 -or $targetFiles.Count -ne 37) { throw "번역 표 개수 검증 실패" }
    foreach ($source in $sourceFiles) {
        $installed = Join-Path $target $source.Name
        if (-not (Test-Path -LiteralPath $installed) -or (Get-LowerHash $source.FullName) -ne (Get-LowerHash $installed)) {
            throw "번역 파일 검증 실패: $($source.Name)"
        }
    }
    Write-Host "검증 완료: Decktamer $GameVersion / 한글패치 $PatchVersion"
}

if (-not $SkipProcessCheck) {
    $gameProcess = Get-Process -Name "Decktamer" -ErrorAction SilentlyContinue
    if ($gameProcess) { throw "Decktamer가 실행 중입니다. 게임을 완전히 종료한 뒤 다시 실행하세요." }
}

$resolvedGame = Resolve-GameRoot $GamePath
if ([string]::IsNullOrWhiteSpace($LocalizationRoot)) { $LocalizationRoot = Get-DefaultLocalizationRoot }
$dataDir = Join-Path $resolvedGame "Decktamer_Data"
$assetTarget = Join-Path $dataDir "sharedassets0.assets"
$assemblyTarget = Join-Path $dataDir "Managed\Assembly-CSharp.dll"
$backupDir = Join-Path $resolvedGame "KoreanPatch_Backup_1.7.4"
$assetBackup = Join-Path $backupDir "sharedassets0.assets"
$assemblyBackup = Join-Path $backupDir "Assembly-CSharp.dll"

Write-Host "게임 경로: $resolvedGame"
Write-Host "실행 작업: $Mode"

switch ($Mode) {
    "Install" {
        New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
        Install-PatchedFile $assetTarget $AssetPatch $assetBackup $OriginalAssetHash $PatchedAssetHash
        Install-PatchedFile $assemblyTarget $AssemblyPatch $assemblyBackup $OriginalAssemblyHash $PatchedAssemblyHash
        Install-Localization $LocalizationRoot $backupDir
        Assert-Installed $assetTarget $assemblyTarget $LocalizationRoot
        Write-Host "`n설치가 끝났습니다. 게임 설정에서 언어를 한국어로 선택하세요." -ForegroundColor Green
    }
    "Uninstall" {
        Restore-PatchedFile $assetTarget $assetBackup $OriginalAssetHash $PatchedAssetHash
        Restore-PatchedFile $assemblyTarget $assemblyBackup $OriginalAssemblyHash $PatchedAssemblyHash
        Restore-Localization $LocalizationRoot $backupDir
        Write-Host "`n한글패치를 제거하고 원본을 복구했습니다. 백업 폴더는 안전을 위해 남겨 두었습니다." -ForegroundColor Green
    }
    "Verify" {
        Assert-Installed $assetTarget $assemblyTarget $LocalizationRoot
    }
}
