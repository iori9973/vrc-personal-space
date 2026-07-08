#if VRC_SDK_VRCSDK3
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace PersonalSpace.Editor
{
    /// <summary>
    /// リモート位置ズラし(遅延補償)用の Animator を生成する Editor 拡張。
    ///
    /// 仕組み:
    ///  - 常駐アプリが逃走方向を同期パラメータ PS_OffX / PS_OffZ (-1〜1) として送る。
    ///  - リモート(他プレーヤー視点 = IsLocal:false)でのみ、Armature の localPosition を
    ///    2D BlendTree で最大 lead(m) だけオフセットし、「先回りして避けている」ように見せる。
    ///  - ローカル(自分視点 = IsLocal:true)ではオフセットしない(実際に /input で動くため)。
    ///  - 逃げるのを止めるとパラメータが 0 に戻り、実同期位置が追いついてオフセットも消える。
    ///
    /// 生成した AnimatorController は Modular Avatar の Merge Animator で FX に統合する想定
    /// (C 工程)。単体テスト時は一時的にアバターの FX に設定してもよい。
    ///
    /// ※Unity 外で自動テストできないため、生成物はエディタ上での動作確認が必要。
    /// </summary>
    public class PersonalSpaceRemoteOffsetWindow : EditorWindow
    {
        private VRCAvatarDescriptor _avatar;
        private float _lead = 0.25f;       // 最大リード距離(m)
        private bool _enabledDefault = true;

        private const string GeneratedDir = "Assets/PersonalSpace/Generated";
        private const string AnimDir = GeneratedDir + "/PS_Anim";
        private const string ControllerPath = GeneratedDir + "/PS_RemoteOffset.controller";

        private const string POffX = "PS_OffX";
        private const string POffZ = "PS_OffZ";
        private const string PEnabled = "PS_Enabled";

        // メニュー(Radial)で実行時調整する値。ローカルのみ効くので非同期。
        private const string PRange = "PS_Range";  // 反応範囲
        private const string PGain = "PS_Gain";    // 押し出しの強さ
        private const string PLead = "PS_Lead";    // 遅延補償の量

        [MenuItem("Tools/Personal Space/Remote Offset (遅延補償)")]
        public static void Open()
        {
            GetWindow<PersonalSpaceRemoteOffsetWindow>("PS Remote Offset");
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("リモート位置ズラし (遅延補償)", EditorStyles.boldLabel);
            _avatar = (VRCAvatarDescriptor)EditorGUILayout.ObjectField(
                "Avatar", _avatar, typeof(VRCAvatarDescriptor), true);
            _lead = EditorGUILayout.Slider("最大リード距離 (m)", _lead, 0.05f, 0.6f);
            _enabledDefault = EditorGUILayout.Toggle("既定でON", _enabledDefault);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(_avatar == null))
            {
                if (GUILayout.Button("生成 / 更新")) Generate();
                if (GUILayout.Button("Expression Menu を生成 (ON/OFF)")) GenerateMenu();
                if (GUILayout.Button("削除")) Remove();
            }

            EditorGUILayout.HelpBox(
                "PS_OffX / PS_OffZ (Float, 同期) と PS_Enabled (Bool, 同期) を追加し、\n" +
                ControllerPath + " を生成します。\n" +
                "この Controller は Modular Avatar の Merge Animator で FX に統合してください。\n" +
                "アプリ側は既定でオフセットを送信します(止めるには --no-remote-offset)。",
                MessageType.Info);
        }

        private void Generate()
        {
            var animator = _avatar.GetComponent<Animator>();
            if (animator == null || !animator.isHuman)
            {
                EditorUtility.DisplayDialog("Personal Space",
                    "ヒューマノイドの Animator が必要です。", "OK");
                return;
            }
            Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            if (hips == null || hips.parent == null)
            {
                EditorUtility.DisplayDialog("Personal Space",
                    "Hips とその親(Armature)が見つかりませんでした。", "OK");
                return;
            }
            Transform armature = hips.parent;
            string armPath = AnimationUtility.CalculateTransformPath(armature, _avatar.transform);
            Vector3 basePos = armature.localPosition;

            EnsureFolder(GeneratedDir);
            EnsureFolder(AnimDir);

            // 各方向のクリップ(Armature.localPosition を basePos から ±lead ずらす)
            AnimationClip center = MakeClip("PS_Off_Center", armPath, basePos);
            AnimationClip xPlus = MakeClip("PS_Off_XPlus", armPath, basePos + new Vector3(_lead, 0f, 0f));
            AnimationClip xMinus = MakeClip("PS_Off_XMinus", armPath, basePos + new Vector3(-_lead, 0f, 0f));
            AnimationClip zPlus = MakeClip("PS_Off_ZPlus", armPath, basePos + new Vector3(0f, 0f, _lead));
            AnimationClip zMinus = MakeClip("PS_Off_ZMinus", armPath, basePos + new Vector3(0f, 0f, -_lead));

            // AnimatorController
            if (File.Exists(ControllerPath)) AssetDatabase.DeleteAsset(ControllerPath);
            var controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            controller.AddParameter("IsLocal", AnimatorControllerParameterType.Bool);
            controller.AddParameter(PEnabled, AnimatorControllerParameterType.Bool);
            controller.AddParameter(POffX, AnimatorControllerParameterType.Float);
            controller.AddParameter(POffZ, AnimatorControllerParameterType.Float);

            AnimatorControllerLayer[] layers = controller.layers;
            layers[0].name = "PS_RemoteOffset";
            controller.layers = layers;
            AnimatorStateMachine sm = controller.layers[0].stateMachine;

            var offState = sm.AddState("Off");
            offState.motion = center;
            sm.defaultState = offState;

            var tree = new BlendTree
            {
                name = "PS_OffsetTree",
                blendType = BlendTreeType.FreeformCartesian2D,
                blendParameter = POffX,
                blendParameterY = POffZ,
            };
            AssetDatabase.AddObjectToAsset(tree, controller);
            tree.children = new[]
            {
                Child(center, 0f, 0f),
                Child(xPlus, 1f, 0f),
                Child(xMinus, -1f, 0f),
                Child(zPlus, 0f, 1f),
                Child(zMinus, 0f, -1f),
            };

            var onState = sm.AddState("RemoteOffset");
            onState.motion = tree;

            // Off -> RemoteOffset : リモート かつ 有効
            var toOn = offState.AddTransition(onState);
            toOn.hasExitTime = false;
            toOn.duration = 0.1f;
            toOn.AddCondition(AnimatorConditionMode.IfNot, 0f, "IsLocal");
            toOn.AddCondition(AnimatorConditionMode.If, 0f, PEnabled);

            // RemoteOffset -> Off : ローカルになった or 無効になった
            var toOffLocal = onState.AddTransition(offState);
            toOffLocal.hasExitTime = false;
            toOffLocal.duration = 0.1f;
            toOffLocal.AddCondition(AnimatorConditionMode.If, 0f, "IsLocal");

            var toOffDisabled = onState.AddTransition(offState);
            toOffDisabled.hasExitTime = false;
            toOffDisabled.duration = 0.1f;
            toOffDisabled.AddCondition(AnimatorConditionMode.IfNot, 0f, PEnabled);

            EditorUtility.SetDirty(controller);

            AddSyncedParameters();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[PersonalSpace] リモートオフセット生成完了: " + ControllerPath
                      + " (Armature=" + armPath + ")");
        }

        private static ChildMotion Child(Motion motion, float x, float y)
        {
            return new ChildMotion
            {
                motion = motion,
                position = new Vector2(x, y),
                timeScale = 1f,
                directBlendParameter = "",
            };
        }

        private AnimationClip MakeClip(string clipName, string armPath, Vector3 pos)
        {
            var clip = new AnimationClip { name = clipName };
            SetConstant(clip, armPath, "m_LocalPosition.x", pos.x);
            SetConstant(clip, armPath, "m_LocalPosition.y", pos.y);
            SetConstant(clip, armPath, "m_LocalPosition.z", pos.z);
            string path = AnimDir + "/" + clipName + ".anim";
            if (File.Exists(path)) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(clip, path);
            return clip;
        }

        private static void SetConstant(AnimationClip clip, string path, string prop, float value)
        {
            var binding = new EditorCurveBinding
            {
                path = path,
                type = typeof(Transform),
                propertyName = prop,
            };
            var curve = new AnimationCurve(new Keyframe(0f, value), new Keyframe(1f / 60f, value));
            AnimationUtility.SetEditorCurve(clip, binding, curve);
        }

        private void AddSyncedParameters()
        {
            VRCExpressionParameters vp = _avatar.expressionParameters;
            if (vp == null)
            {
                vp = CreateInstance<VRCExpressionParameters>();
                vp.parameters = new VRCExpressionParameters.Parameter[0];
                EnsureFolder(GeneratedDir);
                AssetDatabase.CreateAsset(vp, GeneratedDir + "/PS_ExpressionParameters.asset");
                _avatar.expressionParameters = vp;
                EditorUtility.SetDirty(_avatar);
            }

            var list = new List<VRCExpressionParameters.Parameter>(
                vp.parameters ?? new VRCExpressionParameters.Parameter[0]);

            Upsert(list, POffX, VRCExpressionParameters.ValueType.Float, 0f, saved: false, synced: true);
            Upsert(list, POffZ, VRCExpressionParameters.ValueType.Float, 0f, saved: false, synced: true);
            Upsert(list, PEnabled, VRCExpressionParameters.ValueType.Bool,
                _enabledDefault ? 1f : 0f, saved: true, synced: true);

            vp.parameters = list.ToArray();
            EditorUtility.SetDirty(vp);
        }

        private static void Upsert(List<VRCExpressionParameters.Parameter> list, string name,
            VRCExpressionParameters.ValueType type, float def, bool saved, bool synced)
        {
            var existing = list.Find(p => p.name == name);
            if (existing != null)
            {
                existing.valueType = type;
                existing.defaultValue = def;
                existing.saved = saved;
                existing.networkSynced = synced;
                return;
            }
            list.Add(new VRCExpressionParameters.Parameter
            {
                name = name,
                valueType = type,
                defaultValue = def,
                saved = saved,
                networkSynced = synced,
            });
        }

        // Expression Menu に PS_Enabled の ON/OFF トグルを追加する。
        private void GenerateMenu()
        {
            EnsureFolder(GeneratedDir);

            // PS_Enabled が無ければ同期パラメータを用意しておく
            AddSyncedParameters();
            AssetDatabase.SaveAssets();

            string menuPath = GeneratedDir + "/PS_Menu.asset";
            var psMenu = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(menuPath);
            if (psMenu == null)
            {
                psMenu = CreateInstance<VRCExpressionsMenu>();
                psMenu.name = "PS_Menu";
                AssetDatabase.CreateAsset(psMenu, menuPath);
            }
            if (psMenu.controls == null)
                psMenu.controls = new List<VRCExpressionsMenu.Control>();

            if (!psMenu.controls.Exists(c => c.parameter != null && c.parameter.name == PEnabled))
            {
                psMenu.controls.Add(new VRCExpressionsMenu.Control
                {
                    name = "有効",
                    type = VRCExpressionsMenu.Control.ControlType.Toggle,
                    parameter = new VRCExpressionsMenu.Control.Parameter { name = PEnabled },
                    value = 1f,
                });
                EditorUtility.SetDirty(psMenu);
            }

            // Radial 用パラメータを用意し、メニューに Radial を追加
            AddMenuTuningParams();
            AddRadial(psMenu, "反応範囲", PRange);
            AddRadial(psMenu, "押し出しの強さ", PGain);
            AddRadial(psMenu, "遅延補償の量", PLead);

            // アバターのルートメニューに「Personal Space」サブメニューを差し込む
            VRCExpressionsMenu root = _avatar.expressionsMenu;
            if (root == null)
            {
                root = CreateInstance<VRCExpressionsMenu>();
                root.name = "PS_RootMenu";
                root.controls = new List<VRCExpressionsMenu.Control>();
                AssetDatabase.CreateAsset(root, GeneratedDir + "/PS_RootMenu.asset");
                _avatar.expressionsMenu = root;
                EditorUtility.SetDirty(_avatar);
            }
            if (root.controls == null)
                root.controls = new List<VRCExpressionsMenu.Control>();

            bool hasSub = root.controls.Exists(c =>
                c.type == VRCExpressionsMenu.Control.ControlType.SubMenu && c.subMenu == psMenu);
            if (!hasSub)
            {
                if (root.controls.Count >= 8)
                {
                    Debug.LogWarning("[PersonalSpace] ルートメニューが満杯(8)です。手動で PS_Menu を追加してください。");
                }
                else
                {
                    root.controls.Add(new VRCExpressionsMenu.Control
                    {
                        name = "Personal Space",
                        type = VRCExpressionsMenu.Control.ControlType.SubMenu,
                        subMenu = psMenu,
                    });
                    EditorUtility.SetDirty(root);
                }
            }

            AssetDatabase.SaveAssets();
            Debug.Log("[PersonalSpace] Expression Menu を生成/更新しました: " + menuPath);
        }

        // Radial 用の非同期パラメータ(反応範囲/強さ/遅延補償量)を Expression Parameters に追加。
        private void AddMenuTuningParams()
        {
            VRCExpressionParameters vp = _avatar.expressionParameters;
            if (vp == null) return; // AddSyncedParameters で必ず作られている前提
            var list = new List<VRCExpressionParameters.Parameter>(
                vp.parameters ?? new VRCExpressionParameters.Parameter[0]);

            // 既定: 範囲=最大(1.0), 強さ=中(0.5→gain1.5), 遅延補償=中(0.5→lead1.0)
            Upsert(list, PRange, VRCExpressionParameters.ValueType.Float, 1.0f, saved: true, synced: false);
            Upsert(list, PGain, VRCExpressionParameters.ValueType.Float, 0.5f, saved: true, synced: false);
            Upsert(list, PLead, VRCExpressionParameters.ValueType.Float, 0.5f, saved: true, synced: false);

            vp.parameters = list.ToArray();
            EditorUtility.SetDirty(vp);
        }

        private static void AddRadial(VRCExpressionsMenu menu, string label, string param)
        {
            if (menu.controls.Exists(c =>
                c.type == VRCExpressionsMenu.Control.ControlType.RadialPuppet &&
                c.subParameters != null && c.subParameters.Length > 0 &&
                c.subParameters[0] != null && c.subParameters[0].name == param))
            {
                return;
            }
            menu.controls.Add(new VRCExpressionsMenu.Control
            {
                name = label,
                type = VRCExpressionsMenu.Control.ControlType.RadialPuppet,
                subParameters = new[]
                {
                    new VRCExpressionsMenu.Control.Parameter { name = param },
                },
            });
            EditorUtility.SetDirty(menu);
        }

        private void Remove()
        {
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath) != null)
                AssetDatabase.DeleteAsset(ControllerPath);
            if (AssetDatabase.IsValidFolder(AnimDir))
                AssetDatabase.DeleteAsset(AnimDir);

            VRCExpressionParameters vp = _avatar.expressionParameters;
            if (vp != null && vp.parameters != null)
            {
                var list = new List<VRCExpressionParameters.Parameter>(vp.parameters);
                list.RemoveAll(p => p.name == POffX || p.name == POffZ || p.name == PEnabled
                                    || p.name == PRange || p.name == PGain || p.name == PLead);
                vp.parameters = list.ToArray();
                EditorUtility.SetDirty(vp);
                AssetDatabase.SaveAssets();
            }
            Debug.Log("[PersonalSpace] リモートオフセットを削除しました");
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            string leaf = Path.GetFileName(path);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
#endif
