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
            bool next = !current;
            zdo.Set(ZdoKeyBackTorch, next);

            WearableTorchesPlugin.Log.LogInfo($"Back torch state for {player.GetPlayerName()} = {next}");
        }

        // Sync per player
        public static void SyncBackTorchForPlayer(Player player)
        {
            var nview = GetNView(player);
            if (nview == null)
                return;

            var zdo = nview.GetZDO();
            if (zdo == null)
                return;

            bool hasBackTorch = zdo.GetBool(ZdoKeyBackTorch, false);

            BackTorches.TryGetValue(player, out var existing);

            if (hasBackTorch)
            {
                if (existing == null)
                {
                    // need visual
                    var torchItem = GetTorchInHands(player);
                    if (torchItem == null)
                    {
                        // no more torch equipped → clear flag if owner
                        if (nview.IsOwner())
                            zdo.Set(ZdoKeyBackTorch, false);
                        return;
                    }

                    var go = CreateBackTorchVisual(player, torchItem);
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

        // Reflection: Get hand items
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

        // Create visual torch on back (no network, no loot)
        private static GameObject CreateBackTorchVisual(Player player, ItemDrop.ItemData item)
        {
            var prefab = item.m_dropPrefab;
            if (prefab == null)
            {
                WearableTorchesPlugin.Log.LogWarning("Prefab is null");
                return null;
            }

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

            // mesh copy
            foreach (var renderer in prefab.GetComponentsInChildren<MeshRenderer>(true))
            {
                var cloneMesh = UnityEngine.Object.Instantiate(renderer.gameObject, backTorch.transform, false);
                cloneMesh.name = "BackTorchMesh";
            }

            // flames
            var fx = FindTorchFlamesObject(player);
            if (fx != null)
            {
                var cloneFx = UnityEngine.Object.Instantiate(fx, backTorch.transform, false);
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
            light.color = new Color(1f, 0.55f, 0.25f);
            light.range = 15f;
            light.intensity = 1.9f;
            light.shadows = LightShadows.Soft;

            return backTorch;
        }

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

        // Find FX (not mesh)
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

                return t.gameObject;
            }
            return null;
        }

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
