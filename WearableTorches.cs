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

            // local input
            var localPlayer = Player.m_localPlayer;
            if (localPlayer != null && ZInput.GetKeyDown(KeyCode.G))
            {
                WearableTorchManager.OnBackTorchKeyPressed(localPlayer);
            }

            // sync all players
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
        private static readonly Dictionary<Player, GameObject> BackTorches = new Dictionary<Player, GameObject>();

        private const string ZdoKeyBackTorch = "WearableTorches_BackTorch";
        private const string ZdoKeyBackTorchPrefab = "WearableTorches_BackTorchPrefab";

        private static readonly Vector3 FlameOffset = new Vector3(0f, 0f, 0.61f);
        private static readonly Vector3 LightOffset = new Vector3(0f, 0f, 0.61f);
        private static readonly Quaternion BackRotationOffset = Quaternion.Euler(-80f, 0f, 0f);

        // Toggle G (owner)
        public static void OnBackTorchKeyPressed(Player player)
        {
            var nview = GetNView(player);
            if (nview == null)
                return;

            if (!nview.IsOwner())
                return;

            var zdo = nview.GetZDO();
            if (zdo == null)
                return;

            bool current = zdo.GetBool(ZdoKeyBackTorch, false);

            if (!current)
            {
                // turn ON -> need a torch + prefab name
                var torchItem = GetTorchInHands(player);
                if (torchItem == null)
                {
                    WearableTorchesPlugin.Log.LogInfo("Back torch toggle: no torch in hands");
                    return;
                }

                var dropPrefab = torchItem.m_dropPrefab;
                if (dropPrefab == null)
                {
                    WearableTorchesPlugin.Log.LogWarning("Back torch toggle: dropPrefab null");
                    return;
                }

                string prefabName = dropPrefab.name;
                if (string.IsNullOrEmpty(prefabName))
                {
                    WearableTorchesPlugin.Log.LogWarning("Back torch toggle: prefab name empty");
                    return;
                }

                // Write network state
                zdo.Set(ZdoKeyBackTorchPrefab, prefabName);
                zdo.Set(ZdoKeyBackTorch, true);
                WearableTorchesPlugin.Log.LogInfo($"Back torch ON for {player.GetPlayerName()} prefab={prefabName}");

                // Unequip from hands + play equip/unequip sound
                TryUnequipTorchFromHands(player, torchItem);
            }
            else
            {
                // turn OFF
                zdo.Set(ZdoKeyBackTorch, false);
                zdo.Set(ZdoKeyBackTorchPrefab, "");
                WearableTorchesPlugin.Log.LogInfo($"Back torch OFF for {player.GetPlayerName()}");
            }
        }

        // Sync per player (all clients)
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

            // NEW: if player has a back torch and equips a torch in hands,
            // we automatically turn off the back torch (owner only).
            if (hasBackTorch)
            {
                var torchInHands = GetTorchInHands(player);
                if (torchInHands != null && nview.IsOwner())
                {
                    zdo.Set(ZdoKeyBackTorch, false);
                    zdo.Set(ZdoKeyBackTorchPrefab, "");
                    hasBackTorch = false; // local override so we also destroy visual this frame
                    WearableTorchesPlugin.Log.LogInfo(
                        $"Back torch auto OFF for {player.GetPlayerName()} (torch equipped in hand)");
                }
            }

            BackTorches.TryGetValue(player, out var existing);

            if (hasBackTorch && !string.IsNullOrEmpty(prefabName))
            {
                if (existing == null)
                {
                    // spawn visual based on prefab name (same for all clients)
                    var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabName) : null;
                    if (prefab == null)
                    {
                        WearableTorchesPlugin.Log.LogWarning($"Back torch: prefab '{prefabName}' not found in ZNetScene");
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

        // helper: get ZNetView from Player
        private static ZNetView GetNView(Player player)
        {
            if (player == null)
                return null;

            return player.GetComponent<ZNetView>();
        }

        // Reflection: Get hand items (used by owner & sync)
        private static ItemDrop.ItemData GetTorchInHands(Player player)
        {
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;

                var getRight = typeof(Player).GetMethod("GetRightItem", flags);
                var getLeft = typeof(Player).GetMethod("GetLeftItem", flags);

                var right = getRight != null ? (ItemDrop.ItemData)getRight.Invoke(player, null) : null;
                var left = getLeft != null ? (ItemDrop.ItemData)getLeft.Invoke(player, null) : null;

                if (IsTorch(right)) return right;
                if (IsTorch(left)) return left;

                return null;
            }
            catch (Exception e)
            {
                WearableTorchesPlugin.Log.LogError($"Reflection error: {e}");
                return null;
            }
        }

        private static bool IsTorch(ItemDrop.ItemData item)
        {
            if (item == null) return false;
            return (item.m_shared?.m_name ?? "").ToLower().Contains("torch");
        }

        // Try unequip torch from hands + play equip SFX via Player.UnequipItem
        private static void TryUnequipTorchFromHands(Player player, ItemDrop.ItemData torchItem)
        {
            if (player == null || torchItem == null)
                return;

            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                // 1) Try UnequipItem(ItemDrop.ItemData, bool triggerEffects)
                var miBool = typeof(Player).GetMethod(
                    "UnequipItem",
                    flags,
                    null,
                    new Type[] { typeof(ItemDrop.ItemData), typeof(bool) },
                    null
                );

                if (miBool != null)
                {
                    miBool.Invoke(player, new object[] { torchItem, true });
                    WearableTorchesPlugin.Log.LogInfo("UnequipItem(item, true) called for back torch");
                    return;
                }

                // 2) Fallback: UnequipItem(ItemDrop.ItemData)
                var miSimple = typeof(Player).GetMethod(
                    "UnequipItem",
                    flags,
                    null,
                    new Type[] { typeof(ItemDrop.ItemData) },
                    null
                );

                if (miSimple != null)
                {
                    miSimple.Invoke(player, new object[] { torchItem });
                    WearableTorchesPlugin.Log.LogInfo("UnequipItem(item) called for back torch");
                    return;
                }

                WearableTorchesPlugin.Log.LogWarning("Player.UnequipItem not found via reflection");
            }
            catch (Exception e)
            {
                WearableTorchesPlugin.Log.LogError($"TryUnequipTorchFromHands error: {e}");
            }
        }

        // Create visual torch on back (no network, no loot)
        private static GameObject CreateBackTorchVisual(Player player, GameObject prefab)
        {
            if (prefab == null)
                return null;

            var attach = FindBackAttach(player);

            var backTorch = new GameObject("BackTorchObject");
            backTorch.transform.SetParent(attach, false);

            backTorch.transform.localRotation = BackRotationOffset;
            backTorch.transform.localPosition = new Vector3(-0.0026f, -0.002f, -0.0017f);

            // fix scaling
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

            // flames: try from player → fallback to prefab
            GameObject fxPrefab = FindTorchFlamesObject(player);
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
            light.color = new Color(1f, 0.50f, 0.20f);
            light.range = 11f;
            light.intensity = 1.2f;
            light.shadows = LightShadows.Soft;

            return backTorch;
        }

        #region Finder

        // Locate back bone
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

        // Find FX in player's hierarchy (old behavior)
        private static GameObject FindTorchFlamesObject(Player player)
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

        // Find FX in torch prefab (ParticleSystem without mesh)
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

        // Destroy all visuals
        private static void DestroyBackTorch()
        {
            foreach (var kvp in BackTorches)
            {
                if (kvp.Value != null)
                    UnityEngine.Object.Destroy(kvp.Value);
            }
            BackTorches.Clear();
        }

        public static void ForceDestroyBackTorch() => DestroyBackTorch();
    }
}
