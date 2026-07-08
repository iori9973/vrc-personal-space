# VRChat Personal Space Keeper

他プレーヤーが一定距離より近づいたとき、**自分だけ**が相手から離れる方向へ自動で移動し、
パーソナルスペースを保つツール。ワールドに依存せず、アバターに仕込んで使う。

## 仕組み

VRChat の仕様上、アバターからは「他人の検知」はできても「自分の移動」はできない。
そのため 2 パーツ構成になっている。

```
[アバター] 放射状に N 個(既定8, 最大16)の Contact Receiver(Proximity) が
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

前提: VRChat Avatars SDK (VRCSDK3-Avatars) が入ったプロジェクト。

**VCC で導入する：**

1. VCC の **Settings → Packages → Add Repository** に次の URL を追加：
   `https://iori9973.github.io/vrc-personal-space/index.json`
   （または [リスティングページ](https://iori9973.github.io/vrc-personal-space/) の「VCC に追加」ボタン）
2. アバタープロジェクトの **Manage Packages** で **Personal Space** を Install。

導入後、付属の OSC アプリは `Packages/com.vrc-personal-space/osc/` に入っています。

**セットアップ：**

1. メニュー **Tools > Personal Space > Setup Window** を開く。
2. `Avatar` にアバター（VRCAvatarDescriptor）をドラッグ。
3. 方向センサー数（既定8）や反応半径を調整し、**セットアップ / 更新** を押す。
   - アバター直下に `PersonalSpaceSensors`（N方向センサー）が作られる。
   - Expression Parameters に `PS_0 … PS_{N-1}` (Float) が追加される。
4. いつも通りアバターをアップロードする。

| 項目 | 既定 | 説明 |
|------|------|------|
| 方向センサー数 | 8 | 多いほど逃げる方向の分解能が上がる（4〜16）。Local Only なのでランク非計上 |
| 反応半径 (m) | 1.0 | この距離より近づかれたら離れ始める |
| センサーオフセット (m) | 0.4 | 各センサーの中心オフセット。大きいほど方向の分解能が上がる |
| センサー高さ (m) | 0.9 | 概ね胴体の高さ |
| パラメータを同期する | OFF | 通常 OFF で OSC 出力される。届かない場合のみ ON にして再セットアップ |

> センサー数を変えたら、常駐アプリも同じ数で起動すること（`--sensors N`）。Quest でも
> アップロードする場合のみ Contacts 合計16個のハード制限に注意（Quest 単体では本ツールは動かない）。

#### リモート位置ズラし（遅延補償・任意）

OSC 移動には往復遅延があるため、そのままだと他プレーヤー視点では避けるのが一瞬遅れて見える。
これを補うのがこの機能。**Tools > Personal Space > Remote Offset (遅延補償)** を開き、Avatar を
セットして **生成 / 更新** を押すと：

- `PS_OffX / PS_OffZ`（Float・同期）と `PS_Enabled`（Bool・同期）が追加される。
- `Assets/PersonalSpace/Generated/PS_RemoteOffset.controller` が生成される。
- アプリが逃走方向を `PS_OffX/OffZ` として同期送信し、**リモート（他人視点＝IsLocal:false）でのみ**
  Armature を最大リード距離だけ先行オフセット。自分視点では動かさない（実際に /input で動くため）。

生成した Controller は次のどちらかで FX に反映する：
- **Modular Avatar**（推奨・非破壊）：後述の「Modular Avatar で配布」を参照。
- 単体テスト：一時的にアバターの FX Playable Layer に設定して確認。

> 同期消費は `PS_OffX/OffZ`(各8bit) + `PS_Enabled`(1bit) = 約17bit。生センサー(PS_0…)は
> ローカルのままで同期しない。

#### Expression Menu（ON/OFF）

同じ Remote Offset ウィンドウの **Expression Menu を生成 (ON/OFF)** を押すと、`PS_Menu` と
ルートメニューへの「Personal Space」サブメニュー（`PS_Enabled` トグル）が作られる。
メニューで OFF にすると、リモートオフセットが止まるだけでなく、**アプリも移動送信を停止**する
（アプリが同期パラメータ `PS_Enabled` を購読しているため）。

#### Modular Avatar で配布（任意・非破壊）

自分用途なら上記の Editor 適用だけで動くが、非破壊・配布向けにするなら [Modular Avatar](https://modular-avatar.nadena.dev/ja) で：

1. `PersonalSpaceSensors` と生成物を含む GameObject をアバター直下に置き、Prefab 化。
2. **MA Merge Animator** を追加し、`PS_RemoteOffset.controller` を FX にマージ。
3. **MA Parameters** で `PS_0…`（Local）、`PS_OffX/OffZ`・`PS_Enabled`（Synced）を宣言。
4. **MA Menu Installer** で `PS_Menu` を差し込む。

これで Expression Parameters / Menu / FX を直接編集せずに統合できる。

### 2. 常駐アプリ（PC）

前提: Python 3.9+。

```bash
cd osc
pip install -r requirements.txt
python personal_space.py --sensors 8   # Editor のセンサー数と合わせる
```

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
| `--sensors` | 8 | センサー数（Editor と一致させる） |
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
