<#
.SYNOPSIS
  BtbN/FFmpeg-Builds の GitHub Releases（rolling tag "latest"）から
  Windows 用 GPL ビルドを取得し、ffmpeg.exe / ffprobe.exe / ライセンス文書を Destination に置く。

.DESCRIPTION
  - 開発版(master)ではなく、リリース系列（ffmpeg-nX.Y-latest-win64-gpl-X.Y.zip）の最新版を選ぶ
  - BtbN が公開している checksums.sha256 で SHA-256 を検証する
  - libx264 を含む GPL 版を使う（LGPL 版には libx264 が入っていないため）
  - 入手元・バージョン・コミット・ハッシュを FFMPEG-SOURCE.txt に書き出す
  - GitHub Actions 上では GITHUB_OUTPUT に asset / version / commit / sha256 を出力する

  必要なもの: gh CLI と環境変数 GH_TOKEN（GitHub Actions なら github.token）
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repo = 'BtbN/FFmpeg-Builds'
$tag = 'latest'

Write-Host "== $repo ($tag) のアセット一覧を取得"
$assets = (gh release view $tag --repo $repo --json assets | ConvertFrom-Json).assets
if (-not $assets) { throw "アセット一覧を取得できませんでした" }

# 例: ffmpeg-n7.1-latest-win64-gpl-7.1.zip
$pattern = '^ffmpeg-n(?<ver>\d+\.\d+)-latest-win64-gpl-\k<ver>\.zip$'
$candidates = $assets | ForEach-Object {
    if ($_.name -match $pattern) {
        [pscustomobject]@{ Name = $_.name; Url = $_.url; Version = [version]$Matches['ver'] }
    }
} | Sort-Object Version -Descending

if (-not $candidates) {
    throw "win64-gpl のリリース系列ビルドが見つかりませんでした。アセット名の形式が変わった可能性があります: $($assets.name -join ', ')"
}
$asset = $candidates[0]
Write-Host "== 選択: $($asset.Name)"

$work = Join-Path ([IO.Path]::GetTempPath()) ("ffmpeg-dl-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
New-Item -ItemType Directory -Force -Path $Destination | Out-Null

gh release download $tag --repo $repo --pattern $asset.Name --dir $work --clobber
$zip = Join-Path $work $asset.Name
$hash = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
Write-Host "   SHA-256: $hash"

# チェックサム検証
$verified = $false
try {
    gh release download $tag --repo $repo --pattern 'checksums.sha256' --dir $work --clobber
    $sumFile = Join-Path $work 'checksums.sha256'
    $line = Get-Content $sumFile | Where-Object { $_ -match [regex]::Escape($asset.Name) + '$' } | Select-Object -First 1
    if ($line) {
        $expected = ($line -split '\s+')[0].ToLowerInvariant()
        if ($expected -ne $hash) { throw "SHA-256 が一致しません。期待値 $expected / 実際 $hash" }
        $verified = $true
        Write-Host "   checksums.sha256 と一致しました"
    }
}
catch {
    if ($_.Exception.Message -like '*SHA-256*') { throw }
    Write-Warning "checksums.sha256 で検証できませんでした: $($_.Exception.Message)"
}
if (-not $verified) {
    if ($env:GITHUB_ACTIONS) { Write-Host "::warning::FFmpeg のチェックサムを検証できませんでした（$($asset.Name)）" }
}

Expand-Archive -Path $zip -DestinationPath (Join-Path $work 'x') -Force
$root = Join-Path $work 'x'
$ffmpeg = Get-ChildItem -Path $root -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1
$ffprobe = Get-ChildItem -Path $root -Recurse -Filter 'ffprobe.exe' | Select-Object -First 1
$license = Get-ChildItem -Path $root -Recurse -File | Where-Object { $_.Name -match '^(LICENSE|COPYING)' } | Select-Object -First 1
if (-not $ffmpeg -or -not $ffprobe) { throw "zip 内に ffmpeg.exe / ffprobe.exe が見つかりませんでした" }
if (-not $license) { throw "zip 内にライセンス文書（LICENSE.txt）が見つかりませんでした" }

Copy-Item $ffmpeg.FullName (Join-Path $Destination 'ffmpeg.exe') -Force
Copy-Item $ffprobe.FullName (Join-Path $Destination 'ffprobe.exe') -Force
Copy-Item $license.FullName (Join-Path $Destination 'FFMPEG-LICENSE.txt') -Force

$versionLine = (& (Join-Path $Destination 'ffmpeg.exe') -hide_banner -version | Select-Object -First 1)
$configLine = (& (Join-Path $Destination 'ffmpeg.exe') -hide_banner -version | Where-Object { $_ -like 'configuration:*' } | Select-Object -First 1)
Write-Host "   $versionLine"
if ($configLine -notmatch '--enable-gpl' -or $configLine -notmatch '--enable-libx264') {
    throw "取得した FFmpeg が GPL/libx264 有効ビルドではありません: $configLine"
}

# 例: "ffmpeg version n7.1.1-20-g1234abcd-20250301 Copyright ..." → コミット 1234abcd
$ffVersion = ''
$commit = ''
if ($versionLine -match '^ffmpeg version (?<v>\S+)') { $ffVersion = $Matches['v'] }
if ($ffVersion -match '-g(?<c>[0-9a-f]{7,40})') { $commit = $Matches['c'] }

$sourceUrl = if ($commit) { "https://github.com/FFmpeg/FFmpeg/tree/$commit" } else { 'https://github.com/FFmpeg/FFmpeg' }
$downloadUrl = "https://github.com/$repo/releases/download/$tag/$($asset.Name)"
$date = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm UTC')

$info = @"
同梱 FFmpeg について / About the bundled FFmpeg
================================================

この zip に含まれる ffmpeg.exe / ffprobe.exe は PoiConvert とは別のソフトウェアです。
ffmpeg.exe and ffprobe.exe in this archive are separate programs, not part of PoiConvert itself.

ビルド       : $($asset.Name)
バージョン   : $ffVersion
入手元       : $downloadUrl
ビルド提供元 : https://github.com/$repo （ビルドスクリプトもここで公開されています）
SHA-256      : $hash
チェックサム : $(if ($verified) { 'BtbN の checksums.sha256 と一致を確認済み' } else { '未検証' })
取得日時     : $date

ライセンス / License
--------------------
このビルドは GPL 版（--enable-gpl、libx264 などを含む）で、GNU General Public License
version 3 (GPLv3) の下で配布されています。全文は同梱の FFMPEG-LICENSE.txt を参照してください。
This build is the GPL variant and is distributed under the GNU GPL version 3.
See FFMPEG-LICENSE.txt for the full license text.

ソースコード / Source code
--------------------------
FFmpeg 本体のソース（このビルドのコミット）: $sourceUrl
FFmpeg の公式サイト: https://ffmpeg.org/
同梱ライブラリ（x264 など）のソースとビルド手順: https://github.com/$repo
$(if ($commit) { "PoiConvert の GitHub Release にも、このコミットの FFmpeg ソースアーカイブ（ffmpeg-source-$commit.tar.gz）を添付しています。" })
"@
# BOM 付き UTF-8（Windows のメモ帳でも文字化けしないように）
[IO.File]::WriteAllText((Join-Path $Destination 'FFMPEG-SOURCE.txt'), $info, [Text.UTF8Encoding]::new($true))

if ($env:GITHUB_OUTPUT) {
    "asset=$($asset.Name)" >> $env:GITHUB_OUTPUT
    "version=$ffVersion" >> $env:GITHUB_OUTPUT
    "commit=$commit" >> $env:GITHUB_OUTPUT
    "sha256=$hash" >> $env:GITHUB_OUTPUT
}

Remove-Item -Recurse -Force $work
Write-Host "== 完了: $Destination"
