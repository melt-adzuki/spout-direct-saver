# Operational Notes

## 推奨運用

### 保存形式

通常は `HEVC NVENC / MKV + FFV1 alpha sidecar` を使うのが無難です。

- RGB 本体の再生互換性が高い
- alpha を lossless で保持できる
- raw spool 依存より temp 帯域の圧力がかなり低い

### キャッシュ先

一時キャッシュは高速なドライブを推奨します。  
特に 4K / 60fps では、SATA SSD と高速 NVMe で挙動差が出やすいです。

上書きしたい場合:

```powershell
$env:SPOUT_DIRECT_SAVER_CACHE_ROOT = 'C:\FastCache'
```

## sender について

### Live Game sender

- synthetic sender より複雑なフレーム変化を含むことがあります。
- ゲームがフォアグラウンドかどうかで挙動が変わるケースがありました。
- sender 側の share mode や pixel format の違いで受信負荷が変わります。

### synthetic sender

`SpoutDirectSaver.TestSender` には次の scene があります。

- `Simple`
- `Complex`
- `AlphaStress`

`AlphaStress` は alpha が実際に変化するため、alpha sidecar の確認に向いています。

## E2E の見方

### まず見る値

- `receive_only_unique_avg_fps`
- `receive_only_unique_min_1s_fps`
- `record_unique_avg_fps`
- `record_unique_min_1s_fps`
- `content_analysis_exact_signature_*`
- `content_analysis_motion_stutter_*`

### まだ実験的な値

- `content_analysis_experimental_perceptual_*`

この値は人間の体感に寄せる狙いがありますが、現時点では good / bad を完全には分離できません。  
判断材料には使えますが、単独の正解指標ではありません。

## よくある症状

### sender を待っていますのまま進まない

- sender 名が変わっていないか確認します。
- sender がすでに映像を出しているか確認します。
- shared texture sender か image sender かで受信条件が変わるため、sender の種類も確認します。

### 録画中だけ重い

- ライブプレビューは録画中に止まる前提です。
- temp キャッシュ先が遅いドライブだと悪化しやすいです。
- alpha が大きく変化し続けるシーンは、alpha sidecar の負荷も上がります。

### 出力ファイルの見た目と `record_*` が合わない

- `record_*` は capture 経路の観測値です。
- 最終的な見た目は `content_analysis_*` と実動画確認で判断してください。

## 検証コマンド

### app 起動

```powershell
dotnet run --project .\SpoutDirectSaver.App\SpoutDirectSaver.App.csproj
```

### E2E

```powershell
dotnet run --project .\SpoutDirectSaver.E2E\SpoutDirectSaver.E2E.csproj -- --launch-test-sender --test-sender-scene AlphaStress --seconds 5
```

### encoder ベンチ

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\BenchmarkEncoders.ps1
```

## 実装を触る人向けメモ

- preview 最適化と録画最適化は分けて考えること
- Live Game と synthetic sender は両方で確認すること
- E2E の数値だけでなく、必ず最終ファイルも目視すること
- alpha を含む構成変更では main file と `.alpha.mkv` の両方を確認すること
