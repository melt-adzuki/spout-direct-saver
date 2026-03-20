# Architecture And Performance

## 現在の主構成

### 受信

- Spout sender の状態を見て、shared texture 優先で受信します。
- sender が GL/DX shared texture を出している場合は `CPUmode=false` と `BufferMode=true` を優先します。
- shared texture 読み出しは D3D11 staging texture を使った readback です。
- 録画中は、可能な限り録画用 `PixelBufferLease` に直接受けて余分なコピーを避けます。

### 録画

- 可変フレームレートです。
- 前フレームと同一内容なら新規フレームを追加せず、表示時間だけ伸ばします。
- 現在の推奨パスは:
  - RGB 本体: `HEVC NVENC`
  - alpha: grayscale `FFV1` sidecar

### UI

- 停止後プレビューは LibVLCSharp を使用します。
- 録画中はライブプレビューを止め、WPF の `WritePixels` 負荷で録画を邪魔しないようにしています。

## なぜこの設計になったか

4K / 60fps / RGBA8 は 1 フレームあたり約 `33 MB` です。  
単純計算で毎秒ほぼ `2 GB` 流れるため、raw に近い一時スプールを前提にすると SATA SSD ではかなり厳しくなります。

初期の実装では、以下が主なボトルネックになりました。

- raw もしくは軽圧縮フレームの temp 書き出し
- 録画中の WPF ライブプレビュー更新
- 受信後の余分なメモリコピー
- alpha を毎フレーム full scan して sidecar 化する処理
- multi-stream alpha 動画の再生互換性

そのため、現在は「キャプチャ中に巨大な raw spool を書き続ける」よりも、

- RGB は GPU エンコードに逃がす
- alpha は別経路に分離する
- 必要なところだけ lossless を維持する

という方針を取っています。

## 効いた改善

### 1. 受信経路の最適化

- shared texture sender に対して受信設定を自動最適化
- D3D11 readback の staging ring を増やして CPU/GPU の待ちを減らす
- sender 情報の毎フレーム再取得を避ける fast path を追加

### 2. 録画中コピーの削減

- 録画時フレームを `PixelBufferLease` に直接受ける
- 録画中はプレビュー更新を止める
- 参照カウント付きバッファで RGB writer と alpha worker に分配する

### 3. alpha 処理の分離

- alpha 抽出を capture スレッドから外した
- alpha sidecar は main RGB と分離した
- alpha が前フレームと同一なら、sidecar 側で前フレームを再利用する

## 現在の性能解釈

E2E では 3 種類の見方があります。

### `receive_only_*`

receiver がどれだけ新規フレームを拾えているかの観測値です。  
受信 ceiling の把握に使います。

### `record_*`

録画パイプラインが何フレーム受け取れたかの観測値です。  
ただし sender 種類や重複判定の影響で、最終見た目と完全一致しない場合があります。

### `content_analysis_*`

出力ファイルを decode して、実際の見た目ベースで更新 cadence を推定する指標です。  
最終品質に一番近いですが、まだ指標ごとに性格が違います。

- `exact_signature`
  - 厳密寄り
  - 細かい変化を見逃して高めに出ることがある
- `motion_stutter`
  - exact と motion threshold を組み合わせた補助指標
- `experimental_perceptual`
  - 人間の見た目に寄せたい実験指標
  - まだ安定版ではない

## 設計上の判断

### sidecar を採用した理由

1 本の MKV に RGB と alpha を同居させるより、

- RGB 本体の再生互換性を保ちやすい
- preview が壊れにくい
- alpha だけ別戦略で保持できる

という利点が大きかったためです。

### lossless 単一ファイルを既定にしていない理由

`PNG / MOV` や `FFV1 / MKV` は品質面では強いですが、4K/60 の継続録画では encode と temp 帯域の負荷が重くなりやすいです。  
そのため既定は `HEVC NVENC / MKV + FFV1 alpha sidecar` にしています。

## 今後さらに詰めるなら

- shared texture から main RGB をさらに GPU 直結に寄せる
- content-based 指標を見た目により一致する形へ改良する
- alpha 側の再利用判定を、完全一致以外も扱えるようにする
- player 互換性を保ちつつ sidecar の扱いをもう少し自動化する
