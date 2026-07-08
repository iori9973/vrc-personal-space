#if VRC_SDK_VRCSDK3
using System.Collections.Generic;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace PersonalSpace.Editor
{
    /// <summary>
    /// Modular Avatar 経由でパラメータ/メニュー/アニメを非破壊に注入するヘルパ。
    ///
    /// FaceEmo など NDMF 系ツールは Expression のパラメータ/メニューをビルド時に再生成する。
    /// そのため Expression 資産を直接編集すると消えてしまう。MA コンポーネントで注入すれば
    /// NDMF パイプラインで合成され、共存できる。
    ///
    /// 生成物はアバター直下の 1 つのコンテナ(PersonalSpace)配下にまとめ、MA コンポーネントは
    /// その中の PS_ModularAvatar に集約する。
    /// </summary>
    internal static class PersonalSpaceMA
    {
        public const string ObjName = "PS_ModularAvatar";
        // 生成物をすべてこの 1 つの子にまとめる（Hierarchy を散らかさないため）。
        // アバター直下: PersonalSpace/{Sensors, PS_RangeViz, PS_NearSensor, PS_ModularAvatar}
        public const string ContainerName = "PersonalSpace";

        /// <summary>集約コンテナ(PersonalSpace)を取得（無ければ作る）。原点・等倍で置く。</summary>
        public static Transform EnsureContainer(VRCAvatarDescriptor avatar)
        {
            Transform c = avatar.transform.Find(ContainerName);
            if (c != null) return c;

            var go = new GameObject(ContainerName);
            Undo.RegisterCreatedObjectUndo(go, "Create " + ContainerName);
            go.transform.SetParent(avatar.transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            return go.transform;
        }

        public static GameObject EnsureRoot(VRCAvatarDescriptor avatar)
        {
            Transform container = EnsureContainer(avatar);
            Transform t = container.Find(ObjName);
            if (t != null) return t.gameObject;

            var go = new GameObject(ObjName);
            Undo.RegisterCreatedObjectUndo(go, "Create " + ObjName);
            go.transform.SetParent(container, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            return go;
        }

        // PS_ModularAvatar を新レイアウト(コンテナ配下)・旧レイアウト(アバター直下)の両方から探す。
        private static Transform FindRoot(VRCAvatarDescriptor avatar)
        {
            Transform c = avatar.transform.Find(ContainerName);
            if (c != null)
            {
                Transform r = c.Find(ObjName);
                if (r != null) return r;
            }
            return avatar.transform.Find(ObjName);
        }

        /// <summary>MA Parameters に 1 件 upsert する（struct なのでインデックス差し替え）。</summary>
        public static void UpsertParameter(VRCAvatarDescriptor avatar, string name,
            ParameterSyncType sync, bool localOnly, bool saved, float def)
        {
            GameObject root = EnsureRoot(avatar);
            var comp = root.GetComponent<ModularAvatarParameters>();
            if (comp == null) comp = Undo.AddComponent<ModularAvatarParameters>(root);

            var cfg = new ParameterConfig
            {
                nameOrPrefix = name,
                remapTo = "",
                internalParameter = false,
                isPrefix = false,
                syncType = sync,
                localOnly = localOnly,
                defaultValue = def,
                saved = saved,
                hasExplicitDefaultValue = true,
            };

            int idx = comp.parameters.FindIndex(p => p.nameOrPrefix == name);
            if (idx >= 0) comp.parameters[idx] = cfg;
            else comp.parameters.Add(cfg);
            EditorUtility.SetDirty(comp);
        }

        public static void EnsureMenuInstaller(VRCAvatarDescriptor avatar, VRCExpressionsMenu menu)
        {
            GameObject root = EnsureRoot(avatar);
            foreach (var mi in root.GetComponents<ModularAvatarMenuInstaller>())
            {
                if (mi.menuToAppend == menu) { EditorUtility.SetDirty(mi); return; }
            }
            var comp = Undo.AddComponent<ModularAvatarMenuInstaller>(root);
            comp.menuToAppend = menu;
            EditorUtility.SetDirty(comp);
        }

        public static void EnsureMergeAnimator(VRCAvatarDescriptor avatar, RuntimeAnimatorController controller)
        {
            GameObject root = EnsureRoot(avatar);
            foreach (var m in root.GetComponents<ModularAvatarMergeAnimator>())
            {
                if (m.animator == controller) { EditorUtility.SetDirty(m); return; }
            }
            var comp = Undo.AddComponent<ModularAvatarMergeAnimator>(root);
            comp.animator = controller;
            comp.layerType = VRCAvatarDescriptor.AnimLayerType.FX;
            comp.pathMode = MergeAnimatorPathMode.Absolute; // クリップは avatar root 相対で書いている
            comp.matchAvatarWriteDefaults = true;
            EditorUtility.SetDirty(comp);
        }

        /// <summary>旧方式でアバターの Expression Menu に直接差した PS メニューを掃除（重複防止）。</summary>
        public static void CleanupLegacyMenu(VRCAvatarDescriptor avatar)
        {
            VRCExpressionsMenu menu = avatar.expressionsMenu;
            if (menu == null || menu.controls == null) return;
            int before = menu.controls.Count;
            menu.controls.RemoveAll(c =>
                c != null && (
                    c.name == "Personal Space" ||
                    (c.type == VRCExpressionsMenu.Control.ControlType.SubMenu &&
                     c.subMenu != null && c.subMenu.name != null &&
                     (c.subMenu.name == "PS_Menu" || c.subMenu.name == "PS_MenuRoot"))));
            if (menu.controls.Count != before) EditorUtility.SetDirty(menu);
        }

        /// <summary>旧方式で Expression Parameters に直接足していた PS_ を掃除（重複防止）。</summary>
        public static void CleanupLegacyExprParams(VRCAvatarDescriptor avatar, string prefix)
        {
            VRCExpressionParameters vp = avatar.expressionParameters;
            if (vp == null || vp.parameters == null) return;
            var list = new List<VRCExpressionParameters.Parameter>(vp.parameters);
            int before = list.Count;
            list.RemoveAll(p => p.name != null && p.name.StartsWith(prefix));
            if (list.Count != before)
            {
                vp.parameters = list.ToArray();
                EditorUtility.SetDirty(vp);
            }
        }

        /// <summary>MA Parameters から条件に合う名前のエントリを削除する。</summary>
        public static void RemoveParametersMatching(VRCAvatarDescriptor avatar, System.Predicate<string> match)
        {
            Transform t = FindRoot(avatar);
            if (t == null) return;
            var comp = t.GetComponent<ModularAvatarParameters>();
            if (comp == null) return;
            comp.parameters.RemoveAll(p => p.nameOrPrefix != null && match(p.nameOrPrefix));
            EditorUtility.SetDirty(comp);
        }

        public static void RemoveComponentsOfType<T>(VRCAvatarDescriptor avatar) where T : Component
        {
            Transform t = FindRoot(avatar);
            if (t == null) return;
            foreach (var c in t.GetComponents<T>()) Undo.DestroyObjectImmediate(c);
        }

        /// <summary>MA コンポーネントが 1 つも残っていなければ統合オブジェクトを削除する。</summary>
        public static void DestroyRootIfEmpty(VRCAvatarDescriptor avatar)
        {
            Transform t = FindRoot(avatar);
            if (t == null) return;
            var comps = t.GetComponents<AvatarTagComponent>();
            if (comps == null || comps.Length == 0) Undo.DestroyObjectImmediate(t.gameObject);
        }

        public static void RemoveAll(VRCAvatarDescriptor avatar)
        {
            Transform t = FindRoot(avatar);
            if (t != null) Undo.DestroyObjectImmediate(t.gameObject);
        }
    }
}
#endif
