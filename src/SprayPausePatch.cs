using HarmonyLib;

namespace SprayMod
{
    /// <summary>True while any SprayMod overlay (spray wheel or settings panel) is open.</summary>
    internal static class SprayUiBlock
    {
        public static bool AnyOpen => SprayWheelUI.IsWheelOpen || SpraySettingsUI.IsSettingsOpen;
    }

    /// <summary>
    /// Block the game's chat input (T / Y) from opening while our UI is up - otherwise you could
    /// start typing in chat from behind/over the spray menu. The game only gates chat on
    /// UIState.IsInteracting (a UIView count we can't set), so we suppress it directly.
    /// </summary>
    [HarmonyPatch(typeof(UIChat), nameof(UIChat.StartInput))]
    public static class SprayChatStartInputPatch
    {
        static bool Prefix() => !SprayUiBlock.AnyOpen; // false = don't open chat while our UI is open
    }

    /// <summary>Block quick-chat key actions while our UI is up (same reasoning as chat input).</summary>
    [HarmonyPatch(typeof(ChatManager), nameof(ChatManager.Client_QuickChatAction))]
    public static class SprayQuickChatPatch
    {
        static bool Prefix() => !SprayUiBlock.AnyOpen;
    }


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
