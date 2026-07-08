#if VRC_SDK_VRCSDK3
using System;
using System.Collections.Generic;
using System.IO;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Contact.Components;

namespace PersonalSpace.Editor
{
    /// <summary>
    /// アバターに「パーソナルスペース センサー」を自動セットアップする Editor 拡張。
    ///
    /// 仕組み:
    ///  - 水平方向に N 個の Contact Receiver(Proximity) を放射状に配置し、他プレーヤーの
    ///    Head/Torso/Hand/Foot/Finger(全ヒューマノイドに自動生成される Sender)を検知する。
    ///  - センサー i は角度 θ=360*i/N (i=0 が正面 +Z) に置き、近さを Expression Parameter
    ///    PS_0 … PS_{N-1} として OSC 出力する。
    ///  - 常駐アプリ(osc/personal_space.py)が全センサーから逃走ベクトルを合成し、
    ///    /input で自分だけを離す。方向分解能を上げたいほど N を増やす。
    ///
    /// センサーは Local Only なので PC のパフォーマンスランク(Contacts)には計上されない。
    /// （Quest 併用時のみ Contacts 合計16個のハード制限に注意）
    /// </summary>
    public class PersonalSpaceSetupWindow : EditorWindow
    {
        private VRCAvatarDescriptor _avatar;
        private int _sensorCount = 16;  // 方向センサーの数 (4〜16 推奨)
        private float _radius = 1.5f;   // 反応半径(m) ＝ メニュー「反応範囲」の最大値
        private float _offset = 0.4f;   // 各センサーの中心オフセット(m)
        private float _height = 0.9f;   // センサーの高さ(m) ＝ 概ね胴体
        private bool _sync = false;     // Expression Parameter を同期するか

        // 機能の選択（それぞれ単体でも有効化できる）
        private bool _includePush = true;     // 押し出し機能（要OSCアプリ・センサー）
        private bool _includeOffset = true;   // 遅延補償（押し出しのサブ）
        private bool _includeCloak = true;    // 透明化機能（アバター単体・OSC不要）
        private bool _includeMenu = true;     // Expression Menu を追加する
        private float _lead = 0.25f;          // 最大リード距離(m)
        private bool _enabledDefault = true;  // メニュー ON/OFF の初期値
        private float _cloakDistance = 0.6f;  // 透明化する接近距離(m)

        private const string SensorsName = "Sensors";                    // コンテナ配下のセンサー親
        private const string LegacySensorsRoot = "PersonalSpaceSensors"; // 旧レイアウト(直下)掃除用
        private const string ParamPrefix = "PS_";

        // 全ヒューマノイドアバターに自動生成される Contact Sender のタグ。
        private static readonly List<string> BodyTags = new List<string>
        {
            "Head", "Torso", "Hand", "Foot", "Finger",
        };

        [MenuItem("Tools/Personal Space/Setup Window")]
        public static void Open()
        {
            GetWindow<PersonalSpaceSetupWindow>("Personal Space");
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("パーソナルスペース センサー", EditorStyles.boldLabel);
            _avatar = (VRCAvatarDescriptor)EditorGUILayout.ObjectField(
                "Avatar", _avatar, typeof(VRCAvatarDescriptor), true);

            _sensorCount = EditorGUILayout.IntSlider("方向センサー数", _sensorCount, 4, 16);
            _radius = EditorGUILayout.FloatField("最大反応半径 (m)", _radius);
            _offset = EditorGUILayout.FloatField("センサーオフセット (m)", _offset);
            _height = EditorGUILayout.FloatField("センサー高さ (m)", _height);
            _sync = EditorGUILayout.Toggle("パラメータを同期する", _sync);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("機能（単体でも有効化可）", EditorStyles.boldLabel);
            _includePush = EditorGUILayout.Toggle("押し出し機能（要OSCアプリ）", _includePush);
            using (new EditorGUI.DisabledScope(!_includePush))
            {
                _includeOffset = EditorGUILayout.Toggle("　遅延補償を含める", _includeOffset);
                using (new EditorGUI.DisabledScope(!_includeOffset))
                    _lead = EditorGUILayout.Slider("　　最大リード距離 (m)", _lead, 0.05f, 0.6f);
            }
            _includeCloak = EditorGUILayout.Toggle("透明化機能（近づかれたら消える）", _includeCloak);
            using (new EditorGUI.DisabledScope(!_includeCloak))
                _cloakDistance = EditorGUILayout.FloatField("　透明化する距離 (m)", _cloakDistance);
            _includeMenu = EditorGUILayout.Toggle("メニューを追加する", _includeMenu);
            using (new EditorGUI.DisabledScope(!_includeMenu))
                _enabledDefault = EditorGUILayout.Toggle("　メニュー既定でON", _enabledDefault);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(_avatar == null))
            {
                if (GUILayout.Button("セットアップ / 更新")) Setup();
                if (GUILayout.Button("削除")) RemoveSetup();
            }

            EditorGUILayout.HelpBox(
                "1ボタンで センサー＋遅延補償＋メニュー を一括生成します（Modular Avatar 経由）。\n" +
                $"常駐アプリは同じセンサー数で起動: python personal_space.py --sensors {_sensorCount}\n" +
                "最大反応半径 = メニュー「反応範囲」の上限。実行時にこの範囲内で縮められます。\n" +
                "センサーは Local Only でランク非計上。FaceEmo 等の NDMF 系ツールと共存できます。",
                MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("OSC アプリ（PC常駐）", EditorStyles.boldLabel);
            if (GUILayout.Button("OSCアプリをPCにインストール / 更新"))
                InstallOscApp();
            EditorGUILayout.HelpBox(
                "押し出し機能を使うには PC で OSC アプリを常駐させます。\n" +
                "このボタンで PC 共通の固定フォルダ（" + OscInstallDir + "）へコピーします。\n" +
                "プロジェクトが何個あっても実体は1箇所にまとまり、そこの PersonalSpace.bat から起動・自動起動できます。\n" +
                "※透明化だけを使う場合は OSC アプリ不要です。",
                MessageType.None);
        }

        // PC 共通のアプリ設置先（プロジェクト非依存）。
        private static string OscInstallDir =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VRCPersonalSpace", "app");

        // パッケージ同梱の osc フォルダを固定フォルダへコピーする。
        private void InstallOscApp()
        {
            string src = Path.GetFullPath("Packages/com.vrc-personal-space/osc");
            if (!Directory.Exists(src))
            {
                EditorUtility.DisplayDialog("Personal Space",
                    "同梱の osc フォルダが見つかりませんでした:\n" + src +
                    "\n\nVCC でパッケージが正しく入っているか確認してください。", "OK");
                return;
            }
            string dst = OscInstallDir;
            try
            {
                CopyDirRecursive(src, dst);
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Personal Space",
                    "インストールに失敗しました:\n" + e.Message, "OK");
                return;
            }
            Debug.Log("[PersonalSpace] OSCアプリをインストールしました: " + dst);
            EditorUtility.RevealInFinder(Path.Combine(dst, "PersonalSpace.bat"));
            EditorUtility.DisplayDialog("Personal Space",
                "OSCアプリをインストールしました:\n" + dst +
                "\n\nこのフォルダの PersonalSpace.bat をダブルクリックで起動できます。" +
                "\n（VCC で更新したら、このボタンを押し直して上書き更新してください）", "OK");
        }

        // .meta / __pycache__ を除いて再帰コピー（上書き）。
        private static void CopyDirRecursive(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string file in Directory.GetFiles(src))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(file, Path.Combine(dst, name), true);
            }
            foreach (string dir in Directory.GetDirectories(src))
            {
                string name = Path.GetFileName(dir);
                if (name == "__pycache__") continue;
                CopyDirRecursive(dir, Path.Combine(dst, name));
            }
        }

        private void Setup()
        {
            // まっさらにしてから、有効な機能だけを生成する。
            // メニューアセットは残す（GenerateMenu が中身を作り直す。削除→同フレーム再生成だと
            // controls が空で保存される AssetDatabase 競合を避けるため）。
            TearDown(full: false);

            if (_includePush)
            {
                CreateSensors();
                AddParameters();
                if (_includeOffset)
                    PersonalSpaceRemote.GenerateOffset(_avatar, _lead, _enabledDefault);
            }
            if (_includeCloak)
                PersonalSpaceRemote.GenerateCloak(_avatar, _cloakDistance);
            if (_includeMenu && (_includePush || _includeCloak))
            {
                PersonalSpaceRemote.GenerateMenu(_avatar, _enabledDefault, _includePush, _includeCloak);
                if (_includePush)
                    PersonalSpaceRemote.GenerateRangeViz(_avatar, _radius);
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[PersonalSpace] セットアップ完了: {_avatar.name} "
                      + (_includePush ? "押し出し " : "") + (_includeCloak ? "透明化 " : ""));
        }

        // 放射状に N 個の Contact Receiver(縦カプセル・Proximity) を生成する
        private void CreateSensors()
        {
            Transform container = PersonalSpaceMA.EnsureContainer(_avatar);
            var rootGo = new GameObject(SensorsName);
            Undo.RegisterCreatedObjectUndo(rootGo, "Create PersonalSpace Sensors");
            rootGo.transform.SetParent(container, false);
            Transform root = rootGo.transform;
            root.localPosition = new Vector3(0f, _height, 0f);

            for (int i = 0; i < _sensorCount; i++)
            {
                // i=0 が正面(+Z)。時計回りに配置。
                float theta = 2f * Mathf.PI * i / _sensorCount;
                var dir = new Vector3(Mathf.Sin(theta), 0f, Mathf.Cos(theta));

                var childGo = new GameObject(ParamPrefix + i);
                Undo.RegisterCreatedObjectUndo(childGo, "Create PersonalSpace sensor");
                childGo.transform.SetParent(root, false);
                childGo.transform.localPosition = dir * _offset;

                var recv = Undo.AddComponent<VRCContactReceiver>(childGo);
                // 縦カプセルで身長差があっても水平距離だけで検知（高さ方向のすり抜け防止）
                recv.shapeType = ContactBase.ShapeType.Capsule;
                recv.radius = _radius;
                recv.height = 3.0f;
                recv.position = Vector3.zero;
                recv.rotation = Quaternion.identity;
                recv.receiverType = ContactReceiver.ReceiverType.Proximity;
                recv.parameter = ParamPrefix + i;
                recv.collisionTags = new List<string>(BodyTags);
                recv.allowSelf = false;
                recv.allowOthers = true;
                recv.localOnly = true;
                EditorUtility.SetDirty(recv);
            }
        }

        // Modular Avatar 経由でセンサーパラメータ PS_0…PS_{N-1} を注入する
        // （FaceEmo など NDMF 系ツールに消されないよう非破壊で）。
        private void AddParameters()
        {
            // 旧方式で直接足していた PS_ パラメータ／メニューを掃除（重複防止）
            PersonalSpaceMA.CleanupLegacyExprParams(_avatar, ParamPrefix);
            PersonalSpaceMA.CleanupLegacyMenu(_avatar);
            // センサー数変更に追従: 既存のセンサー番号パラメータをいったん除去
            PersonalSpaceMA.RemoveParametersMatching(_avatar, IsSensorParamName);

            for (int i = 0; i < _sensorCount; i++)
            {
                PersonalSpaceMA.UpsertParameter(_avatar, ParamPrefix + i,
                    ParameterSyncType.Float, localOnly: !_sync, saved: false, def: 0f);
            }
            AssetDatabase.SaveAssets();
        }

        // "PS_" + 数字 = センサーパラメータ
        private static bool IsSensorParamName(string name)
        {
            if (name == null || !name.StartsWith(ParamPrefix)) return false;
            string rest = name.Substring(ParamPrefix.Length);
            if (rest.Length == 0) return false;
            foreach (char c in rest) if (!char.IsDigit(c)) return false;
            return true;
        }

        private void RemoveSetup()
        {
            TearDown(full: true);
            AssetDatabase.SaveAssets();
            Debug.Log("[PersonalSpace] 削除しました: " + _avatar.name);
        }

        // 生成物を丸ごと除去してまっさらにする（Setup 冒頭でも呼ぶ）。
        // full=false のときメニューアセットは残す（GenerateMenu が中身を作り直すため）。
        private void TearDown(bool full)
        {
            // 新レイアウト: 集約コンテナごと削除（Sensors/リング/近接/MA を一括）
            DestroyChild(PersonalSpaceMA.ContainerName);
            // 旧レイアウト(〜v0.8.x はアバター直下に個別配置)の掃除
            DestroyChild(LegacySensorsRoot);
            DestroyChild("PS_RangeViz");
            DestroyChild("PS_NearSensor");
            PersonalSpaceMA.RemoveAll(_avatar); // 旧 PS_ModularAvatar(直下)
            // 生成アセット(Controller/クリップ/メニュー)を削除
            PersonalSpaceRemote.DeleteAssets(includeMenu: full);
            // 旧方式で直接編集した Expression Parameters / Menu の残骸も掃除
            PersonalSpaceMA.CleanupLegacyExprParams(_avatar, ParamPrefix);
            PersonalSpaceMA.CleanupLegacyMenu(_avatar);
        }

        private void DestroyChild(string name)
        {
            Transform t = _avatar.transform.Find(name);
            if (t != null) Undo.DestroyObjectImmediate(t.gameObject);
        }
    }
}
#endif
