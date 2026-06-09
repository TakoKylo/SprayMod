using System;
using System.IO;
using UnityEngine;
using Unity.Netcode;
using HarmonyLib;

namespace SprayMod
{
    /// <summary>
    /// Mod entry point. Implements b897's IPuckPlugin (was IPuckMod). The mod is now
    /// fully client-side: it loads a local spray library, renders decals, and shares
    /// placements peer-to-peer by reference over chat (see spraymod-b897-architecture).
    /// </summary>
    public class SprayMod : IPuckPlugin
    {
        private GameObject rootObject;            // hosts SprayManager + input handler
        private SprayManager sprayManager;
        private GameObject wheelObject;
        private SprayWheelUI sprayWheel;
        private GameObject settingsUIObject;
        private SpraySettingsUI settingsUI;
        private GameObject chatSyncObject;
        private SprayInputHandler inputHandler;
        private Harmony harmony;

        private bool libraryLoaded;
        private bool libraryLoading;

        public bool OnEnable()
        {
            try
            {
                SprayUtilities.DebugLog("Enabling SprayMod...");

                // Link-first: the default sprays are preset links (seeded into sprays.json by
                // SprayConfigManager on first load). We only need to seed the spray sound here.
                SprayUtilities.SeedBundledSound();

                CreateManager();
                CreateChatSync();
                CreateWheel();
                CreateSettingsUI();
                CreateInputHandler();
                RegisterPatches();

                // Load the library lag-free; initialise the wheel callbacks when done.
                ReloadLibrary();

                Debug.Log("[SprayMod] Enabled. Type '/spray' or press the spray key to open the wheel.");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[SprayMod] Failed to enable: {e.Message}\n{e.StackTrace}");
                return false;
            }
        }

        public bool OnDisable()
        {
            try
            {
                SprayUtilities.DebugLog("Disabling SprayMod...");

                if (harmony != null) { harmony.UnpatchSelf(); harmony = null; }

                if (sprayWheel != null) sprayWheel.Hide();
                if (sprayManager != null) sprayManager.ClearAllSprays();

                DestroyObject(ref settingsUIObject); settingsUI = null;
                DestroyObject(ref wheelObject); sprayWheel = null;
                DestroyObject(ref chatSyncObject);
                DestroyObject(ref rootObject); sprayManager = null; inputHandler = null;

                Debug.Log("[SprayMod] Disabled.");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[SprayMod] Failed to disable: {e.Message}");
                return false;
            }
        }

        // ---- setup ----

        private void CreateManager()
        {
            sprayManager = UnityEngine.Object.FindFirstObjectByType<SprayManager>();
            if (sprayManager == null)
            {
                rootObject = new GameObject("SprayManager");
                UnityEngine.Object.DontDestroyOnLoad(rootObject);
                sprayManager = rootObject.AddComponent<SprayManager>();
            }
            else
            {
                rootObject = sprayManager.gameObject;
            }
        }

        private void CreateChatSync()
        {
            chatSyncObject = new GameObject("SprayChatSync");
            UnityEngine.Object.DontDestroyOnLoad(chatSyncObject);
            chatSyncObject.AddComponent<SprayChatSync>();
        }

        private void CreateWheel()
        {
            wheelObject = new GameObject("SprayWheelUI");
            UnityEngine.Object.DontDestroyOnLoad(wheelObject);
            sprayWheel = wheelObject.AddComponent<SprayWheelUI>();
            sprayWheel.Configure(
                onSelect: PerformSpray,
                onClear: ClearAllSpraysOnClient,
                onAddLink: OpenSpraySettingsLinks);
        }

        /// <summary>Opens the settings panel on the Sprays tab (where links are added/managed).</summary>
        private void OpenSpraySettingsLinks()
        {
            settingsUI?.OpenToSpraysTab();
        }

        private void CreateSettingsUI()
        {
            settingsUIObject = new GameObject("SpraySettingsUI");
            UnityEngine.Object.DontDestroyOnLoad(settingsUIObject);
            settingsUI = settingsUIObject.AddComponent<SpraySettingsUI>();
        }

        private void CreateInputHandler()
        {
            inputHandler = rootObject.AddComponent<SprayInputHandler>();
            inputHandler.Initialize(sprayWheel, ExecuteSpray, PerformSpray, ClearAllSpraysOnClient);
        }

        private void RegisterPatches()
        {
            harmony = new Harmony("com.spraymod.patch");
            SprayChatPatch.SetModInstance(this);

            // Patch each class independently (instead of PatchAll) so that if a single game method
            // is renamed/removed in a future update, that one feature degrades but the rest of the
            // mod - including local spray placement and rendering - still loads.
            SafePatch(typeof(SprayChatReceivePatch));     // receive + hide P2P spray-sync messages
            SafePatch(typeof(SprayChatPatch));            // intercept the /spray chat command
            SafePatch(typeof(SprayPausePatch));           // ESC closes our UI instead of opening pause
            SafePatch(typeof(SprayChatStartInputPatch));  // block chat (T/Y) while our UI is open
            SafePatch(typeof(SprayQuickChatPatch));       // block quick-chat while our UI is open
        }

        private void SafePatch(Type patchClass)
        {
            try { harmony.CreateClassProcessor(patchClass).Patch(); }
            catch (Exception e) { Debug.LogError($"[SprayMod] Patch failed for {patchClass.Name} (feature disabled): {e.Message}"); }
        }

        // ---- library ----

        public void ReloadLibrary(Action onComplete = null)
        {
            if (sprayManager == null || libraryLoading) return;
            libraryLoading = true;
            sprayManager.LoadLibrary(success =>
            {
                libraryLoaded = true;
                libraryLoading = false;
                SprayUtilities.DebugLog($"Library loaded: {sprayManager.LibraryCount} sprays");
                onComplete?.Invoke();
            });
        }

        // ---- commands / actions ----

        /// <summary>Opens (or toggles) the spray wheel; loads the library first if needed.</summary>
        public void ExecuteSpray()
        {
            try
            {
                if (sprayManager == null || sprayWheel == null) return;

                if (!libraryLoaded)
                {
                    if (!libraryLoading) ReloadLibrary(() => sprayWheel.Show());
                    return;
                }

                if (sprayWheel.IsVisible()) sprayWheel.Hide();
                else sprayWheel.Show();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SprayMod] ExecuteSpray error: {e.Message}");
            }
        }

        /// <summary>Places spray <paramref name="index"/> from the local library.</summary>
        public void PerformSpray(int index)
        {
            try
            {
                if (sprayManager == null) return;
                if (!libraryLoaded)
                {
                    if (!libraryLoading) ReloadLibrary();
                    return;
                }

                GetAimRay(out Vector3 origin, out Vector3 direction);
                sprayManager.RequestSpray(origin, direction, index);
            }
            catch (Exception e)
            {
                Debug.LogError($"[SprayMod] PerformSpray error: {e.Message}");
            }
        }

        /// <summary>Clears EVERY spray currently on this client (yours and other players').</summary>
        private void ClearAllSpraysOnClient()
        {
            sprayManager?.ClearAllSprays();
        }

        /// <summary>
        /// Determines the spray aim ray: from the local player's stick blade (preferred),
        /// else the main camera.
        /// </summary>
        private static void GetAimRay(out Vector3 origin, out Vector3 direction)
        {
            Player local = SprayManager.GetLocalPlayer();
            if (local != null && local.Stick != null && local.PlayerCamera != null)
            {
                origin = local.Stick.BladeHandlePosition;
                direction = (origin - local.PlayerCamera.transform.position).normalized;
                return;
            }

            Camera cam = Camera.main;
            if (cam != null)
            {
                origin = cam.transform.position;
                direction = cam.transform.forward;
                return;
            }

            origin = Vector3.zero;
            direction = Vector3.forward;
        }

        private static void DestroyObject(ref GameObject obj)
        {
            if (obj != null)
            {
                UnityEngine.Object.Destroy(obj);
                obj = null;
            }
        }
    }
}
