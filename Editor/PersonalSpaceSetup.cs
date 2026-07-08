#if VRC_SDK_VRCSDK3
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
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

        private const string RootName = "PersonalSpaceSensors";
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
            using (new EditorGUI.DisabledScope(_avatar == null))
            {
                if (GUILayout.Button("セットアップ / 更新")) Setup();
                if (GUILayout.Button("削除")) RemoveSetup();
            }

            EditorGUILayout.HelpBox(
                $"PS_0 … PS_{_sensorCount - 1} (Float) を Expression Parameters に追加します。\n" +
                "常駐アプリは同じセンサー数で起動してください: python personal_space.py --sensors " + _sensorCount + "\n" +
                "最大反応半径 = メニュー「反応範囲」の上限。実行時はメニューでこの範囲内に縮められます。\n" +
                "Local Only のため PC のパフォーマンスランクには計上されません。\n" +
                "通常は「同期する」OFF でも OSC 出力されます。届かない場合のみ ON で再セットアップ。",
                MessageType.Info);
        }

        private void Setup()
        {
            Transform root = _avatar.transform.Find(RootName);
            if (root == null)
            {
                var rootGo = new GameObject(RootName);
                Undo.RegisterCreatedObjectUndo(rootGo, "Create PersonalSpace Sensors");
                rootGo.transform.SetParent(_avatar.transform, false);
                root = rootGo.transform;
            }
            root.localPosition = new Vector3(0f, _height, 0f);
            root.localRotation = Quaternion.identity;
            root.localScale = Vector3.one;

            // センサー数を変えたとき古い子が残らないよう、一度作り直す。
            for (int c = root.childCount - 1; c >= 0; c--)
            {
                Undo.DestroyObjectImmediate(root.GetChild(c).gameObject);
            }

            for (int i = 0; i < _sensorCount; i++)
            {
                // i=0 が正面(+Z)。時計回りに配置。
                float theta = 2f * Mathf.PI * i / _sensorCount;
                var dir = new Vector3(Mathf.Sin(theta), 0f, Mathf.Cos(theta));

                var childGo = new GameObject(ParamPrefix + i);
                Undo.RegisterCreatedObjectUndo(childGo, "Create PersonalSpace sensor");
                childGo.transform.SetParent(root, false);
                childGo.transform.localPosition = dir * _offset;
                childGo.transform.localRotation = Quaternion.identity;
                childGo.transform.localScale = Vector3.one;

                var recv = Undo.AddComponent<VRCContactReceiver>(childGo);
                recv.shapeType = ContactBase.ShapeType.Sphere;
                recv.radius = _radius;
                recv.position = Vector3.zero;
                recv.rotation = Quaternion.identity;
                recv.receiverType = ContactReceiver.ReceiverType.Proximity;
                recv.parameter = ParamPrefix + i;
                recv.collisionTags = new List<string>(BodyTags);
                recv.allowSelf = false;   // 自分の手足では反応しない
                recv.allowOthers = true;  // 他人を検知する
                recv.localOnly = true;    // 自分のクライアントでのみ評価（ランク非計上）
                EditorUtility.SetDirty(recv);
            }

            AddParameters();
            Debug.Log($"[PersonalSpace] セットアップ完了: {_avatar.name} ({_sensorCount}方向)");
        }

        private void AddParameters()
        {
            VRCExpressionParameters vp = _avatar.expressionParameters;
            if (vp == null)
            {
                vp = CreateInstance<VRCExpressionParameters>();
                vp.parameters = new VRCExpressionParameters.Parameter[0];

                const string dir = "Assets/PersonalSpace/Generated";
                if (!AssetDatabase.IsValidFolder(dir))
                {
                    Directory.CreateDirectory(dir);
                    AssetDatabase.Refresh();
                }
                AssetDatabase.CreateAsset(vp, dir + "/PS_ExpressionParameters.asset");
                _avatar.expressionParameters = vp;
                EditorUtility.SetDirty(_avatar);
                Debug.LogWarning("[PersonalSpace] Expression Parameters が未設定だったため新規作成しました: "
                                 + dir + "/PS_ExpressionParameters.asset");
            }

            // 既存の PS_ パラメータをいったん除去してから追加（センサー数変更に追従）。
            var list = new List<VRCExpressionParameters.Parameter>(
                vp.parameters ?? new VRCExpressionParameters.Parameter[0]);
            list.RemoveAll(p => p.name != null && p.name.StartsWith(ParamPrefix));

            for (int i = 0; i < _sensorCount; i++)
            {
                list.Add(new VRCExpressionParameters.Parameter
                {
                    name = ParamPrefix + i,
                    valueType = VRCExpressionParameters.ValueType.Float,
                    defaultValue = 0f,
                    saved = false,
                    networkSynced = _sync,
                });
            }

            vp.parameters = list.ToArray();
            EditorUtility.SetDirty(vp);
            AssetDatabase.SaveAssets();
        }

        private void RemoveSetup()
        {
            Transform root = _avatar.transform.Find(RootName);
            if (root != null)
            {
                Undo.DestroyObjectImmediate(root.gameObject);
            }

            VRCExpressionParameters vp = _avatar.expressionParameters;
            if (vp != null && vp.parameters != null)
            {
                var list = new List<VRCExpressionParameters.Parameter>(vp.parameters);
                list.RemoveAll(p => p.name != null && p.name.StartsWith(ParamPrefix));
                vp.parameters = list.ToArray();
                EditorUtility.SetDirty(vp);
                AssetDatabase.SaveAssets();
            }
            Debug.Log("[PersonalSpace] 削除しました: " + _avatar.name);
        }
    }
}
#endif
