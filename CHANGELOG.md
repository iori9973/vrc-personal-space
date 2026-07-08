# Changelog

## [0.3.0]

- **Modular Avatar 対応（非破壊注入）**。パラメータ・メニュー・遅延補償アニメを、Expression 資産を直接編集する代わりに MA コンポーネント（MA Parameters / MA Menu Installer / MA Merge Animator）でアバター直下の `PS_ModularAvatar` に注入するように変更。
  - **FaceEmo など NDMF 系ツールと共存**でき、ビルド時に項目が消える問題を解消。
  - リモート位置ズラしの Controller が**自動で FX にマージ**される（手動 Merge Animator 設定が不要に）。
  - メニューは「Personal Space」サブメニューとして追加。
  - Modular Avatar (>=1.10.0) が必須依存に。

## [0.2.0]

- 方向センサーの既定を 16 に変更（最大反応半径の既定も 1.5m に）。
- Expression Menu に実行時調整の Radial を追加：反応範囲 `PS_Range` / 押し出しの強さ `PS_Gain` / 遅延補償の量 `PS_Lead`（いずれも非同期、アプリが購読して反映）。
- 反応範囲は物理半径を最大にしたまま近接度しきい値で縮める方式（アニメ生成不要）。

## [0.1.0]

- 初回リリース。
- N 方向 Contact Receiver による他プレーヤーの接近検知（Local Only, ランク非計上）。
- OSC アプリによる自動回避（逃走ベクトル合成 → `/input` 移動）。
- OSCQuery による自動接続（固定ポートへフォールバック可）。
- リモート位置ズラし（遅延補償）。
- Expression Menu による ON/OFF。
