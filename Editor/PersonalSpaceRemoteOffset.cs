#if VRC_SDK_VRCSDK3
using System.Collections.Generic;
using System.IO;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Contact.Components;

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

        // 範囲の視覚表示（足元リング）
        private const string RangeCtrlPath = GeneratedDir + "/PS_RangeViz.controller";
        private const string RangeMatPath = GeneratedDir + "/PS_RangeViz.mat";
        private const string RangeObj = "PS_RangeViz";   // 表示メッシュ(コンテナ配下)
        private const string PShowRange = "PS_ShowRange"; // 範囲表示 ON/OFF
        // アニメの参照パスはアバター root 相対（MA Merge Animator は Absolute）。
        private static string RangeVizPath => PersonalSpaceMA.ContainerName + "/" + RangeObj;

        // 透明化モード（近づかれたら他人視点で自分が消える）
        private const string CloakCtrlPath = GeneratedDir + "/PS_Cloak.controller";
        private const string NearObj = "PS_NearSensor";   // 近接検知用の受信機
        private const string PNear = "PS_Near";           // 一定内に誰かいる(同期Bool)
        private const string PCloak = "PS_CloakMode";     // 透明化モード ON/OFF(同期Bool)
        private const string PCloakRange = "PS_CloakRange"; // 透明化の距離(Radial・ローカル)
        private const string PCloakSelf = "PS_CloakSelf";   // 自分視点でも消す(ローカル・非同期)
        // 近接センサーのアバター root 相対パス（半径アニメの参照先）
        private static string CloakSensorPath => PersonalSpaceMA.ContainerName + "/" + NearObj;

        // サブメニュー資産
        private const string MenuPushPath = GeneratedDir + "/PS_MenuPush.asset";
        private const string MenuCloakPath = GeneratedDir + "/PS_MenuCloak.asset";

        // 全ヒューマノイドに自動生成される Contact Sender のタグ
        private static readonly string[] BodyTags = { "Head", "Torso", "Hand", "Foot", "Finger" };

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
        public static void GenerateMenu(VRCAvatarDescriptor avatar, bool enabledDefault,
                                        bool includePush, bool includeCloak)
        {
            EnsureFolder(GeneratedDir);

            string menuPath = GeneratedDir + "/PS_Menu.asset";
            var psMenu = LoadOrCreateMenu(menuPath, "PS_Menu");
            psMenu.controls = new List<VRCExpressionsMenu.Control>();

            if (includePush && includeCloak)
            {
                // 両方 ON: 「押し出し」「透明化」のサブページに分ける
                var pushMenu = LoadOrCreateMenu(MenuPushPath, "PS_MenuPush");
                pushMenu.controls = new List<VRCExpressionsMenu.Control>();
                AddPushControls(pushMenu);
                EditorUtility.SetDirty(pushMenu);

                var cloakMenu = LoadOrCreateMenu(MenuCloakPath, "PS_MenuCloak");
                cloakMenu.controls = new List<VRCExpressionsMenu.Control>();
                AddCloakControls(cloakMenu);
                EditorUtility.SetDirty(cloakMenu);

                psMenu.controls.Add(SubMenuControl("押し出し", pushMenu));
                psMenu.controls.Add(SubMenuControl("透明化", cloakMenu));
            }
            else if (includePush)
            {
                // 片方だけなら無駄な階層を作らず直接並べる
                AddPushControls(psMenu);
            }
            else if (includeCloak)
            {
                AddCloakControls(psMenu);
            }
            EditorUtility.SetDirty(psMenu);

            // 「Personal Space」サブメニューにまとめるラッパー
            string rootPath = GeneratedDir + "/PS_MenuRoot.asset";
            var psRoot = LoadOrCreateMenu(rootPath, "PS_MenuRoot");
            psRoot.controls = new List<VRCExpressionsMenu.Control>
            {
                SubMenuControl("Personal Space", psMenu),
            };
            EditorUtility.SetDirty(psRoot);

            // MA で非破壊にメニュー＆パラメータを注入（FaceEmo 等と共存）
            PersonalSpaceMA.CleanupLegacyExprParams(avatar, "PS_");
            PersonalSpaceMA.CleanupLegacyMenu(avatar);
            PersonalSpaceMA.EnsureMenuInstaller(avatar, psRoot);
            if (includePush)
            {
                PersonalSpaceMA.UpsertParameter(avatar, PEnabled, ParameterSyncType.Bool,
                    localOnly: false, saved: true, def: enabledDefault ? 1f : 0f);
                // 反応範囲/強さ/遅延補償量: ローカル(非同期)。既定 範囲1.0, 強さ0.5, 遅延補償0.5
                PersonalSpaceMA.UpsertParameter(avatar, PRange, ParameterSyncType.Float,
                    localOnly: true, saved: true, def: 1.0f);
                PersonalSpaceMA.UpsertParameter(avatar, PGain, ParameterSyncType.Float,
                    localOnly: true, saved: true, def: 0.5f);
                PersonalSpaceMA.UpsertParameter(avatar, PLead, ParameterSyncType.Float,
                    localOnly: true, saved: true, def: 0.5f);
                PersonalSpaceMA.UpsertParameter(avatar, PShowRange, ParameterSyncType.Bool,
                    localOnly: true, saved: true, def: 0f);
            }
            if (includeCloak)
            {
                PersonalSpaceMA.UpsertParameter(avatar, PCloak, ParameterSyncType.Bool,
                    localOnly: false, saved: true, def: 0f);
                // 透明化の距離: ローカル(非同期)。既定 1.0(=最大距離)
                PersonalSpaceMA.UpsertParameter(avatar, PCloakRange, ParameterSyncType.Float,
                    localOnly: true, saved: true, def: 1.0f);
                // 自分からも消す: ローカル(非同期)。既定 0(=自分には見える)
                PersonalSpaceMA.UpsertParameter(avatar, PCloakSelf, ParameterSyncType.Bool,
                    localOnly: true, saved: true, def: 0f);
            }

            AssetDatabase.SaveAssets();
            Debug.Log("[PersonalSpace] Expression Menu を生成/更新しました: " + menuPath);
        }

        private static VRCExpressionsMenu LoadOrCreateMenu(string path, string name)
        {
            var m = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(path);
            if (m == null)
            {
                m = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                m.name = name;
                AssetDatabase.CreateAsset(m, path);
            }
            if (m.controls == null) m.controls = new List<VRCExpressionsMenu.Control>();
            return m;
        }

        private static VRCExpressionsMenu.Control SubMenuControl(string name, VRCExpressionsMenu sub)
        {
            return new VRCExpressionsMenu.Control
            {
                name = name,
                type = VRCExpressionsMenu.Control.ControlType.SubMenu,
                subMenu = sub,
            };
        }

        private static void AddPushControls(VRCExpressionsMenu m)
        {
            m.controls.Add(new VRCExpressionsMenu.Control
            {
                name = "有効",
                type = VRCExpressionsMenu.Control.ControlType.Toggle,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = PEnabled },
                value = 1f,
            });
            AddRadial(m, "反応範囲", PRange);
            AddRadial(m, "押し出しの強さ", PGain);
            AddRadial(m, "遅延補償の量", PLead);
            AddToggle(m, "範囲表示", PShowRange);
        }

        private static void AddCloakControls(VRCExpressionsMenu m)
        {
            AddToggle(m, "透明化", PCloak);
            AddRadial(m, "透明化の距離", PCloakRange);
            AddToggle(m, "自分からも消える", PCloakSelf);
        }

        /// <summary>生成アセット（Controller/クリップ/メニュー等）を削除する。
        /// includeMenu=false のときメニューアセットは残す（Setup 時は GenerateMenu が
        /// 同一パスに作り直すため、削除→同フレーム再生成の競合で controls が空になるのを防ぐ）。</summary>
        public static void DeleteAssets(bool includeMenu = true)
        {
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath) != null)
                AssetDatabase.DeleteAsset(ControllerPath);
            if (AssetDatabase.IsValidFolder(AnimDir))
                AssetDatabase.DeleteAsset(AnimDir);

            if (includeMenu)
            {
                string[] menus =
                {
                    GeneratedDir + "/PS_Menu.asset", GeneratedDir + "/PS_MenuRoot.asset",
                    MenuPushPath, MenuCloakPath,
                };
                foreach (string p in menus)
                {
                    if (AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(p) != null)
                        AssetDatabase.DeleteAsset(p);
                }
            }
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(RangeCtrlPath) != null)
                AssetDatabase.DeleteAsset(RangeCtrlPath);
            if (AssetDatabase.LoadAssetAtPath<Material>(RangeMatPath) != null)
                AssetDatabase.DeleteAsset(RangeMatPath);
            string ringPath = GeneratedDir + "/PS_RingMesh.asset";
            if (AssetDatabase.LoadAssetAtPath<Mesh>(ringPath) != null)
                AssetDatabase.DeleteAsset(ringPath);
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(CloakCtrlPath) != null)
                AssetDatabase.DeleteAsset(CloakCtrlPath);
        }

        /// <summary>足元に反応範囲を示す半透明ディスクを生成し、PS_Range に連動させる（ローカル表示）。</summary>
        public static void GenerateRangeViz(VRCAvatarDescriptor avatar, float maxRadius)
        {
            EnsureFolder(GeneratedDir);
            EnsureFolder(AnimDir);

            // 表示メッシュ(コンテナ配下 PersonalSpace/PS_RangeViz)
            Transform container = PersonalSpaceMA.EnsureContainer(avatar);
            Transform t = container.Find(RangeObj);
            GameObject go = t != null ? t.gameObject : null;
            if (go == null)
            {
                go = new GameObject(RangeObj);
                Undo.RegisterCreatedObjectUndo(go, "Create " + RangeObj);
                go.transform.SetParent(container, false);
            }
            go.transform.localPosition = new Vector3(0f, 0.01f, 0f);
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = new Vector3(maxRadius, 0.005f, maxRadius); // 平たいプレビュー
            go.SetActive(true);

            var mf = go.GetComponent<MeshFilter>();
            if (mf == null) mf = Undo.AddComponent<MeshFilter>(go);
            mf.sharedMesh = GetRingMesh();
            var mr = go.GetComponent<MeshRenderer>();
            if (mr == null) mr = Undo.AddComponent<MeshRenderer>(go);
            mr.sharedMaterial = GetRangeMaterial();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;

            // リングは外周半径0.5なので半径R には scale.xz = 2R。実効範囲 R = maxRadius*(0.2+0.8*PS_Range)
            float sMin = 2f * maxRadius * 0.2f;
            float sMax = 2f * maxRadius * 1.0f;
            const float flatY = 0.005f;

            AnimationClip off = MakeVizClip("PS_RangeViz_Off", false, sMin, flatY);
            AnimationClip minC = MakeVizClip("PS_RangeViz_Min", true, sMin, flatY);
            AnimationClip maxC = MakeVizClip("PS_RangeViz_Max", true, sMax, flatY);

            if (File.Exists(RangeCtrlPath)) AssetDatabase.DeleteAsset(RangeCtrlPath);
            var controller = AnimatorController.CreateAnimatorControllerAtPath(RangeCtrlPath);
            controller.AddParameter("IsLocal", AnimatorControllerParameterType.Bool);
            controller.AddParameter(PShowRange, AnimatorControllerParameterType.Bool);
            controller.AddParameter(PRange, AnimatorControllerParameterType.Float);

            AnimatorControllerLayer[] layers = controller.layers;
            layers[0].name = "PS_RangeViz";
            controller.layers = layers;
            AnimatorStateMachine sm = controller.layers[0].stateMachine;

            var offState = sm.AddState("Off");
            offState.motion = off;
            sm.defaultState = offState;

            var tree = new BlendTree
            {
                name = "PS_RangeTree",
                blendType = BlendTreeType.Simple1D,
                blendParameter = PRange,
            };
            AssetDatabase.AddObjectToAsset(tree, controller);
            tree.children = new[]
            {
                new ChildMotion { motion = minC, threshold = 0f, timeScale = 1f, directBlendParameter = "" },
                new ChildMotion { motion = maxC, threshold = 1f, timeScale = 1f, directBlendParameter = "" },
            };
            var onState = sm.AddState("Shown");
            onState.motion = tree;

            // Off -> Shown : ローカル かつ 範囲表示ON
            var toOn = offState.AddTransition(onState);
            toOn.hasExitTime = false; toOn.duration = 0f;
            toOn.AddCondition(AnimatorConditionMode.If, 0f, "IsLocal");
            toOn.AddCondition(AnimatorConditionMode.If, 0f, PShowRange);
            // Shown -> Off : リモート化 or OFF
            var toOff1 = onState.AddTransition(offState);
            toOff1.hasExitTime = false; toOff1.duration = 0f;
            toOff1.AddCondition(AnimatorConditionMode.IfNot, 0f, "IsLocal");
            var toOff2 = onState.AddTransition(offState);
            toOff2.hasExitTime = false; toOff2.duration = 0f;
            toOff2.AddCondition(AnimatorConditionMode.IfNot, 0f, PShowRange);

            EditorUtility.SetDirty(controller);

            PersonalSpaceMA.EnsureMergeAnimator(avatar, controller);
            PersonalSpaceMA.UpsertParameter(avatar, PShowRange, ParameterSyncType.Bool,
                localOnly: true, saved: true, def: 0f);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[PersonalSpace] 範囲表示を生成しました: " + RangeCtrlPath);
        }

        /// <summary>透明化モード: 一定内に近づかれたら他人視点で自分のメッシュを非表示にする。</summary>
        public static void GenerateCloak(VRCAvatarDescriptor avatar, float cloakDistance)
        {
            EnsureFolder(GeneratedDir);
            EnsureFolder(AnimDir);

            // 近接検知の受信機(PS_NearSensor)。Constant型で「一定内に誰かいる」を PS_Near(同期)に。
            Transform container = PersonalSpaceMA.EnsureContainer(avatar);
            Transform ns = container.Find(NearObj);
            GameObject nso = ns != null ? ns.gameObject : null;
            if (nso == null)
            {
                nso = new GameObject(NearObj);
                Undo.RegisterCreatedObjectUndo(nso, "Create " + NearObj);
                nso.transform.SetParent(container, false);
            }
            nso.transform.localPosition = new Vector3(0f, 0.9f, 0f);
            nso.transform.localRotation = Quaternion.identity;
            nso.transform.localScale = Vector3.one;
            var recv = nso.GetComponent<VRCContactReceiver>();
            if (recv == null) recv = Undo.AddComponent<VRCContactReceiver>(nso);
            recv.shapeType = ContactBase.ShapeType.Capsule;
            recv.radius = cloakDistance;
            recv.height = 3.0f;
            recv.position = Vector3.zero;
            recv.rotation = Quaternion.identity;
            recv.receiverType = ContactReceiver.ReceiverType.Constant; // 0/1
            recv.parameter = PNear;
            recv.collisionTags = new List<string>(BodyTags);
            recv.allowSelf = false;
            recv.allowOthers = true;
            // Local Only を OFF にする。ON だと PS_Near が同期されず他人視点で消せないため。
            // （所有者の検知結果が同期パラメータ経由で全リモートへ伝わる。受信機1個なのでランク影響は軽微）
            recv.localOnly = false;
            EditorUtility.SetDirty(recv);

            // アバターの全 Renderer を列挙して 表示/非表示 クリップを作る（PS_ 自前オブジェクトは除外）
            var visible = new AnimationClip { name = "PS_Cloak_Visible" };
            var hidden = new AnimationClip { name = "PS_Cloak_Hidden" };
            foreach (Renderer r in avatar.GetComponentsInChildren<Renderer>(true))
            {
                if (IsUnderPSObject(r.transform, avatar.transform)) continue;
                string path = AnimationUtility.CalculateTransformPath(r.transform, avatar.transform);
                System.Type rt = r.GetType();
                SetConstantTyped(visible, path, rt, "m_Enabled", 1f);
                SetConstantTyped(hidden, path, rt, "m_Enabled", 0f);
            }
            SaveClipAsset(visible, "PS_Cloak_Visible");
            SaveClipAsset(hidden, "PS_Cloak_Hidden");

            if (File.Exists(CloakCtrlPath)) AssetDatabase.DeleteAsset(CloakCtrlPath);
            var controller = AnimatorController.CreateAnimatorControllerAtPath(CloakCtrlPath);
            controller.AddParameter("IsLocal", AnimatorControllerParameterType.Bool);
            controller.AddParameter(PCloak, AnimatorControllerParameterType.Bool);
            controller.AddParameter(PNear, AnimatorControllerParameterType.Bool);
            controller.AddParameter(PCloakRange, AnimatorControllerParameterType.Float);
            controller.AddParameter(PCloakSelf, AnimatorControllerParameterType.Bool);

            AnimatorControllerLayer[] layers = controller.layers;
            layers[0].name = "PS_Cloak";
            controller.layers = layers;
            AnimatorStateMachine sm = controller.layers[0].stateMachine;

            var visState = sm.AddState("Visible");
            visState.motion = visible;
            sm.defaultState = visState;
            var hidState = sm.AddState("Hidden");
            hidState.motion = hidden;

            // 消える条件 = 透明化ON かつ 近い かつ (他人視点 または 自分からも消すON)。
            // Visible -> Hidden : 他人視点で消す（従来）
            var toHideRemote = visState.AddTransition(hidState);
            toHideRemote.hasExitTime = false; toHideRemote.duration = 0f;
            toHideRemote.AddCondition(AnimatorConditionMode.If, 0f, PCloak);
            toHideRemote.AddCondition(AnimatorConditionMode.If, 0f, PNear);
            toHideRemote.AddCondition(AnimatorConditionMode.IfNot, 0f, "IsLocal");
            // Visible -> Hidden : 「自分からも消える」ON なら自分視点(IsLocal)でも消す
            var toHideSelf = visState.AddTransition(hidState);
            toHideSelf.hasExitTime = false; toHideSelf.duration = 0f;
            toHideSelf.AddCondition(AnimatorConditionMode.If, 0f, PCloak);
            toHideSelf.AddCondition(AnimatorConditionMode.If, 0f, PNear);
            toHideSelf.AddCondition(AnimatorConditionMode.If, 0f, PCloakSelf);
            // Hidden -> Visible : 消える条件が崩れたら戻す
            var s1 = hidState.AddTransition(visState);
            s1.hasExitTime = false; s1.duration = 0f;
            s1.AddCondition(AnimatorConditionMode.IfNot, 0f, PCloak);
            var s2 = hidState.AddTransition(visState);
            s2.hasExitTime = false; s2.duration = 0f;
            s2.AddCondition(AnimatorConditionMode.IfNot, 0f, PNear);
            // 自分視点 かつ 自分からも消すOFF のとき見える（他人視点はこの遷移を取らない）
            var s3 = hidState.AddTransition(visState);
            s3.hasExitTime = false; s3.duration = 0f;
            s3.AddCondition(AnimatorConditionMode.If, 0f, "IsLocal");
            s3.AddCondition(AnimatorConditionMode.IfNot, 0f, PCloakSelf);

            // 2枚目のレイヤー: 近接センサーの半径を PS_CloakRange(0..1) で動的に変える。
            // 実効距離 = cloakDistance*(0.2 + 0.8*PS_CloakRange)。押し出しの反応範囲と同じ考え方。
            float rMax = cloakDistance;
            float rMin = cloakDistance * 0.2f;
            AnimationClip rMinC = MakeCloakRadiusClip("PS_CloakRange_Min", rMin);
            AnimationClip rMaxC = MakeCloakRadiusClip("PS_CloakRange_Max", rMax);

            controller.AddLayer("PS_CloakRange");
            AnimatorControllerLayer[] ls = controller.layers;
            int li = ls.Length - 1;
            ls[li].defaultWeight = 1f;
            AnimatorStateMachine rsm = ls[li].stateMachine;
            var rTree = new BlendTree
            {
                name = "PS_CloakRangeTree",
                blendType = BlendTreeType.Simple1D,
                blendParameter = PCloakRange,
            };
            AssetDatabase.AddObjectToAsset(rTree, controller);
            rTree.children = new[]
            {
                new ChildMotion { motion = rMinC, threshold = 0f, timeScale = 1f, directBlendParameter = "" },
                new ChildMotion { motion = rMaxC, threshold = 1f, timeScale = 1f, directBlendParameter = "" },
            };
            var rState = rsm.AddState("Range");
            rState.motion = rTree;
            rsm.defaultState = rState;
            controller.layers = ls;

            EditorUtility.SetDirty(controller);

            PersonalSpaceMA.EnsureMergeAnimator(avatar, controller);
            PersonalSpaceMA.UpsertParameter(avatar, PNear, ParameterSyncType.Bool,
                localOnly: false, saved: false, def: 0f);
            PersonalSpaceMA.UpsertParameter(avatar, PCloak, ParameterSyncType.Bool,
                localOnly: false, saved: true, def: 0f);
            // 透明化の距離(ローカル)。既定 1.0=最大。メニュー未生成でもこの既定で動く。
            PersonalSpaceMA.UpsertParameter(avatar, PCloakRange, ParameterSyncType.Float,
                localOnly: true, saved: true, def: 1.0f);
            // 自分からも消す(ローカル/非同期・自分視点だけの効果)。既定 0=自分には見える。
            PersonalSpaceMA.UpsertParameter(avatar, PCloakSelf, ParameterSyncType.Bool,
                localOnly: true, saved: true, def: 0f);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[PersonalSpace] 透明化モードを生成しました: " + CloakCtrlPath);
        }

        // r の Transform が PS_ 自前オブジェクト配下かどうか（透明化の対象から除外するため）
        private static bool IsUnderPSObject(Transform t, Transform avatarRoot)
        {
            for (Transform p = t; p != null && p != avatarRoot; p = p.parent)
            {
                string n = p.name;
                // 新レイアウト(コンテナ配下は全部自前) + 旧レイアウトの個別名
                if (n == PersonalSpaceMA.ContainerName ||
                    n == "PS_ModularAvatar" || n == "PersonalSpaceSensors" ||
                    n == RangeObj || n == NearObj)
                    return true;
            }
            return false;
        }

        // 近接センサー(VRCContactReceiver)の半径を一定値にするクリップ。
        private static AnimationClip MakeCloakRadiusClip(string clipName, float radius)
        {
            var clip = new AnimationClip { name = clipName };
            var binding = new EditorCurveBinding
            {
                path = CloakSensorPath,
                type = typeof(VRCContactReceiver),
                propertyName = "radius",
            };
            var curve = new AnimationCurve(new Keyframe(0f, radius), new Keyframe(1f / 60f, radius));
            AnimationUtility.SetEditorCurve(clip, binding, curve);
            SaveClipAsset(clip, clipName);
            return clip;
        }

        private static void SaveClipAsset(AnimationClip clip, string clipName)
        {
            string path = AnimDir + "/" + clipName + ".anim";
            if (File.Exists(path)) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(clip, path);
        }

        // 足元に置く平たいリング(輪)メッシュを生成してアセット化する。
        // 外周半径0.5・帯幅は外周の約12%。XZ平面に寝かせ、法線は上向き。
        private static Mesh GetRingMesh()
        {
            string ringPath = GeneratedDir + "/PS_RingMesh.asset";
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(ringPath);
            if (existing != null) return existing;

            const int seg = 64;      // 円周の分割数（滑らかさ）
            const float outer = 0.5f;
            const float inner = 0.44f;
            var verts = new Vector3[seg * 2];
            var norms = new Vector3[seg * 2];
            var uvs = new Vector2[seg * 2];
            var tris = new int[seg * 6];
            for (int i = 0; i < seg; i++)
            {
                float a = 2f * Mathf.PI * i / seg;
                float cx = Mathf.Cos(a), cz = Mathf.Sin(a);
                verts[i * 2] = new Vector3(cx * outer, 0f, cz * outer);
                verts[i * 2 + 1] = new Vector3(cx * inner, 0f, cz * inner);
                norms[i * 2] = Vector3.up;
                norms[i * 2 + 1] = Vector3.up;
                uvs[i * 2] = new Vector2(i / (float)seg, 1f);
                uvs[i * 2 + 1] = new Vector2(i / (float)seg, 0f);
            }
            for (int i = 0; i < seg; i++)
            {
                int o0 = i * 2, in0 = i * 2 + 1;
                int o1 = ((i + 1) % seg) * 2, in1 = ((i + 1) % seg) * 2 + 1;
                int t = i * 6;
                tris[t] = o0; tris[t + 1] = in0; tris[t + 2] = o1;
                tris[t + 3] = o1; tris[t + 4] = in0; tris[t + 5] = in1;
            }
            var mesh = new Mesh { name = "PS_RingMesh" };
            mesh.vertices = verts;
            mesh.normals = norms;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, ringPath);
            return mesh;
        }

        private static Material GetRangeMaterial()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(RangeMatPath);
            if (mat != null) return mat;
            Shader sh = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Transparent");
            mat = new Material(sh) { color = new Color(0.2f, 0.8f, 1f, 0.25f) };
            AssetDatabase.CreateAsset(mat, RangeMatPath);
            return mat;
        }

        private static AnimationClip MakeVizClip(string clipName, bool active, float scaleXZ, float scaleY)
        {
            var clip = new AnimationClip { name = clipName };
            string p = RangeVizPath;
            SetConstantTyped(clip, p, typeof(GameObject), "m_IsActive", active ? 1f : 0f);
            SetConstantTyped(clip, p, typeof(Transform), "m_LocalScale.x", scaleXZ);
            SetConstantTyped(clip, p, typeof(Transform), "m_LocalScale.y", scaleY);
            SetConstantTyped(clip, p, typeof(Transform), "m_LocalScale.z", scaleXZ);
            string path = AnimDir + "/" + clipName + ".anim";
            if (File.Exists(path)) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(clip, path);
            return clip;
        }

        private static void SetConstantTyped(AnimationClip clip, string path, System.Type type, string prop, float value)
        {
            var binding = new EditorCurveBinding { path = path, type = type, propertyName = prop };
            var curve = new AnimationCurve(new Keyframe(0f, value), new Keyframe(1f / 60f, value));
            AnimationUtility.SetEditorCurve(clip, binding, curve);
        }

        private static void AddToggle(VRCExpressionsMenu menu, string label, string param)
        {
            if (menu.controls.Exists(c => c.parameter != null && c.parameter.name == param)) return;
            menu.controls.Add(new VRCExpressionsMenu.Control
            {
                name = label,
                type = VRCExpressionsMenu.Control.ControlType.Toggle,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = param },
                value = 1f,
            });
            EditorUtility.SetDirty(menu);
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
