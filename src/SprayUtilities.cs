using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SprayMod
{
    /// <summary>Small helpers used across the mod.</summary>
    public static class SprayUtilities
    {
        private static SprayClientConfig _clientConfig;

        public static void SetConfig(SprayClientConfig client) => _clientConfig = client;

        /// <summary>The folder the mod DLL lives in (where shipped assets like default_sprays.json sit).</summary>
        public static string GetModDirectory()
        {
            try { return Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location); }
            catch { return null; }
        }

        /// <summary>Copies the bundled SpraySound.wav (next to the DLL) into the config folder if absent.</summary>
        public static void SeedBundledSound()
        {
            try
            {
                string modDir = GetModDirectory();
                if (string.IsNullOrEmpty(modDir)) return;
                string src = Path.Combine(modDir, "SpraySound.wav");
                if (!File.Exists(src)) return;

                string dest = SprayConfigManager.GetSpraySoundPath();
                if (string.IsNullOrEmpty(dest) || File.Exists(dest)) return;

                string destDir = Path.GetDirectoryName(dest);
                if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
                File.Copy(src, dest, false);
                Debug.Log($"[SprayMod] Seeded spray sound to: {dest}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SprayMod] Failed to seed spray sound: {e.Message}");
            }
        }

        /// <summary>Debug log that only fires when Debug is enabled in the client config.</summary>
        public static void DebugLog(string message)
        {
            if (_clientConfig?.Debug == true)
                Debug.Log($"[SprayMod] {message}");
        }

        // b897 replacement for the old UIManager.isMouseActive. Toggling "mouse required" makes the
        // game show the cursor and suspend gameplay input - what mod overlays need while they're open.
        public static bool GetGameMouseActive()
        {
            try { return GlobalStateManager.UIState.IsMouseRequired; }
            catch { return false; }
        }

        public static void SetGameMouseActive(bool active)
        {
            try { GlobalStateManager.SetUIState(new Dictionary<string, object> { { "isMouseRequired", active } }); }
            catch { }
        }
    }
}
