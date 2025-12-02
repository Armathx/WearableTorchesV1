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
            Log.LogInfo("Wearable Torches loaded (Awake)");
        }
    }

    public static class WearableTorchManager
    {
        private static ItemDrop.ItemData _equippedTorch;
        private static GameObject _backTorchLight; //ptite point light
        private static GameObject _backTorchObject; //Mesh de torch

        public static void ToggleBackTorch(Player player, ItemDrop.ItemData item)
        {
            if (player == null || item == null)
            {
                WearableTorchesPlugin.Log.LogWarning("ToggleBackTorch: player ou item null");
                return;
            }

            WearableTorchesPlugin.Log.LogInfo($"ToggleBackTorch sur item : {item.m_shared.m_name}");

            if (_equippedTorch == item)
            {
                Unequip();
            }
            else
            {
                Equip(player, item);
            }
        }

        private static void Equip(Player player, ItemDrop.ItemData item)
        {
            _equippedTorch = item;

            // On essaie d'abord un bone du mesh plutôt que le root
            Transform attach = player.transform;

            Transform hips = player.transform.Find("Visual/Armature/Hips");
            Transform spine = player.transform.Find("Visual/Armature/Hips/Spine");

            if (spine != null)
            {
                attach = spine;
                WearableTorchesPlugin.Log.LogInfo("Attach = Spine");
            }
            else if (hips != null)
            {
                attach = hips;
                WearableTorchesPlugin.Log.LogInfo("Attach = Hips");
            }
            else
            {
                WearableTorchesPlugin.Log.LogWarning("Hips/Spine introuvables, attach sur root player.");
            }

            // ----- LIGHT -----
            if (_backTorchLight == null)
            {
                _backTorchLight = new GameObject("BackTorchLight");
            }

            _backTorchLight.transform.SetParent(attach, false);

            // Offset relativement petit, autour du bas du torse
            Vector3 localPos = new Vector3(-0.0015f, 0.0015f, 0.0025f);
            _backTorchLight.transform.localPosition = localPos;

            var light = _backTorchLight.GetComponent<Light>();
            if (light == null)
                light = _backTorchLight.AddComponent<Light>();

            light.type = LightType.Point;
            light.intensity = 1.5f;
            light.range = 15f;
            light.color = new Color(1f, 0.4f, 0.4f);
            light.shadows = LightShadows.Soft;

            // ----- OBJET VISUEL AU MÊME ENDROIT -----
            if (_backTorchObject != null)
            {
                Object.Destroy(_backTorchObject);
                _backTorchObject = null;
            }

            if (item.m_dropPrefab != null)
            {
                GameObject prefab = item.m_dropPrefab;
                WearableTorchesPlugin.Log.LogInfo($"Instanciation prefab de torche : {prefab.name}");

                _backTorchObject = Object.Instantiate(prefab, attach);
                _backTorchObject.name = "BackTorchObject";

                _backTorchObject.transform.localPosition = localPos;
                _backTorchObject.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);

                // >>> COMPENSATION DE SCALE DU BONE <<<
                Vector3 parentScale = attach.lossyScale;
                Vector3 invScale = new Vector3(
                    parentScale.x != 0f ? 1f / parentScale.x : 1f,
                    parentScale.y != 0f ? 1f / parentScale.y : 1f,
                    parentScale.z != 0f ? 1f / parentScale.z : 1f
                );
                _backTorchObject.transform.localScale = invScale;

                // Désactiver loot + physique
                var itemDropComp = _backTorchObject.GetComponent<ItemDrop>();
                if (itemDropComp != null) itemDropComp.enabled = false;

                var rb = _backTorchObject.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.isKinematic = true;
                    rb.useGravity = false;
                }

                foreach (var c in _backTorchObject.GetComponentsInChildren<Collider>(true))
                    c.enabled = false;

                _backTorchObject.SetActive(true);
                foreach (var r in _backTorchObject.GetComponentsInChildren<Renderer>(true))
                {
                    r.enabled = true;
                    r.gameObject.SetActive(true);
                }
            }
            else
            {
                WearableTorchesPlugin.Log.LogWarning("item.m_dropPrefab est null, pas de visuel de torche.");
            }

            WearableTorchesPlugin.Log.LogInfo(
                $"Torche EQUIPÉE (attach={attach.name}, worldPos={_backTorchLight.transform.position})");
        }

        private static void Unequip()
        {
            _equippedTorch = null;

            if (_backTorchLight != null)
            {
                Object.Destroy(_backTorchLight);
                _backTorchLight = null;
            }

            if (_backTorchObject != null)
            {
                Object.Destroy(_backTorchObject);
                _backTorchObject = null;
            }

            WearableTorchesPlugin.Log.LogInfo("Torche de dos DÉSEQUIPÉE (lumière + mesh détruits).");
        }
    }

    // Patch : clic droit sur un item de l’inventaire
    [HarmonyPatch(typeof(InventoryGui), "OnRightClickItem")]
    public static class InventoryGui_OnRightClickItem_Patch
    {
        static bool Prefix(InventoryGui __instance, InventoryGrid grid, ItemDrop.ItemData item)
        {
            // Log pour vérifier que le patch est bien appelé
            string itemName = item != null ? item.m_shared.m_name : "NULL";
            WearableTorchesPlugin.Log.LogInfo($"OnRightClickItem appelé sur : {itemName}");

            if (item == null)
                return true; // rien à faire

            // On ne s’occupe que des torches
            var name = item.m_shared?.m_name ?? "";
            if (!name.ToLower().Contains("torch"))
            {
                // pas une torche -> comportement normal de Valheim
                return true;
            }

            var player = Player.m_localPlayer;
            if (player == null)
            {
                WearableTorchesPlugin.Log.LogWarning("Player.m_localPlayer est null dans OnRightClickItem.");
                return true;
            }

            // Notre logique d’équipement / déséquipement
            WearableTorchManager.ToggleBackTorch(player, item);

            // On bloque le comportement normal du clic droit
            return false;
        }
    }
}
