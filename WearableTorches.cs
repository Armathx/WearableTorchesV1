using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WearableTorches
{
    [BepInPlugin("com.thomas.wearabletorches", "Wearable Torches", "1.0.6")]
    public class WearableTorchesPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            _harmony = new Harmony("com.thomas.wearabletorches");
            _harmony.PatchAll();
            Log.LogInfo("Wearable Torches loaded (G key, torch required in hand)");
        }

        private void Update()
        {
            if (ZInput.instance == null)
                return;

            // Joueur local : touche G
            var localPlayer = Player.m_localPlayer;
            if (localPlayer != null)
            {
                WearableTorchManager.HandleLocalInput(localPlayer);
            }

            // Sync visuelle pour TOUS les joueurs (local + autres)
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
        private static readonly Dictionary<long, GameObject> BackTorches =
            new Dictionary<long, GameObject>();

        public const string ZdoKeyBackTorch = "WearableTorches_BackTorch";
        public const string ZdoKeyBackTorchPrefab = "WearableTorches_BackTorchPrefab";

        private static readonly Vector3 FlameOffset = new Vector3(0f, 0f, 0.61f);
        private static readonly Vector3 LightOffset = new Vector3(0f, 0f, 0.61f);
        private static readonly Quaternion BackRotationOffset = Quaternion.Euler(-80f, 0f, 0f);

        private static long GetPlayerKey(Player player)
        {
            if (player == null) return 0;
            return player.GetPlayerID();
        }

        private static GameObject FindExistingBackTorchObject(Player player)
        {
            if (player == null) return null;
            foreach (var t in player.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "BackTorchObject")
                    return t.gameObject;
            }
            return null;
        }

        public static void HandleLocalInput(Player player)
        {
            var nview = GetNView(player);
            if (nview == null || !nview.IsOwner())
                return;

            if (!ZInput.GetKeyDown(KeyCode.G))
                return;

            var zdo = nview.GetZDO();
            if (zdo == null)
                return;

            bool hasBackTorch = zdo.GetBool(ZdoKeyBackTorch, false);

            if (!hasBackTorch)
            {
                var torchItem = GetTorchInHands(player);

                if (torchItem == null)
                {
                    WearableTorchesPlugin.Log.LogInfo(
                        "Back torch: no torch in hand, toggle ignored for " + player.GetPlayerName());
                    return;
                }

                var dropPrefab = torchItem.m_dropPrefab;
                if (dropPrefab == null)
                {
                    WearableTorchesPlugin.Log.LogWarning(
                        "Back torch: torch in hand has no dropPrefab for " + player.GetPlayerName());
                    return;
                }

                string prefabName = dropPrefab.name;
                if (string.IsNullOrEmpty(prefabName))
                    return;

                zdo.Set(ZdoKeyBackTorchPrefab, prefabName);
                zdo.Set(ZdoKeyBackTorch, true);

                HideVanillaTorchVisuals(player, true);

                WearableTorchesPlugin.Log.LogInfo(
                    "Back torch ON (G key, from hand) for " + player.GetPlayerName() +
                    " prefab=" + prefabName);
            }
            else
            {
                zdo.Set(ZdoKeyBackTorch, false);
                zdo.Set(ZdoKeyBackTorchPrefab, "");

                HideVanillaTorchVisuals(player, false);

                WearableTorchesPlugin.Log.LogInfo(
                    "Back torch OFF (G key) for " + player.GetPlayerName());
            }
        }

        public static void SyncBackTorchForPlayer(Player player)
        {
            if (player == null)
                return;

            var nview = GetNView(player);
            if (nview == null)
                return;

            var zdo = nview.GetZDO();
            if (zdo == null)
                return;

            bool hasBackTorch = zdo.GetBool(ZdoKeyBackTorch, false);
            string prefabName = zdo.GetString(ZdoKeyBackTorchPrefab, "");

            long key = GetPlayerKey(player);
            if (key == 0)
                return;

            BackTorches.TryGetValue(key, out var existing);

            if (hasBackTorch && !string.IsNullOrEmpty(prefabName))
            {
                if (existing != null)
                    return;

                var existingChild = FindExistingBackTorchObject(player);
                if (existingChild != null)
                {
                    BackTorches[key] = existingChild;
                    return;
                }

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
                    BackTorches[key] = go;
            }
            else
            {
                if (existing != null)
                {
                    UnityEngine.Object.Destroy(existing);
                    BackTorches.Remove(key);
                }
                else
                {
                    var stray = FindExistingBackTorchObject(player);
                    if (stray != null)
                        UnityEngine.Object.Destroy(stray);
                }
            }
        }

        private static ZNetView GetNView(Player player)
        {
            return player != null ? player.GetComponent<ZNetView>() : null;
        }

        internal static bool IsTorch(ItemDrop.ItemData item)
        {
            if (item == null) return false;
            return (item.m_shared != null ? item.m_shared.m_name : "")
                .ToLower()
                .Contains("torch");
        }

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

        private static GameObject CreateBackTorchVisual(Player player, GameObject prefab)
        {
            if (prefab == null)
                return null;

            var existingChild = FindExistingBackTorchObject(player);
            if (existingChild != null)
                return existingChild;

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

            foreach (var renderer in prefab.GetComponentsInChildren<MeshRenderer>(true))
            {
                var cloneMesh = UnityEngine.Object.Instantiate(renderer.gameObject, backTorch.transform, false);
                cloneMesh.name = "BackTorchMesh";
            }

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

        internal static void HideVanillaTorchVisuals(Player player, bool hide)
        {
            if (player == null) return;

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

        public static void RemoveBackTorch(Player player)
        {
            if (player == null) return;

            long key = GetPlayerKey(player);
            if (key != 0 && BackTorches.TryGetValue(key, out var go))
            {
                if (go != null)
                    UnityEngine.Object.Destroy(go);
                BackTorches.Remove(key);
            }

            var child = FindExistingBackTorchObject(player);
            if (child != null)
                UnityEngine.Object.Destroy(child);
        }

        // NEW: Disable back torch flag + visuals for a player (used by TP patch)
        public static void ForceDisableBackTorch(Player player)
        {
            if (player == null) return;

            var nview = GetNView(player);
            if (nview == null || !nview.IsValid())
                return;

            // Only the owner should write to its ZDO
            if (!nview.IsOwner())
                return;

            var zdo = nview.GetZDO();
            if (zdo == null)
                return;

            if (zdo.GetBool(ZdoKeyBackTorch, false))
            {
                zdo.Set(ZdoKeyBackTorch, false);
                zdo.Set(ZdoKeyBackTorchPrefab, "");
            }

            // Restore vanilla visuals and remove our object
            HideVanillaTorchVisuals(player, false);
            RemoveBackTorch(player);

            WearableTorchesPlugin.Log.LogInfo("Back torch OFF due to teleport for " + player.GetPlayerName());
        }
    }

    [HarmonyPatch(typeof(Player), "OnDestroy")]
    public static class Player_OnDestroy_Patch
    {
        static void Prefix(Player __instance)
        {
            WearableTorchManager.RemoveBackTorch(__instance);
        }
    }

    // NEW: Teleport safety - if the player has a back torch, disable it before teleport
    [HarmonyPatch(typeof(Player), nameof(Player.TeleportTo))]
    public static class Player_TeleportTo_Patch
    {
        static void Prefix(Player __instance)
        {
            WearableTorchManager.ForceDisableBackTorch(__instance);
        }
    }
}
