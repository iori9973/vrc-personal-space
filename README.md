# VRChat Personal Space Keeper

他プレーヤーが一定距離より近づいたとき、**自分だけ**が相手から離れる方向へ自動で移動し、
パーソナルスペースを保つツール。ワールドに依存せず、アバターに仕込んで使う。

## 仕組み

VRChat の仕様上、アバターからは「他人の検知」はできても「自分の移動」はできない。
そのため 2 パーツ構成になっている。

```
[アバター] 放射状に N 個(既定16)の Contact Receiver(Proximity) が
          他人の Head/Torso/Hand/Foot/Finger を検知
   │  OSC out : /avatar/parameters/PS_0 … PS_{N-1}（ローカル）
   ▼
[PC常駐アプリ] 全センサーから逃走ベクトルを合成
   │  OSC in : /input/Vertical, /input/Horizontal で自分を移動（離れたら 0）
   │  OSC in : /avatar/parameters/PS_OffX, PS_OffZ（同期・遅延補償）
   ▼
[VRChat] 自分が相手から離れ、他人視点でも先回りしてパーソナルスペースを保つ
```

- 接続は **OSCQuery で自動**（ポート指定不要）。未対応環境は固定ポート(受信9001/送信9000)。
- センサー i は角度 θ=360×i/N（i=0 が正面）。数を増やすほど逃げる方向の分解能が上がる。
- センサーは **Local Only** なので PC のパフォーマンスランク（Contacts）には計上されない。
- **リモート位置ズラし**：逃走方向を同期し、他人視点で Armature を先行オフセットして OSC 遅延を相殺。

## セットアップ

### 1. アバター側（Unity / VCC）

前提: VRChat Avatars SDK (VRCSDK3-Avatars) ＋ **Modular Avatar (>=1.10.0)** が入ったプロジェクト。
（VCC で本パッケージを入れると Modular Avatar も依存として一緒に入ります）

**VCC で導入する：**

1. VCC の **Settings → Packages → Add Repository** に次の URL を追加：
   `https://iori9973.github.io/vrc-personal-space/index.json`
   （または [リスティングページ](https://iori9973.github.io/vrc-personal-space/) の「VCC に追加」ボタン）
2. アバタープロジェクトの **Manage Packages** で **Personal Space** を Install。

導入後、付属の OSC アプリは `Packages/com.vrc-personal-space/osc/` に入っています。ただし
`Packages/` はプロジェクト単位なので、**Setup Window の「OSCアプリをPCにインストール」ボタンで
PC 共通の固定フォルダ（`%LOCALAPPDATA%\VRCPersonalSpace\app\`）へコピーして、そこから使う**のが
おすすめです（プロジェクトが複数あってもアプリの実体は1箇所にまとまり、自動起動のパスも壊れません）。

**セットアップ：**

1. メニュー **Tools > Personal Space > Setup Window** を開く。
2. `Avatar` にアバター（VRCAvatarDescriptor）をドラッグ。
3. 方向センサー数（既定8）や反応半径を調整し、**セットアップ / 更新** を押す。
   - アバター直下に `PersonalSpace` コンテナが作られ、その中に `Sensors`（N方向センサー）などがまとまる。
   - Expression Parameters に `PS_0 … PS_{N-1}` (Float) が追加される。
4. いつも通りアバターをアップロードする。

| 項目 | 既定 | 説明 |
|------|------|------|
| 方向センサー数 | 16 | 多いほど逃げる方向の分解能が上がる（4〜16）。Local Only なのでランク非計上 |
| 最大反応半径 (m) | 1.5 | メニュー「反応範囲」の上限。実行時はこの範囲内で縮められる |
| センサーオフセット (m) | 0.4 | 各センサーの中心オフセット。大きいほど方向の分解能が上がる |
| センサー高さ (m) | 0.9 | 概ね胴体の高さ |
| パラメータを同期する | OFF | 通常 OFF で OSC 出力される。届かない場合のみ ON にして再セットアップ |

> センサー数を変えたら、常駐アプリも同じ数で起動すること（`--sensors N`）。Quest でも
> アップロードする場合のみ Contacts 合計16個のハード制限に注意（Quest 単体では本ツールは動かない）。

#### リモート位置ズラし（遅延補償・任意）

OSC 移動には往復遅延があるため、そのままだと他プレーヤー視点では避けるのが一瞬遅れて見える。
これを補うのがこの機能。**Tools > Personal Space > Remote Offset (遅延補償)** を開き、Avatar を
セットして **生成 / 更新** を押すと：

- `PS_OffX / PS_OffZ`（Float・同期）と `PS_Enabled`（Bool・同期）が **MA 経由で**追加される。
- `Assets/PersonalSpace/Generated/<アバター名>/PS_RemoteOffset.controller` が生成され、**MA Merge Animator で
  自動的に FX にマージ**される（手動設定は不要）。
- アプリが逃走方向を `PS_OffX/OffZ` として同期送信し、**リモート（他人視点＝IsLocal:false）でのみ**
  Armature を最大リード距離だけ先行オフセット。自分視点では動かさない（実際に /input で動くため）。

> パラメータ・メニュー・アニメはアバター直下の `PS_ModularAvatar` に MA コンポーネントとして
> 注入される。Expression 資産を直接編集しないので、**FaceEmo など NDMF 系ツールと共存**できる。
> 同期消費は `PS_OffX/OffZ`(各8bit) + `PS_Enabled`(1bit) = 約17bit。生センサー(PS_0…)はローカル。

#### Expression Menu（ON/OFF ＋ 実行時調整）

セットアップ時に `PS_Menu` が作られ、**MA Menu Installer で「Personal Space」サブメニュー**として
非破壊追加される。押し出し・透明化の両方が有効なときは、その下が **「押し出し」「透明化」の2ページ**に
分かれる（片方だけなら直接並ぶ）。内容：

**押し出し**
| メニュー項目 | パラメータ | 種類 | 効果 |
|---|---|---|---|
| 有効 | `PS_Enabled` | Toggle(同期) | OFF でリモートオフセット停止＋アプリの移動送信も停止 |
| 反応範囲 | `PS_Range` | Radial(非同期) | 最大反応半径の範囲内で実効範囲を縮める（近づかれてから逃げ始める距離） |
| 押し出しの強さ | `PS_Gain` | Radial(非同期) | 逃げる速さ |
| 遅延補償の量 | `PS_Lead` | Radial(非同期) | リモート先行量 |
| 範囲表示 | `PS_ShowRange` | Toggle(非同期) | 足元に反応範囲のリング（**イエロー**）を表示（自分のみ）。範囲調整時の目安 |

**透明化**
| メニュー項目 | パラメータ | 種類 | 効果 |
|---|---|---|---|
| 透明化 | `PS_CloakMode` | Toggle(同期) | ONにすると、近づかれた時に**他人視点で自分が消える**（自分には見える）。押し出しの代替 |
| 透明化の距離 | `PS_CloakRange` | Radial(非同期) | 「透明化する最大距離」の範囲内で、消え始める距離を絞る（近接センサーの半径を動的に変える） |
| 範囲表示 | `PS_ShowCloak` | Toggle(非同期) | 足元に透明化の距離リング（**シアン**）を表示（自分のみ） |
| 自分からも消える | `PS_CloakSelf` | Toggle(非同期) | ONにすると**自分視点でも**消える（通常は自分には見えたまま）。既定 OFF |

> 透明化は「透明化する最大距離(m)」以内（メニューでさらに絞れる）に誰かが入ると、`PS_Near`(同期)経由でリモートのメッシュを非表示にする。
> VRChat の仕様上、**特定の人だけには消せず全員から消える**（視点別可視化ができないため）。OSCアプリ不要で動く。

反応範囲・強さ・遅延補償量は**アプリがメニュー値を購読して反映**する（受信していない間は
起動時の CLI 値を使う）。範囲は物理半径を最大にしたまま、近接度のしきい値で縮める方式なので
アニメーション生成は不要。ON/OFF の `PS_Enabled` のみ同期し、3つの Radial はローカル効果なので
非同期（同期ビット消費なし）。

#### Modular Avatar 統合（自動）

[Modular Avatar](https://modular-avatar.nadena.dev/ja) が必須。セットアップ時に、アバター直下の
`PS_ModularAvatar` に以下が自動生成される（手動設定は不要）：

- **MA Parameters** … `PS_0…`（Local）、`PS_OffX/OffZ`・`PS_Enabled`（Synced）、`PS_Range/Gain/Lead`（Local）
- **MA Merge Animator** … `PS_RemoteOffset.controller` を FX にマージ
- **MA Menu Installer** … 「Personal Space」メニューを追加

Expression のパラメータ／メニューを直接書き換えないため、**FaceEmo など NDMF 系ツールと共存**できる。

#### 近づける相手を選びたい場合（フレンド/特定人物）

Contacts も OSC も「**誰が近づいたか**」の身元情報を持たないため、ツール側でフレンド判定や
名前指定はできません。代わりに次の方法が使えます：

- **特定の人だけ通したい**: 相手のネームプレートで **Avatar Interactions を個別 OFF** にすると、
  その人の Contacts が届かなくなり反応しなくなる（VRChat 標準機能・相手の協力不要）。
- **フレンドは通す/他は離れる**: VRChat セーフティの**信頼ランクごとの Avatar Interactions** 設定で近い挙動が可能。
- **通行証機能（本ツール内蔵）**: 下記参照。特定アイテムを持っている人だけ通す（所持ベース）。

#### 通行証機能（特定アイテム所持者は押し出さない）

「合言葉」を決めて、その合言葉のタグを仕込んだ**通行証アイテム**を通したい相手に渡す方式です。
身元判定ではなく**所持**ベース（合言葉を渡した相手は全員通れる）で、仲良しグループを迎える用途向け。

**セットアップ（自分側）:**
1. Setup Window の「押し出し機能」の下で「**通行証を通す**」を ON にする。
2. 「**合言葉**」を入力（相手に配る通行証アイテムと一致させる文字列。例 `friends`）。
3. 「**通行証アイテム(Prefab)を書き出す**」を押す → `Assets/PersonalSpace/Generated/PassItems/` に Prefab が出力される。
4. 「セットアップ / 更新」を押す（合言葉タグを検知する通行証センサーが放射状に生成される）。

**配布（相手側）:**
- 書き出した通行証アイテム Prefab を通したい相手に渡し、相手のアバターに入れてもらう（着けるだけ）。
- **アバター直下でも、アクセサリの中に入れてもOK**（検知球は置いた位置に出るので配置を選ばない）。体の中心近く（胸〜腰）だと確実。アクセサリに同梱して配れば相手はそれを着けるだけで済む。
- アップロードすれば完了。合言葉が一致していれば、その相手が近づいても押し出されなくなる。

**使い方（自分側・VRChat内）:**
- メニュー「Personal Space → 押し出し → **通行証は通す**」を ON にすると、通行証所持者を検知した**方向だけ**押し出しを止める。
  正面のフレンドは通しつつ、横から来た他人はちゃんと押し出す（方向別キャンセル）。OFF なら全員から離れる。

**注意:**
- 合言葉が漏れると他人も通れる（信頼できる人にだけ配る前提）。合言葉を変えたら自分も同じ合言葉でセットアップし直す。
- 通行証所持者と他人が**同じ方向で重なって近い**場合は、その方向は通ってしまう（用途上許容）。
- 相手の協力（通行証アイテムを着ける＋アップロード）が必要。

### 2. 常駐アプリ（PC）

前提: Python 3.9+。

**インストール（推奨）:** Unity の **Setup Window → 「OSCアプリをPCにインストール」** を押すと、
PC 共通の固定フォルダ `%LOCALAPPDATA%\VRCPersonalSpace\app\` に osc 一式がコピーされます。以降は
そのフォルダで使います（プロジェクトを消してもアプリと自動起動設定は残る）。VCC で更新したら同じ
ボタンで上書き更新してください。

```bash
cd %LOCALAPPDATA%\VRCPersonalSpace\app
pip install -r requirements.txt
```

**GUI で使う（推奨・起動状態がひと目でわかる）:**

固定フォルダの `PersonalSpace.bat` をダブルクリック（または `python personal_space_gui.py`）。
ウィンドウに **状態ランプ**（停止中／接続中／待機中／動作中）・**ログ**・現在の逃走ベクトルと
移動量が表示され、**開始／停止**ボタンとセンサー数などの設定を操作できます。Tkinter は
Python 標準添付のため追加インストールは不要です。

**CLI で使う（従来どおり）:**

```bash
python personal_space.py --sensors 16   # Editor のセンサー数と合わせる
```

**VRChat の起動に合わせて自動起動する:**

GUI の「**Windows起動時に自動で常駐**」を ON にすると、常駐ウォッチャ（`personal_space_watch.py`）が
Windows のスタートアップに登録されます。以降は **VRChat を起動すると、アプリが立ち上がっていなければ
自動で起動して OSC を開始**します（起動済みなら二重に立ち上げません）。手動で番人だけ動かすなら
`pythonw personal_space_watch.py`。解除はトグルを OFF に（動作中の常駐は次回 Windows 再起動で止まります）。

**OSCQuery で自動接続します**（ポート指定不要）。アプリを起動しておけば、VRChat 側で
アバターを切り替えるだけで自動的に有効化されます。VRChat を発見できない環境や
tinyoscquery が入っていない場合は、固定ポート（受信9001 / 送信9000）へ自動フォールバック
します（`--no-oscquery` で最初から固定モード）。

VRChat 側で OSC を有効化する（Action Menu > Options > OSC > Enabled）。

停止は `Ctrl+C`（移動入力を 0 に戻して終了する）。

> `tinyoscquery` は PyPI 未公開のため、`requirements.txt` は git から取得します。
> git が無い環境では `--no-oscquery` で固定ポート運用も可能です。

## 調整（アプリ引数）

```bash
python personal_space.py --gain 1.5 --deadzone 0.05 --smooth 0.4
```

| 引数 | 既定 | 説明 |
|------|------|------|
| `--sensors` | 16 | センサー数（Editor と一致させる） |
| `--no-oscquery` | off | OSCQuery を使わず固定ポートで動かす |
| `--gain` | 1.5 | 押し出しの強さ |
| `--deadzone` | 0.05 | これ以下の入力は 0（微振動防止） |
| `--smooth` | 0.4 | 0=即時 〜 0.99=最大平滑 |
| `--invert-h` | off | 左右が逆に動くとき付ける |
| `--invert-v` | off | 前後が逆に動くとき付ける |
| `--lead` | 1.0 | リモート先行量の倍率（0で遅延補償を無効） |
| `--no-remote-offset` | off | リモート位置ズラしの同期送信を止める |
| `--debug` | off | 受信値と出力をリアルタイム表示 |

`--debug` を付けて近づいてもらい、`near` の値が上がるか、`escape`/`V`/`H` の符号が
「離れる向き」になっているかを確認する。逆なら `--invert-h` / `--invert-v` を付ける。

## 制約・注意

- **Avatar Interactions（アバターのインタラクション）が有効な状態でしか使えない。** 相手のセーフティ
  設定で自分の Avatar Interactions（Contacts）が許可されていないと検知できない（＝ミュート相手や
  セーフティ制限下では機能しないことがある）。
- **PC 上でこの常駐アプリを動かす必要がある**（アバター単体では移動できないため）。PCVR / Desktop 専用。
  Quest 単体では OSC が動かないため機能しない。
- OSCQuery 自動接続には VRChat 2023.3.1 以降が必要（それ未満や未対応環境は `--no-oscquery` で固定ポート）。
- 相手が Collider を無効化した特殊アバターだと検知しづらい。密着距離（〜1m）なら
  手・頭・胴が入るので概ね機能する。
- VR では体と視線の向きがズレるため、逃げる方向が多少ずれる（デスクトップはほぼ正確）。
- 逃げている間は移動入力が上書きされるため、自分の操作は効きにくくなる。
  離れれば入力は 0 に戻り、通常操作に戻る。
- ワールドや環境（重力・移動制限など）によって挙動が変わることがある。

## 免責

VRChat の仕様変更や SDK 更新で動作が変わる可能性があります。OSC 環境やネットワーク状況に
依存するため不具合が出ることがあります。自己責任でご利用ください。

## Unity 側の動作確認について

`Assets/PersonalSpace/Editor/` の生成コード（特にリモートオフセットの AnimatorController /
AnimationClip 生成）は、Unity エディタ上での実動作確認が必要です。生成後は Play や
アップロード前に、FX への統合とパラメータ／メニューの反映を確認してください。
