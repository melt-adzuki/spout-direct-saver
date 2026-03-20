# Spout Direct Saver

Spout2 で受信した映像を、そのまま動画ファイルとして保存する Windows 向け WPF アプリです。  
ライブ入力の確認、録画開始/停止、停止後のシークバー付きプレビュー再生、再録画までを 1 つのアプリで行えます。

## 特長

- Spout2 receiver から RGBA 8bit フレームを受信
- 入力解像度をそのまま維持
- 可変フレームレート記録
  - 前フレームと同一内容なら新規フレームを増やさず、表示時間だけ延長
  - 上限 120fps
- 録画停止後にアプリ内プレビュー再生
- `録画開始` / `録画停止` / `再録画` のシンプルな UI
- E2E ハーネスと synthetic Spout sender を同梱

## いまの設計で強いところ

4K / 60fps / RGBA8 をそのまま raw で一時保存すると、理論上ほぼ `2 GB/s` の書き込み帯域が必要になります。  
このアプリはそこを避けるため、現在は次の構成を主軸にしています。

- 受信
  - shared texture を優先
  - sender に応じて `CPU mode` / `BufferMode` を自動切り替え
- 録画
  - RGB 本体は `HEVC NVENC`
  - alpha は lossless な `FFV1` sidecar
  - 変化していない alpha は sidecar 側で前フレーム再利用
- プレビュー
  - 録画中はライブプレビュー更新を止めて録画優先

この構成により、単純な raw スプール依存よりかなり実用寄りの性能まで持っていけます。

## 保存形式

### `HEVC NVENC / MKV + FFV1 alpha sidecar`

既定の推奨形式です。

- RGB 本体: `HEVC NVENC` の `.mkv`
- alpha: grayscale `FFV1` の `.alpha.mkv`
- 再生互換性、容量、録画時の temp 帯域のバランスを優先
- NVENC が使える NVIDIA GPU 前提

### `PNG / MOV (lossless RGBA 8bit, alpha保持)`

- 単一ファイル
- `rgba` をそのまま保持しやすい
- ファイルサイズは大きめ

### `FFV1 / MKV (lossless, alpha保持, 容量効率重視)`

- 単一ファイル
- lossless
- `PNG / MOV` より容量効率を狙いやすい
- encode 負荷はやや高め

## 必要環境

- Windows
- `.NET 9 SDK`
- `ffmpeg.exe` が `PATH` にあること
- `HEVC NVENC / MKV + FFV1 alpha sidecar` を使う場合は NVENC 対応 NVIDIA GPU

この環境では `ffmpeg 8.x` 系で検証しています。

## 起動

```powershell
dotnet run --project .\SpoutDirectSaver.App\SpoutDirectSaver.App.csproj
```

## 使い方

1. Spout sender を起動します。
2. アプリを起動します。
3. sender が見つかるとライブ入力が受信状態になります。
4. `録画開始` を押します。
5. `録画停止` を押すと、最終ファイルの確定処理が走ります。
6. 停止後はアプリ内でプレビュー再生できます。
7. `再録画` で次のテイクに入れます。

## E2E テスト

synthetic sender 付きの E2E ハーネスを同梱しています。

```powershell
dotnet run --project .\SpoutDirectSaver.E2E\SpoutDirectSaver.E2E.csproj -- --launch-test-sender --test-sender-scene AlphaStress --seconds 5
```

主な出力:

- `receive_only_*`
- `record_*`
- `content_analysis_exact_signature_*`
- `content_analysis_motion_stutter_*`
- `content_analysis_experimental_perceptual_*`

`content_analysis_experimental_perceptual_*` はまだ実験的です。  
現時点では `exact_signature` と `motion_stutter` を主に見つつ、最終判断は実動画の目視確認と併用する前提です。

## よく使う環境変数

- `SPOUT_DIRECT_SAVER_CACHE_ROOT`
  - 一時キャッシュの保存先を上書きします。
- `SPOUT_DIRECT_SAVER_SPOOL_COMPRESSION`
  - スプール圧縮レベルを上書きします。
- `SPOUT_DIRECT_SAVER_DISABLE_HYBRID_RGB`
  - hybrid RGB intermediate を無効化します。
- `SPOUT_DIRECT_SAVER_DISABLE_HYBRID_ALPHA`
  - hybrid alpha spool を無効化します。
- `SPOUT_DIRECT_SAVER_DISABLE_MAIN_WRITER`
  - realtime hybrid writer の RGB 本体を書かない検証用です。
- `SPOUT_DIRECT_SAVER_DISABLE_ALPHA_WRITER`
  - realtime hybrid writer の alpha を書かない検証用です。

## 既知の重要事項

- 4K / 60fps / RGBA8 を raw スプール中心で処理すると、SATA SSD では帯域が厳しくなりやすいです。
- Spout sender の share mode や pixel format によって、最適な受信経路は変わります。
- 汎用プレイヤーでは multi-stream alpha 動画の再生互換性が弱いため、現在は alpha sidecar 分離を採用しています。
- synthetic sender で高 fps が出ても、Live Game sender では別のボトルネックが見えることがあります。

## docs

- [ArchitectureAndPerformance.md](./docs/ArchitectureAndPerformance.md)
- [OperationalNotes.md](./docs/OperationalNotes.md)
- [RealtimeEncodingBenchmarks.md](./docs/RealtimeEncodingBenchmarks.md)
