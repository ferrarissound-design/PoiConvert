# PoiConvert — 動画・音声 爆速コンバーター

動画・音声ファイルを**ドラッグ＆ドロップするだけ**で、
「音声抽出」「軽量化（圧縮）」「GIF化」「形式変換」ができる Windows 用デスクトップアプリです。
変換処理は裏で [FFmpeg](https://ffmpeg.org/)（ffmpeg.exe / ffprobe.exe）を呼び出しています。

- Windows 10 / 11（64bit）
- インストール不要・.NET のインストールも不要（exe 1つで動きます）
- 画面はすべて日本語

## できること

| モード | 内容 |
|---|---|
| 🎵 音声抽出 | MP3 / M4A(AAC) / WAV / 元のまま（再エンコードなし）。ビットレート 128 / 192 / 256 / 320 kbps |
| 📦 軽量化 | H.264 + AAC の MP4 に圧縮。画質プリセット（高画質 CRF20 / 標準 CRF23 / 軽量 CRF28）、解像度（元のまま / 1080p / 720p / 480p・縮小のみ）、**目標サイズ（MB）指定**（2パス） |
| 🎞 GIF化 | 開始時間・長さ・FPS（10/15/20）・横幅（320/480/640/元のまま）を指定。パレット生成 → 適用の2段階で高画質 |
| 🔁 形式変換 | MP4 / MOV / MKV / WEBM へ。可能なら再エンコードなし（-c copy）で一瞬＆無劣化 |

そのほか：

- 複数ファイルをまとめてキューに入れて順番に処理（同時実行数 1〜2）
- ファイルごとの進捗バーと全体の進捗
- キャンセル（ffmpeg を確実に停止し、作りかけのファイルは削除）
- 失敗したファイルだけ「失敗」にして次へ進む。ffmpeg のエラー出力の末尾はログ欄で確認できる
- **同じ名前のファイルがあっても絶対に上書きしない**（`_1`、`_2` … と連番を付ける）
- 設定（最後に使ったモード・オプション・出力先・FFmpeg の場所）は `%AppData%\PoiConvert\settings.json` に保存

## 使い方

1. ウィンドウに動画・音声ファイル（またはフォルダ）を**ドラッグ＆ドロップ**（「＋ ファイルを追加」ボタンでも可）
   - 一覧に「長さ・解像度・サイズ」が表示されます
   - 不要なファイルは選んで「選択を削除」（Delete キーでも可）、「全クリア」で全部消せます
2. 上のタブで**モードを選んで**オプションを設定
3. 出力先を選ぶ（元ファイルと同じフォルダ / 指定フォルダ）
4. **「▶ 変換開始」**
5. 終わったら「📂 出力フォルダを開く」

出力ファイル名の例：

| モード | 元ファイル `旅行.mov` からの出力 |
|---|---|
| 音声抽出 | `旅行_audio.mp3`（M4A なら `.m4a` など） |
| 軽量化 | `旅行_small.mp4` |
| GIF化 | `旅行.gif` |
| 形式変換 | `旅行.mp4`（同じ形式に変換したときは `旅行_1.mp4`） |

## ダウンロード

[Releases](https://github.com/ferrarissound-design/PoiConvert/releases) から、どちらかをダウンロードしてください。

| ファイル | こんな人向け |
|---|---|
| **`PoiConvert-with-ffmpeg-v○○.zip`** | **迷ったらこちら。** 展開して `PoiConvert.exe` を起動するだけ。FFmpeg（ffmpeg.exe / ffprobe.exe）とそのライセンス文書が同梱されています |
| `PoiConvert.exe` | すでに FFmpeg を持っている人向けの本体のみ版 |

> 初回起動時に Windows SmartScreen の警告が出ることがあります（署名していないため）。
> 「詳細情報」→「実行」で起動できます。

## FFmpeg の入手・配置

同梱版 zip を使う場合は何もしなくて OK です。本体のみ版を使う場合は次のどちらかで用意してください。

1. Windows 用 FFmpeg ビルドを入手する
   - 例：[BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds/releases) の `ffmpeg-n○.○-latest-win64-gpl-○.○.zip`
   - または [ffmpeg.org のダウンロードページ](https://ffmpeg.org/download.html) から Windows 版
   - ※ 軽量化・形式変換で libx264 を使うので **GPL 版**（`-gpl`）を選んでください（LGPL 版には libx264 が入っていません）
2. zip を展開し、`bin` フォルダの中の **`ffmpeg.exe` と `ffprobe.exe` の2つ**を、
   **`PoiConvert.exe` と同じフォルダ**にコピーする

別の場所に置いた FFmpeg を使いたい場合は、アプリの「設定 → FFmpeg → 変更…」で `ffmpeg.exe` を指定できます。

PoiConvert は次の順番で FFmpeg を探します。

1. `PoiConvert.exe` と同じフォルダ
2. 設定で指定したパス
3. 環境変数 `PATH`

見つからない場合は起動時に案内ダイアログが出ます。

## ライセンス表記

### FFmpeg

同梱版 zip に含まれる `ffmpeg.exe` / `ffprobe.exe` は PoiConvert とは別のソフトウェアで、
[FFmpeg](https://ffmpeg.org/) プロジェクトの成果物を [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) がビルドしたもの
（Windows 64bit・**GPL 版**）です。

- ライセンス：**GNU General Public License version 3（GPLv3）**
  （GPL 版は libx264 など GPL のライブラリを含むため、ビルド全体が GPLv3 になります）
- zip 内の `FFMPEG-LICENSE.txt`：ライセンス全文（ビルドに同梱されているもの）
- zip 内の `FFMPEG-SOURCE.txt`：ビルド名・バージョン・入手元 URL・SHA-256・ソースコードの入手先
- ソースコード：
  - FFmpeg 本体：<https://github.com/FFmpeg/FFmpeg>（`FFMPEG-SOURCE.txt` に該当コミットを記載）
  - 各 Release に、同梱 FFmpeg と同じコミットのソースアーカイブ `ffmpeg-source-<commit>.tar.gz` も添付しています
  - 同梱ライブラリ（x264 など）のソースとビルド手順：<https://github.com/BtbN/FFmpeg-Builds>

PoiConvert 本体は ffmpeg.exe を別プロセスとして起動するだけで、FFmpeg のコードを組み込んではいません。

FFmpeg は FFmpeg 開発者の商標です。PoiConvert は FFmpeg プロジェクトとは無関係です。

## 開発者向け

### 構成

```
PoiConvert.sln
├─ src/PoiConvert.Core/       UI から独立したロジック（net8.0）
│   ├─ FfmpegCommandBuilder   各モードの ffmpeg 引数を組み立てる（純粋関数）
│   ├─ OutputPathResolver     上書きしない連番ファイル名（_1, _2 …）
│   ├─ FfprobeParser          ffprobe の JSON 出力を解析
│   ├─ ProgressParser         -progress pipe:1 の出力を解析
│   ├─ ProcessRunner          ArgumentList でプロセス起動・キャンセル時はツリーごと kill
│   ├─ ConversionService      1ファイル分の変換（予約→実行→後片付け）
│   ├─ FfmpegLocator          ffmpeg / ffprobe の探索
│   └─ AppSettings            settings.json の読み書き
├─ src/PoiConvert/            WPF アプリ（net8.0-windows）
└─ tests/PoiConvert.Core.Tests/  xUnit
    ├─ 各モード・各オプションのコマンド組み立てテスト
    ├─ 連番ロジック・ffprobe/進捗解析・設定・FFmpeg 探索のテスト
    └─ 結合テスト（本物の ffmpeg で変換。ffmpeg が無い環境では自動スキップ）
```

ファイルパスは必ず `ProcessStartInfo.ArgumentList` で 1 引数ずつ渡しているので、
空白・日本語・記号（`&` `'` `#` `(` `)` など）を含むパスでも壊れません。
出力には必ず ffmpeg の `-n`（上書き禁止）を付けており、連番ロジックと合わせて二重に上書きを防いでいます。

### ビルド・テスト

```powershell
dotnet build PoiConvert.sln -c Release
dotnet test PoiConvert.sln -c Release

# 結合テストで使う ffmpeg を明示する場合（ffmpeg.exe と ffprobe.exe があるフォルダ）
$env:POICONVERT_FFMPEG = "C:\tools\ffmpeg\bin"
dotnet test PoiConvert.sln -c Release

# 単一ファイル exe を作る
dotnet publish src/PoiConvert/PoiConvert.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

### GitHub Actions

- **build**（push / PR のたび）：windows-latest で restore → build → test。
  FFmpeg を取得して結合テストも実行し、動作確認用の exe を Artifacts（`PoiConvert-preview-exe`）に置きます。
- **release**（手動）：下記の手順でリリースを作成します。

### リリース手順

1. GitHub のリポジトリで **Actions** タブを開く
2. 左の一覧から **release** を選ぶ
3. 右側の **Run workflow** を押し、ブランチ（通常 `main`）を選んで **version** に `1.0.0` のように入力
4. **Run workflow** を押すと、次が自動で行われます
   1. 入力チェック（形式が `1.0.0` / `1.2.3-beta.1` でない、またはタグ `v{version}` が既にあれば**失敗して止まる**）
   2. FFmpeg（BtbN/FFmpeg-Builds の win64 GPL 最新リリース系列）をダウンロードし SHA-256 を検証
   3. テスト（同梱する ffmpeg で結合テストも実行）
   4. `dotnet publish`（win-x64 / self-contained / 単一ファイル）で `PoiConvert.exe` を作成
   5. タグ `v{version}` と GitHub Release を作成し、次を添付
      - `PoiConvert.exe`（本体のみ）
      - `PoiConvert-with-ffmpeg-v{version}.zip`（exe + ffmpeg.exe + ffprobe.exe + FFMPEG-LICENSE.txt + FFMPEG-SOURCE.txt + README.md）
      - `ffmpeg-source-<commit>.tar.gz`（同梱 FFmpeg のソース）
      - `SHA256SUMS.txt`

FFmpeg 本体はリポジトリにはコミットしていません（`.gitignore` で除外。リリース時にワークフロー内でダウンロードします）。
