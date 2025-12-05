using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WearableTorches
{
    [BepInPlugin("com.thomas.wearabletorches", "Wearable Torches", "1.0.0")]
    public class WearableTorchesPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            _harmony = new Harmony("com.thomas.wearabletorches");
            _harmony.PatchAll();
            Log.LogInfo("Wearable Torches loaded");
        }

        private void Update()
        {
            if (ZInput.instance == null)
                return;

            var allPlayers = Player.GetAllPlayers();
            foreach (var p in allPlayers)
            {
                WearableTorchManager.SyncBackTorchForPlayer(p);
            }
        }

        private void OnDestroy()
        {
            WearableTorchManager.ForceDestroyBackTorch();
        }
    }

    public static class WearableTorchManager
    {
        private static readonly Dictionary<Player, GameObject> BackTorches =
            new Dictionary<Player, GameObject>();

        public const string ZdoKeyBackTorch = "WearableTorches_BackTorch";
        public const string ZdoKeyBackTorchPrefab = "WearableTorches_BackTorchPrefab";

        private static readonly Vector3 FlameOffset = new Vector3(0f, 0f, 0.61f);
        private static readonly Vector3 LightOffset = new Vector3(0f, 0f, 0.61f);
        private static readonly Quaternion BackRotationOffset = Quaternion.Euler(-80f, 0f, 0f);

        // network sync for all players (called from Update)
        public static void SyncBackTorchForPlayer(Player player)
        {
            var nview = GetNView(player);
            if (nview == null)
                return;

            var zdo = nview.GetZDO();
            if (zdo == null)
                return;

            bool hasBackTorch = zdo.GetBool(ZdoKeyBackTorch, false);
            string prefabName = zdo.GetString(ZdoKeyBackTorchPrefab, "");

            // auto OFF if player equips a torch in hands
            if (hasBackTorch && nview.IsOwner())
            {
                var torchInHands = GetTorchInHands(player);
                if (torchInHands != null)
                {
                    zdo.Set(ZdoKeyBackTorch, false);
                    zdo.Set(ZdoKeyBackTorchPrefab, "");
                    hasBackTorch = false;

                    // restore vanilla visuals
                    HideVanillaTorchVisuals(player, false);

                    WearableTorchesPlugin.Log.LogInfo(
                        "Back torch auto OFF (torch in hands) for " + player.GetPlayerName());
                }
            }

            GameObject existing;
            BackTorches.TryGetValue(player, out existing);

            if (hasBackTorch && !string.IsNullOrEmpty(prefabName))
            {
                if (existing == null)
                {
                    var prefab = ZNetScene.instance != null
                        ? ZNetScene.instance.GetPrefab(prefabName)
                        : null;

                    if (prefab == null)
                    {
                        WearableTorchesPlugin.Log.LogWarning(
                            "Back torch: prefab '" + prefabName + "' not found in ZNetScene");
                        return;
                    }

                    var go = CreateBackTorchVisual(player, prefab);
                    if (go != null)
                        BackTorches[player] = go;
                }
            }
            else
            {
                if (existing != null)
                {
                    UnityEngine.Object.Destroy(existing);
                    BackTorches.Remove(player);
                }
            }
        }

        // --- helpers ---

        private static ZNetView GetNView(Player player)
        {
            return player != null ? player.GetComponent<ZNetView>() : null;
        }

        internal static bool IsTorch(ItemDrop.ItemData item)
        {
            if (item == null) return false;
            return (item.m_shared != null ? item.m_shared.m_name : "").ToLower().Contains("torch");
        }

        // torch in hands (right / left)
        private static ItemDrop.ItemData GetTorchInHands(Player player)
        {
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;

                var getRight = typeof(Player).GetMethod("GetRightItem", flags);
                var getLeft = typeof(Player).GetMethod("GetLeftItem", flags);

                var right = getRight != null
                    ? (ItemDrop.ItemData)getRight.Invoke(player, null)
                    : null;
                var left = getLeft != null
                    ? (ItemDrop.ItemData)getLeft.Invoke(player, null)
                    : null;

                if (IsTorch(right)) return right;
                if (IsTorch(left)) return left;

                return null;
            }
            catch (Exception e)
            {
                WearableTorchesPlugin.Log.LogError("GetTorchInHands reflection error: " + e);
                return null;
            }
        }

        // visual torch on back (no network, no loot)
        private static GameObject CreateBackTorchVisual(Player player, GameObject prefab)
        {
            if (prefab == null)
                return null;

            var attach = FindBackAttach(player);

            var backTorch = new GameObject("BackTorchObject");
            backTorch.transform.SetParent(attach, false);

            backTorch.transform.localRotation = BackRotationOffset;
            backTorch.transform.localPosition = new Vector3(-0.0026f, -0.002f, -0.0017f);

            Vector3 ps = attach.lossyScale;
            backTorch.transform.localScale = new Vector3(
                ps.x != 0 ? 1f / ps.x : 1f,
                ps.y != 0 ? 1f / ps.y : 1f,
                ps.z != 0 ? 1f / ps.z : 1f
            );

            // mesh copy (visual only)
            foreach (var renderer in prefab.GetComponentsInChildren<MeshRenderer>(true))
            {
                var cloneMesh = UnityEngine.Object.Instantiate(renderer.gameObject, backTorch.transform, false);
                cloneMesh.name = "BackTorchMesh";
            }

            // flames: player -> prefab
            GameObject fxPrefab = FindTorchFlamesOnPlayer(player);
            if (fxPrefab == null)
                fxPrefab = FindTorchFlamesInPrefab(prefab);

            if (fxPrefab != null)
            {
                var cloneFx = UnityEngine.Object.Instantiate(fxPrefab, backTorch.transform, false);
                cloneFx.name = "BackTorchFlames";
                cloneFx.transform.localRotation = Quaternion.identity;
                cloneFx.transform.localPosition = FlameOffset;
            }

            // light
            var lightGO = new GameObject("BackTorchLight");
            lightGO.transform.SetParent(backTorch.transform, false);
            lightGO.transform.localRotation = Quaternion.identity;
            lightGO.transform.localPosition = LightOffset;

            var light = lightGO.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.52f, 0.22f);
            light.range = 14f;
            light.intensity = 1.35f;
            light.shadows = LightShadows.Soft;

            return backTorch;
        }

        // hide / show vanilla torch meshes (back/hand) but keep our BackTorchObject
        internal static void HideVanillaTorchVisuals(Player player, bool hide)
        {
            if (player == null) return;

            // MeshRenderer
            foreach (var mr in player.GetComponentsInChildren<MeshRenderer>(true))
            {
                Transform t = mr.transform;

                if (IsUnderBackTorchObject(t))
                    continue;

                string n = t.name.ToLower();
                string parentName = t.parent != null ? t.parent.name.ToLower() : "";

                if (!n.Contains("torch") && !parentName.Contains("torch"))
                    continue;

                mr.enabled = !hide;
            }

            // SkinnedMeshRenderer
            foreach (var smr in player.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Transform t = smr.transform;

                if (IsUnderBackTorchObject(t))
                    continue;

                string n = t.name.ToLower();
                string parentName = t.parent != null ? t.parent.name.ToLower() : "";

                if (!n.Contains("torch") && !parentName.Contains("torch"))
                    continue;

                smr.enabled = !hide;
            }
        }

        private static bool IsUnderBackTorchObject(Transform t)
        {
            while (t != null)
            {
                if (t.name == "BackTorchObject")
                    return true;
                t = t.parent;
            }
            return false;
        }

        #region Finder

        private static Transform FindBackAttach(Player player)
        {
            string[] backBones =
            {
                "Visual/Armature/Hips/Spine/Spine1/Spine2",
                "Visual/Armature/Hips/Spine/Spine1",
                "Visual/Armature/Hips/Spine"
            };

            foreach (string path in backBones)
            {
                var t = player.transform.Find(path);
                if (t != null) return t;
            }

            return player.transform;
        }

        // FX from player hierarchy
        private static GameObject FindTorchFlamesOnPlayer(Player player)
        {
            foreach (var t in player.GetComponentsInChildren<Transform>(true))
            {
                if (!t.name.ToLower().Contains("torch"))
                    continue;

                var ps = t.GetComponentInChildren<ParticleSystem>(true);
                if (ps == null) continue;

                var mesh = t.GetComponentInChildren<MeshRenderer>(true);
                if (mesh != null) continue;

                var sk = t.GetComponentInChildren<SkinnedMeshRenderer>(true);
                if (sk != null) continue;

                return t.gameObject;
            }
            return null;
        }

        // FX from prefab
        private static GameObject FindTorchFlamesInPrefab(GameObject prefab)
        {
            if (prefab == null)
                return null;

            var allPS = prefab.GetComponentsInChildren<ParticleSystem>(true);
            foreach (var ps in allPS)
            {
                var t = ps.transform;

                var mesh = t.GetComponent<MeshRenderer>();
                var skinned = t.GetComponent<SkinnedMeshRenderer>();

                if (mesh == null && skinned == null)
                    return t.gameObject;
            }
            return null;
        }

        #endregion

        private static void DestroyBackTorch()
        {
            foreach (var kvp in BackTorches)
            {
                if (kvp.Value != null)
                    UnityEngine.Object.Destroy(kvp.Value);
            }
            BackTorches.Clear();
        }

        public static void ForceDestroyBackTorch()
        {
            DestroyBackTorch();
        }
    }

    // Patch: HideHandItems -> torch goes to custom back + hide vanilla visuals
    [HarmonyPatch(typeof(Humanoid), "HideHandItems")]
    public static class Humanoid_HideHandItems_Patch
    {
        static void Postfix(Humanoid __instance, bool onlyRightHand, bool animation, ref bool __result)
        {
            if (!__result)
                return;

            var player = __instance as Player;
            if (player == null)
                return;

            var nview = player.GetComponent<ZNetView>();
            if (nview == null || !nview.IsOwner())
                return;

            var zdo = nview.GetZDO();
            if (zdo == null)
                return;

            var tr = Traverse.Create(player);
            var hiddenLeft = tr.Field<ItemDrop.ItemData>("m_hiddenLeftItem").Value;
            var hiddenRight = tr.Field<ItemDrop.ItemData>("m_hiddenRightItem").Value;

            ItemDrop.ItemData torchItem = null;

            if (WearableTorchManager.IsTorch(hiddenRight))
                torchItem = hiddenRight;
            else if (WearableTorchManager.IsTorch(hiddenLeft))
                torchItem = hiddenLeft;

            if (torchItem == null)
            {
                zdo.Set(WearableTorchManager.ZdoKeyBackTorch, false);
                zdo.Set(WearableTorchManager.ZdoKeyBackTorchPrefab, "");
                return;
            }

            var dropPrefab = torchItem.m_dropPrefab;
            if (dropPrefab == null)
                return;

            string prefabName = dropPrefab.name;
            if (string.IsNullOrEmpty(prefabName))
                return;

            zdo.Set(WearableTorchManager.ZdoKeyBackTorchPrefab, prefabName);
            zdo.Set(WearableTorchManager.ZdoKeyBackTorch, true);

            // hide vanilla torch meshes
            WearableTorchManager.HideVanillaTorchVisuals(player, true);

            WearableTorchesPlugin.Log.LogInfo(
                "Back torch ON (HideHandItems) for " + player.GetPlayerName() +
                " prefab=" + prefabName);
        }
    }

    // Patch: ShowHandItems -> torch back OFF + restore vanilla visuals
    [HarmonyPatch]
    public static class Humanoid_ShowHandItems_Patch
    {
        static MethodInfo TargetMethod()
        {
            return AccessTools.Method(typeof(Humanoid), "ShowHandItems",
                new Type[] { typeof(bool), typeof(bool) });
        }

        static void Postfix(Humanoid __instance, bool onlyRightHand, bool animation)
        {
            var player = __instance as Player;
            if (player == null)
                return;

            var nview = player.GetComponent<ZNetView>();
            if (nview == null || !nview.IsOwner())
                return;

            var zdo = nview.GetZDO();
            if (zdo == null)
                return;

            zdo.Set(WearableTorchManager.ZdoKeyBackTorch, false);
            zdo.Set(WearableTorchManager.ZdoKeyBackTorchPrefab, "");

            // re-enable vanilla torch meshes
            WearableTorchManager.HideVanillaTorchVisuals(player, false);

            WearableTorchesPlugin.Log.LogInfo(
                "Back torch OFF (ShowHandItems) for " + player.GetPlayerName());
        }
    }
}
