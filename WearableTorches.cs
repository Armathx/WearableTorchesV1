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
            _harmony.PatchAll(); // pour plus tard si tu veux d’autres patchs
            Log.LogInfo("Wearable Torches loaded (Awake)");
        }

        private void Update()
        {
            var player = Player.m_localPlayer;
            if (player == null)
                return;

            // Utilisation de ZInput (Valheim) pour la touche G
            if (ZInput.instance != null && ZInput.GetKeyDown(KeyCode.G))
            {
                Log.LogInfo("Touche G pressée, gestion de la torche dans le dos.");
                WearableTorchManager.OnBackTorchKeyPressed(player);
            }
        }
    }

    // --------------------------------------------------------
    // Gestion de la torche dans le dos
    // --------------------------------------------------------
    public static class WearableTorchManager
    {
        private static GameObject _backTorchObject; // parent : mesh + FX + light

        // Offsets en ESPACE LOCAL DE LA TORCHE DANS LE DOS
        // (tu peux ajuster ces valeurs facilement)
        private static readonly Vector3 FlameOffset = new Vector3(0f, 0.32f, 0.0f);
        private static readonly Vector3 LightOffset = new Vector3(0f, 0.32f, 0.0f);

        // Rotation additionnelle appliquée à la rotation de la torche en main
        // pour l'adapter à la position dans le dos.
        private static readonly Quaternion BackRotationOffset = Quaternion.Euler(-30f, 30f, 0f);

        // ----------------------------------------------------
        // Appelé quand on appuie sur G
        // ----------------------------------------------------
        public static void OnBackTorchKeyPressed(Player player)
        {
            if (player == null)
                return;

            // Si une torche est déjà sur le dos, on la retire (toggle)
            if (_backTorchObject != null)
            {
                WearableTorchesPlugin.Log.LogInfo("G pressé : torche déjà sur le dos, on la détruit.");
                DestroyBackTorch();
                return;
            }

            // On cherche une torche actuellement en main (droite ou gauche)
            ItemDrop.ItemData torchItem = GetTorchInHands(player);
            if (torchItem == null)
            {
                WearableTorchesPlugin.Log.LogInfo("G pressé : aucune torche en main, rien à faire.");
                return;
            }

            EquipBackTorchFromItem(player, torchItem);
        }

        // ----------------------------------------------------
        // Récupère l'ItemData d'une torche dans les mains
        // (via réflexion sur GetRightItem / GetLeftItem)
        // ----------------------------------------------------
        private static ItemDrop.ItemData GetTorchInHands(Player player)
        {
            try
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;

                MethodInfo getRightItem = typeof(Player).GetMethod("GetRightItem", flags);
                MethodInfo getLeftItem = typeof(Player).GetMethod("GetLeftItem", flags);

                ItemDrop.ItemData right = null;
                ItemDrop.ItemData left = null;

                if (getRightItem != null)
                    right = (ItemDrop.ItemData)getRightItem.Invoke(player, null);

                if (getLeftItem != null)
                    left = (ItemDrop.ItemData)getLeftItem.Invoke(player, null);

                if (IsTorch(right)) return right;
                if (IsTorch(left)) return left;

                return null;
            }
            catch (Exception e)
            {
                WearableTorchesPlugin.Log.LogError($"GetTorchInHands : erreur réflexion : {e}");
                return null;
            }
        }

        private static bool IsTorch(ItemDrop.ItemData item)
        {
            if (item == null) return false;
            string itemName = item.m_shared?.m_name ?? "";
            return itemName.ToLower().Contains("torch");
        }

        // ----------------------------------------------------
        // Crée la torche de dos :
        // - mesh depuis m_dropPrefab
        // - rotation basée sur la torche en main (+ offset)
        // - flammes clonées depuis la torche en main
        // - light créée à la main
        // ----------------------------------------------------
        private static void EquipBackTorchFromItem(Player player, ItemDrop.ItemData item)
        {
            if (item == null)
            {
                WearableTorchesPlugin.Log.LogWarning("EquipBackTorchFromItem : item null.");
                return;
            }

            // 1) Mesh propre depuis le prefab
            GameObject prefab = item.m_dropPrefab;
            if (prefab == null)
            {
                WearableTorchesPlugin.Log.LogWarning("EquipBackTorchFromItem : m_dropPrefab null.");
                return;
            }

            // 2) Point d’attache
            Transform attach = player.transform;

            Transform spline = player.transform.Find("BackTorchSpline");
            Transform spine = player.transform.Find("Visual/Armature/Hips/Spine");
            Transform hips = player.transform.Find("Visual/Armature/Hips");

            if (spline != null)
            {
                attach = spline;
                WearableTorchesPlugin.Log.LogInfo("Back torch attach = BackTorchSpline");
            }
            else if (spine != null)
            {
                attach = spine;
                WearableTorchesPlugin.Log.LogInfo("Back torch attach = Spine");
            }
            else if (hips != null)
            {
                attach = hips;
                WearableTorchesPlugin.Log.LogInfo("Back torch attach = Hips");
            }
            else
            {
                WearableTorchesPlugin.Log.LogWarning("Hips/Spine introuvables, attache sur le root du player.");
            }

            // 3) On détruit un éventuel ancien clone
            if (_backTorchObject != null)
            {
                UnityEngine.Object.Destroy(_backTorchObject);
                _backTorchObject = null;
            }

            // 4) On instancie le mesh du prefab comme objet principal
            _backTorchObject = UnityEngine.Object.Instantiate(prefab, attach, false);
            _backTorchObject.name = "BackTorchObject";

            // 5) Récupérer la rotation de la torche en main pour orienter celle du dos
            Transform handTorchRoot = FindHandTorchRoot(player);
            if (handTorchRoot != null)
            {
                // même rotation locale que la torche en main, + offset pour la position dans le dos
                _backTorchObject.transform.localRotation = handTorchRoot.localRotation * BackRotationOffset;
            }
            else
            {
                // fallback : simple rotation d’offset
                _backTorchObject.transform.localRotation = BackRotationOffset;
            }

            // Position sur le dos (offset dans l'espace local du bone / spline)
            _backTorchObject.transform.localPosition = new Vector3(-0.002f, 0.0f, 0.0022f);

            // Neutralise le scale minuscule du bone
            Vector3 parentScale = attach.lossyScale;
            Vector3 invScale = new Vector3(
                parentScale.x != 0f ? 1f / parentScale.x : 1f,
                parentScale.y != 0f ? 1f / parentScale.y : 1f,
                parentScale.z != 0f ? 1f / parentScale.z : 1f
            );
            _backTorchObject.transform.localScale = invScale;

            // 6) On supprime ce qui sert au loot/physique sur le mesh
            var drop = _backTorchObject.GetComponent<ItemDrop>();
            if (drop != null) drop.enabled = false;

            var rb = _backTorchObject.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = true;
                rb.useGravity = false;
            }

            foreach (var col in _backTorchObject.GetComponentsInChildren<Collider>(true))
                col.enabled = false;

            // 7) On ajoute les FLAMMES en clonant l'objet FX de la torche en main
            GameObject flamesSource = FindTorchFlamesObject(player);
            if (flamesSource != null)
            {
                var flamesClone = UnityEngine.Object.Instantiate(flamesSource, _backTorchObject.transform, false);
                flamesClone.name = "BackTorchFlames";

                // Placement et rotation dans l’espace local de la torche du dos
                flamesClone.transform.localPosition = FlameOffset;
                flamesClone.transform.localRotation = flamesSource.transform.localRotation;

                WearableTorchesPlugin.Log.LogInfo("Flammes clonées depuis la torche en main.");
            }
            else
            {
                WearableTorchesPlugin.Log.LogWarning("FindTorchFlamesObject: aucune source de flammes trouvée, pas de FX feu.");
            }

            // 8) On crée une LIGHT dédiée (dans l’espace local de la torche)
            GameObject lightGO = new GameObject("BackTorchLight");
            lightGO.transform.SetParent(_backTorchObject.transform, false);
            lightGO.transform.localPosition = LightOffset;

            Light l = lightGO.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = new Color(1.0f, 0.3f, 0.2f);
            l.range = 15f;
            l.intensity = 1.8f;
            l.shadows = LightShadows.Soft;

            WearableTorchesPlugin.Log.LogInfo("Light créée pour la torche dans le dos.");

            // 9) On s’assure que tout le visuel est actif
            _backTorchObject.SetActive(true);
            foreach (var r in _backTorchObject.GetComponentsInChildren<Renderer>(true))
            {
                r.enabled = true;
                r.gameObject.SetActive(true);
            }

            WearableTorchesPlugin.Log.LogInfo("Torche de dos EQUIPÉE : mesh + flammes + light (via touche G).");
        }

        // ----------------------------------------------------
        // Cherche le "root" mesh de la torche en main
        // (sert à copier sa rotation locale)
        // ----------------------------------------------------
        private static Transform FindHandTorchRoot(Player player)
        {
            Transform[] all = player.GetComponentsInChildren<Transform>(true);

            foreach (Transform t in all)
            {
                string n = t.name.ToLower();
                if (!n.Contains("torch"))
                    continue;

                MeshRenderer meshRenderer = t.GetComponentInChildren<MeshRenderer>(true);
                SkinnedMeshRenderer skinned = t.GetComponentInChildren<SkinnedMeshRenderer>(true);
                bool hasMesh = (meshRenderer != null || skinned != null);

                if (!hasMesh)
                    continue;

                WearableTorchesPlugin.Log.LogInfo($"FindHandTorchRoot -> {t.name}");
                return t;
            }

            WearableTorchesPlugin.Log.LogWarning("FindHandTorchRoot: aucun root de torche (mesh) trouvé.");
            return null;
        }

        // ----------------------------------------------------
        // Recherche de l'objet qui porte les flammes sur la torche en main
        // (on veut un objet avec ParticleSystem mais SANS mesh)
        // ----------------------------------------------------
        private static GameObject FindTorchFlamesObject(Player player)
        {
            Transform[] all = player.GetComponentsInChildren<Transform>(true);

            foreach (Transform t in all)
            {
                string n = t.name.ToLower();
                if (!n.Contains("torch"))
                    continue;

                ParticleSystem ps = t.GetComponentInChildren<ParticleSystem>(true);
                if (ps == null)
                    continue;

                // On évite les objets qui ont déjà un mesh, on veut surtout le FX
                MeshRenderer meshRenderer = t.GetComponentInChildren<MeshRenderer>(true);
                SkinnedMeshRenderer skinned = t.GetComponentInChildren<SkinnedMeshRenderer>(true);
                bool hasMesh = (meshRenderer != null || skinned != null);

                if (hasMesh)
                    continue;

                WearableTorchesPlugin.Log.LogInfo($"FindTorchFlamesObject -> {t.name}");
                return t.gameObject;
            }

            WearableTorchesPlugin.Log.LogWarning("FindTorchFlamesObject: aucune transform 'torch' avec uniquement des ParticleSystem trouvée.");
            return null;
        }

        // ----------------------------------------------------
        // Destruction du clone de dos
        // ----------------------------------------------------
        private static void DestroyBackTorch()
        {
            if (_backTorchObject != null)
            {
                UnityEngine.Object.Destroy(_backTorchObject);
                _backTorchObject = null;
                WearableTorchesPlugin.Log.LogInfo("Torche de dos DÉTRUITE.");
            }
        }
    }

    // --------------------------------------------------------
    // IMPORTANT :
    // On NE patch plus Player.EquipItem ici → plus d’erreur
    // "Undefined target method".
    // --------------------------------------------------------
}
