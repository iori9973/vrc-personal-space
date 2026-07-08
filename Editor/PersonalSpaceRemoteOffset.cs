#if VRC_SDK_VRCSDK3
using System.Collections.Generic;
using System.IO;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace PersonalSpace.Editor
{
    /// <summary>
    /// リモート位置ズラし(遅延補償)用の Animator とメニューを生成する静的ヘルパー。
    /// （Setup ウィンドウから一括で呼ばれる。単体のウィンドウは持たない）
    ///
    /// 仕組み:
    ///  - 常駐アプリが逃走方向を同期パラメータ PS_OffX / PS_OffZ (-1〜1) として送る。
    ///  - リモート(他プレーヤー視点 = IsLocal:false)でのみ、Armature の localPosition を
    ///    2D BlendTree で最大 lead(m) だけオフセットし、「先回りして避けている」ように見せる。
    ///  - ローカル(自分視点 = IsLocal:true)ではオフセットしない(実際に /input で動くため)。
    ///
    /// 生成物は Modular Avatar 経由で非破壊注入する(PersonalSpaceMA)。FaceEmo 等と共存できる。
    /// ※Unity 外で自動テストできないため、生成物はエディタ上での動作確認が必要。
    /// </summary>
    internal static class PersonalSpaceRemote
    {
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

        /// <summary>遅延補償の Controller を生成し、MA Merge Animator と同期パラメータを注入する。</summary>
        public static void GenerateOffset(VRCAvatarDescriptor avatar, float lead, bool enabledDefault)
        {
            var animator = avatar.GetComponent<Animator>();
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
            string armPath = AnimationUtility.CalculateTransformPath(armature, avatar.transform);
            Vector3 basePos = armature.localPosition;

            EnsureFolder(GeneratedDir);
            EnsureFolder(AnimDir);

            // 各方向のクリップ(Armature.localPosition を basePos から ±lead ずらす)
            AnimationClip center = MakeClip("PS_Off_Center", armPath, basePos);
            AnimationClip xPlus = MakeClip("PS_Off_XPlus", armPath, basePos + new Vector3(lead, 0f, 0f));
            AnimationClip xMinus = MakeClip("PS_Off_XMinus", armPath, basePos + new Vector3(-lead, 0f, 0f));
            AnimationClip zPlus = MakeClip("PS_Off_ZPlus", armPath, basePos + new Vector3(0f, 0f, lead));
            AnimationClip zMinus = MakeClip("PS_Off_ZMinus", armPath, basePos + new Vector3(0f, 0f, -lead));

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

            // Modular Avatar 経由で FX にマージ＋同期パラメータを注入（FaceEmo 等と共存）
            PersonalSpaceMA.CleanupLegacyExprParams(avatar, "PS_");
            PersonalSpaceMA.CleanupLegacyMenu(avatar);
            PersonalSpaceMA.EnsureMergeAnimator(avatar, controller);
            PersonalSpaceMA.UpsertParameter(avatar, PEnabled, ParameterSyncType.Bool,
                localOnly: false, saved: true, def: enabledDefault ? 1f : 0f);
            PersonalSpaceMA.UpsertParameter(avatar, POffX, ParameterSyncType.Float,
                localOnly: false, saved: false, def: 0f);
            PersonalSpaceMA.UpsertParameter(avatar, POffZ, ParameterSyncType.Float,
                localOnly: false, saved: false, def: 0f);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[PersonalSpace] リモートオフセット生成完了: " + ControllerPath
                      + " (Armature=" + armPath + ")");
        }

        /// <summary>Expression Menu(PS_Menu) を作り、MA Menu Installer で非破壊インストールする。</summary>
        public static void GenerateMenu(VRCAvatarDescriptor avatar, bool enabledDefault)
        {
            EnsureFolder(GeneratedDir);

            string menuPath = GeneratedDir + "/PS_Menu.asset";
            var psMenu = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(menuPath);
            if (psMenu == null)
            {
                psMenu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
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
            AddRadial(psMenu, "反応範囲", PRange);
            AddRadial(psMenu, "押し出しの強さ", PGain);
            AddRadial(psMenu, "遅延補償の量", PLead);

            // 「Personal Space」サブメニューにまとめるラッパーを作る（root 直下が散らからないよう）
            string rootPath = GeneratedDir + "/PS_MenuRoot.asset";
            var psRoot = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(rootPath);
            if (psRoot == null)
            {
                psRoot = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                psRoot.name = "PS_MenuRoot";
                AssetDatabase.CreateAsset(psRoot, rootPath);
            }
            if (psRoot.controls == null)
                psRoot.controls = new List<VRCExpressionsMenu.Control>();
            if (!psRoot.controls.Exists(c =>
                c.type == VRCExpressionsMenu.Control.ControlType.SubMenu && c.subMenu == psMenu))
            {
                psRoot.controls.Add(new VRCExpressionsMenu.Control
                {
                    name = "Personal Space",
                    type = VRCExpressionsMenu.Control.ControlType.SubMenu,
                    subMenu = psMenu,
                });
                EditorUtility.SetDirty(psRoot);
            }

            // MA で非破壊にメニュー＆パラメータを注入（FaceEmo 等と共存）
            PersonalSpaceMA.CleanupLegacyExprParams(avatar, "PS_");
            PersonalSpaceMA.CleanupLegacyMenu(avatar);
            PersonalSpaceMA.EnsureMenuInstaller(avatar, psRoot);
            PersonalSpaceMA.UpsertParameter(avatar, PEnabled, ParameterSyncType.Bool,
                localOnly: false, saved: true, def: enabledDefault ? 1f : 0f);
            // 反応範囲/強さ/遅延補償量: ローカル(非同期)。既定 範囲1.0, 強さ0.5, 遅延補償0.5
            PersonalSpaceMA.UpsertParameter(avatar, PRange, ParameterSyncType.Float,
                localOnly: true, saved: true, def: 1.0f);
            PersonalSpaceMA.UpsertParameter(avatar, PGain, ParameterSyncType.Float,
                localOnly: true, saved: true, def: 0.5f);
            PersonalSpaceMA.UpsertParameter(avatar, PLead, ParameterSyncType.Float,
                localOnly: true, saved: true, def: 0.5f);

            AssetDatabase.SaveAssets();
            Debug.Log("[PersonalSpace] Expression Menu を生成/更新しました: " + menuPath);
        }

        /// <summary>遅延補償・メニュー分の生成物と MA コンポーネントを除去する（センサーは残す）。</summary>
        public static void Remove(VRCAvatarDescriptor avatar)
        {
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath) != null)
                AssetDatabase.DeleteAsset(ControllerPath);
            if (AssetDatabase.IsValidFolder(AnimDir))
                AssetDatabase.DeleteAsset(AnimDir);

            PersonalSpaceMA.RemoveComponentsOfType<ModularAvatarMenuInstaller>(avatar);
            PersonalSpaceMA.RemoveComponentsOfType<ModularAvatarMergeAnimator>(avatar);
            PersonalSpaceMA.RemoveParametersMatching(avatar, n =>
                n == PEnabled || n == POffX || n == POffZ || n == PRange || n == PGain || n == PLead);
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

        private static AnimationClip MakeClip(string clipName, string armPath, Vector3 pos)
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
