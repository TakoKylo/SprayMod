using HarmonyLib;

namespace SprayMod
{
    /// <summary>
    /// While a SprayMod overlay (the spray wheel or the settings panel) is open, ESC should close
    /// OUR UI rather than open the game's pause menu. The game binds ESC to its "Pause" action, which
    /// calls UIManager.OnPauseActionPerformed - we prefix that so the close AND the pause-suppression
    /// happen in the same input event (no same-frame race with our own Update handlers).
    ///
    /// Order matters: the settings panel can sit on top of the wheel, so close it first; a second
    /// ESC then closes the wheel; a third lets the game handle pause normally.
    /// </summary>
    [HarmonyPatch(typeof(UIManager), "OnPauseActionPerformed")]
    public static class SprayPausePatch
    {
        static bool Prefix()
        {
            try
            {
                if (SpraySettingsUI.IsSettingsOpen)
                {
                    SpraySettingsUI.Instance?.HandleEscape();
                    return false; // swallow - don't open the pause menu
                }
                if (SprayWheelUI.IsWheelOpen)
                {
                    SprayWheelUI.Instance?.Hide();
                    return false;
                }
            }
            catch { }
            return true; // nothing of ours open - let the game pause normally
        }
    }
}
