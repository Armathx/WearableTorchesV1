using System;
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
            var player = Player.m_localPlayer;
            if (player == null || ZInput.instance == null)
                return;

            if (ZInput.GetKeyDown(KeyCode.G))
                WearableTorchManager.OnBackTorchKeyPressed(player);
        }

        private void OnDestroy()
        {
            WearableTorchManager.ForceDestroyBackTorch();
        }
    }

    public static class WearableTorchManager
    {
        private static GameObject _backTorchObject;

        private static readonly Vector3 FlameOffset = new Vector3(0f, 0f, 0.61f);
        private static readonly Vector3 LightOffset = new Vector3(0f, 0f, 0.61f);

        private static readonly Quaternion BackRotationOffset = Quaternion.Euler(-80f, 0f, 0f);

        // Toggle G
        public static void OnBackTorchKeyPressed(Player player)
        {
            if (_backTorchObject != null)
            {
                DestroyBackTorch();
                return;
            }

            var torchItem = GetTorchInHands(player);
            if (torchItem == null)
            {
                WearableTorchesPlugin.Log.LogInfo("No torch in hands");
                return;
            }

            EquipBackTorchFromItem(player, torchItem);
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

        // Equip torch on back
        private static void EquipBackTorchFromItem(Player player, ItemDrop.ItemData item)
        {
            var prefab = item.m_dropPrefab;
            if (prefab == null)
            {
                WearableTorchesPlugin.Log.LogWarning("Prefab is null");
                return;
            }

            var attach = FindBackAttach(player);

            DestroyBackTorch();

            // ***** FIX: we do NOT instantiate the prefab *****
            _backTorchObject = new GameObject("BackTorchObject");
            _backTorchObject.transform.SetParent(attach, false);

            _backTorchObject.transform.localRotation = BackRotationOffset;
            _backTorchObject.transform.localPosition = new Vector3(-0.0024f, -0.002f, -0.0017f);

            // Fix scaling
            Vector3 ps = attach.lossyScale;
            _backTorchObject.transform.localScale = new Vector3(
                ps.x != 0 ? 1f / ps.x : 1f,
                ps.y != 0 ? 1f / ps.y : 1f,
                ps.z != 0 ? 1f / ps.z : 1f
            );

            // Copy mesh from prefab → but never instantiate ItemDrop
            foreach (var renderer in prefab.GetComponentsInChildren<MeshRenderer>(true))
            {
                var clone = UnityEngine.Object.Instantiate(renderer.gameObject, _backTorchObject.transform, false);
                clone.name = "BackTorchMesh";
            }

            // Flames
            var fx = FindTorchFlamesObject(player);
            if (fx != null)
            {
                var clone = UnityEngine.Object.Instantiate(fx, _backTorchObject.transform, false);
                clone.name = "BackTorchFlames";
                clone.transform.localRotation = Quaternion.identity;
                clone.transform.localPosition = FlameOffset;
            }

            // Light
            var lightGO = new GameObject("BackTorchLight");
            lightGO.transform.SetParent(_backTorchObject.transform, false);
            lightGO.transform.localRotation = Quaternion.identity;
            lightGO.transform.localPosition = LightOffset;

            var light = lightGO.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.55f, 0.25f);
            light.range = 15f;
            light.intensity = 1.9f;
            light.shadows = LightShadows.Soft;
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

        // Destroy
        private static void DestroyBackTorch()
        {
            if (_backTorchObject == null)
                return;

            UnityEngine.Object.Destroy(_backTorchObject);
            _backTorchObject = null;
        }

        public static void ForceDestroyBackTorch() => DestroyBackTorch();
    }
}
