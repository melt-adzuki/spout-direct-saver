# Realtime Encoding Benchmarks

このファイルは、alpha 付き Spout 録画を 4K / 60fps で成立させるために行った codec / packaging 調査の記録です。  
現在の既定構成を選ぶ理由の背景資料として残しています。

## 結論

最終的に一番バランスが良かったのは次の系統でした。

- RGB 本体: `HEVC NVENC`
- alpha: grayscale `FFV1`
- packaging: `RGB main file + alpha sidecar`

理由:

- single-file alpha codec は realtime 性が足りなかった
- dual-video-stream MKV は再生互換性と preview が不安定だった
- raw spool 中心設計は 4K / 60fps では temp 帯域が重すぎた

## なぜ raw spool は厳しいか

RGBA 8-bit / 3840x2160 は約 `33 MB/frame` です。

- `3840 x 2160 x 4 = 33,177,600 bytes`
- `60 fps` で約 `1.99 GB/s`

ここに圧縮、メモリコピー、メタデータ更新、preview、sender/receiver 処理が加わるため、SATA SSD 前提ではかなり苦しい設計になります。

## 比較した候補

### 単一ファイルで alpha を持つ候補

- `PNG / MOV`
- `FFV1 / MKV`
- `ProRes 4444 / MOV`
- `Hap Alpha / MOV`
- `CineForm / MOV`
- `VP9 alpha / WebM`

### 分離型

- RGB main: `hevc_nvenc` または `h264_nvenc`
- alpha: grayscale `ffv1`
- packaging:
  - 1 本の `mkv` に 2 video stream
  - `RGB main + alpha sidecar`

## ベンチ条件

ツール:

- `tools/BenchmarkEncoders.ps1`

入力:

- `VRCSender1` から取得した 3840x2160 RGBA サンプル

評価方法:

1. 通常のコンテナ変換
2. raw BGRA ループ

raw BGRA ループのほうが、実アプリで「すでにメモリ上にあるフレームを encoder へ流す」条件に近いです。

## 主な結果

### 単一ファイル alpha codec

- `PNG / MOV`
  - alpha は保持できる
  - realtime には遠い
- `FFV1 / MKV`
  - alpha は保持できる
  - lossless だが 4K/60 live capture の主軸には重い
- `ProRes 4444 / MOV`
  - 品質は魅力的
  - realtime 性は不足
- `Hap Alpha / MOV`
  - single-file alpha 系の中では比較的速い
  - それでも 4K/60 live capture の主軸には足りない
- `CineForm / MOV`
  - alpha を扱える
  - realtime 性は不十分
- `VP9 alpha / WebM`
  - compact で魅力はある
  - realtime capture 向けではなかった

### 分離型

- `HEVC NVENC + FFV1 alpha`
  - 一番有望だった
- `H264 NVENC + FFV1 alpha`
  - 安全寄りの代替候補

## 実装して分かったこと

ベンチだけでは見えなかった実装上の問題もありました。

- `main + alpha` を 1 本の MKV に multi-stream で入れると、汎用プレイヤーでの見た目が不安定になりやすい
- ffmpeg pipe に raw をそのまま流す realtime 経路は、encoder 以前に搬送コストが重かった
- RGB を GPU encode に逃がし、alpha を sidecar にしたほうが preview と再生互換性が安定した

そのため、現在は benchmark 上の「一番速そうな見た目」ではなく、実際の app / preview / player 互換性まで含めて sidecar 構成を選んでいます。

## 現在の実務上の推奨

### 既定

- `HEVC NVENC / MKV + FFV1 alpha sidecar`

### lossless 単一ファイルが必要な場合

- `PNG / MOV`
- `FFV1 / MKV`

どちらも品質面では優秀ですが、4K / 60fps の継続録画では temp 帯域と encode 負荷に注意が必要です。

## 位置づけ

このドキュメントは「今も試すべき候補一覧」ではなく、「なぜ現在の既定構成に至ったか」を残すための履歴です。  
日常の実装・運用判断は、先に次を参照してください。

- [ArchitectureAndPerformance.md](./ArchitectureAndPerformance.md)
- [OperationalNotes.md](./OperationalNotes.md)
