using HarmonyLib;
using System;

namespace SprayMod
{
    /// <summary>
    /// Intercepts the "/spray" chat command on the client (b897 routes outgoing chat
    /// through ChatManager.Client_SendChatMessage; the old UIChat method is gone).
    /// Swallows the command and opens the spray wheel instead of sending it.
    /// </summary>
    [HarmonyPatch(typeof(ChatManager), "Client_SendChatMessage")]
    public static class SprayChatPatch
    {
        private static SprayMod modInstance;

        public static void SetModInstance(SprayMod instance)
        {
            modInstance = instance;
        }

        static bool Prefix(string content)
        {
            try
            {
                string trimmed = content?.Trim();
                if (string.Equals(trimmed, "/spray", StringComparison.OrdinalIgnoreCase))
                {
                    SprayUtilities.DebugLog("'/spray' command intercepted");
                    modInstance?.ExecuteSpray();
                    return false; // don't send the command to the server
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError($"[SprayMod] Error in chat command patch: {e.Message}");
            }
            return true;
        }
    }
}
