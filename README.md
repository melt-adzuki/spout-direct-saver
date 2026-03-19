# Spout Direct Saver

Spout2 で受信したフレームを動画ファイルへ保存する WPF アプリです。

## 仕様

- 入力: Spout2 receiver
- 受信色: RGBA 8bit
- 録画: 最大 120fps でポーリング
- 可変fps: 前フレームと同一内容なら新規フレームを書かず、表示時間だけ延長
- 停止後: シークバー付きでプレビュー再生
- ボタン: `録画開始` / `録画停止` / `再録画`

## 保存形式

アプリでは 2 つの保存形式を選べます。

- `PNG / MOV`
  - FFmpeg の `png` エンコーダーで `rgba` を直接扱えるため、RGBA 8bit をそのまま保持しやすいです
  - alpha 付き・ロスレス
  - そのぶんファイルサイズは大きめです
- `FFV1 / MKV`
  - alpha 付きロスレス
  - 保存時のみ `bgra` へ並び替えて FFV1 に格納します
  - `PNG / MOV` より容量効率を狙いやすいです

既定は `PNG / MOV` です。

## 実装メモ

- 録画中は変化したフレームだけを一時 PNG として保存します
- 停止時に `ffconcat` マニフェストを生成し、FFmpeg で可変fps動画へ確定します
- プレビュー再生は `LibVLCSharp` を使っています

## 前提条件

- Windows
- `.NET 9 SDK`
- `ffmpeg.exe` が PATH にあること

この環境では `ffmpeg 8.0.1` で動作確認しています。

## 起動

```powershell
dotnet run --project .\SpoutDirectSaver.App\SpoutDirectSaver.App.csproj
```
