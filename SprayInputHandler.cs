// SprayInputHandler.cs - Handles keybind input for spray wheel and quick sprays
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace SprayMod
{
    /// <summary>
    /// Handles keybind detection for opening spray wheel and quick spray binds
    /// Attached to the SprayManager GameObject
    /// </summary>
    public class SprayInputHandler : MonoBehaviour
    {
        private SprayClientConfig _config;
        private SprayWheelUI _sprayWheel;
        private Action _onOpenWheel;
        private Action<int> _onQuickSpray;

        // Track cooldown for key presses
        private float _lastKeyTime = 0f;
        private const float KEY_COOLDOWN = 0.15f;

        public void Initialize(SprayWheelUI wheel, Action openWheelCallback, Action<int> quickSprayCallback)
        {
            _sprayWheel = wheel;
            _onOpenWheel = openWheelCallback;
            _onQuickSpray = quickSprayCallback;
            ReloadConfig();
            Debug.Log("[SprayInputHandler] Initialized");
        }
        
        public void ReloadConfig()
        {
            _config = SprayConfigManager.LoadClientConfig();
            if (_config.Debug)
                Debug.Log($"[SprayInputHandler] Config reloaded - WheelKey: {_config.SprayWheelKey}, QuickBinds: {_config.QuickSprayBinds?.Count ?? 0}");
        }
        
        private void Update()
        {
            if (_config == null) return;
            
            // Don't process input if in a menu, chat, or UI
            if (IsInMenuOrChat()) return;
            
            // Cooldown check
            if (Time.unscaledTime - _lastKeyTime < KEY_COOLDOWN) return;
            
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            
            // Check spray wheel key - toggle behavior
            if (!string.IsNullOrEmpty(_config.SprayWheelKey))
            {
                if (IsKeyPressed(_config.SprayWheelKey, kb, mouse))
                {
                    if (_config.Debug)
                        Debug.Log($"[SprayInputHandler] Spray wheel key '{_config.SprayWheelKey}' pressed!");
                    _lastKeyTime = Time.unscaledTime;
                    
                    // Toggle: if wheel is visible, hide it; otherwise show it
                    if (_sprayWheel != null && _sprayWheel.IsVisible())
                    {
                        _sprayWheel.Hide();
                    }
                    else
                    {
                        _onOpenWheel?.Invoke();
                    }
                    return;
                }
            }
            
            // Check quick spray binds
            if (_config.QuickSprayBinds != null && _config.QuickSprayBinds.Count > 0)
            {
                foreach (var kvp in _config.QuickSprayBinds)
                {
                    if (IsKeyPressed(kvp.Key, kb, mouse))
                    {
                        _lastKeyTime = Time.unscaledTime;
                        int idx = SprayManager.Instance != null ? SprayManager.Instance.IndexOfSource(kvp.Value) : -1;
                        if (_config.Debug)
                            Debug.Log($"[SprayInputHandler] Quick spray key '{kvp.Key}' -> '{kvp.Value}' (index {idx})");
                        if (idx >= 0) _onQuickSpray?.Invoke(idx);
                        return;
                    }
                }
            }
        }
        
        private bool IsInMenuOrChat()
        {
            try
            {
                // Don't block when spray wheel is open - we want toggle to work
                if (_sprayWheel != null && _sprayWheel.IsVisible())
                    return false;
                
                // Check if chat is focused (UIChat.IsFocused set by StartInput/StopInput)
                var uiMgr = MonoBehaviourSingleton<UIManager>.Instance;
                if (uiMgr != null && uiMgr.Chat != null && uiMgr.Chat.IsFocused) return true;

                // A menu/overlay that needs the mouse is open (b897 replacement for isMouseActive)
                if (GlobalStateManager.UIState.IsMouseRequired) return true;

                // Check if cursor is unlocked (typically means in a menu)
                if (Cursor.lockState != CursorLockMode.Locked)
                    return true;
                    
                return false;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Check if a keybind spec is pressed this frame. Understands the chord format produced
        /// by the settings capture UI (see SpraySettingsUI.KeyChordToSpec): optional
        /// "Ctrl+"/"Shift+"/"Alt+" modifier prefixes, plus one or more key tokens joined by '+'.
        /// The required modifiers and all leading tokens must be held; the final token must have
        /// been pressed this frame, so the bind fires exactly once on the last keystroke.
        /// Supports letters, digits (Alpha/Digit#), Keypad/Numpad#, F1-F12, mouse buttons
        /// (Mouse0-5, MB4/MB5, LMB/RMB/MMB), arrows and punctuation keys.
        /// </summary>
        private bool IsKeyPressed(string keyName, Keyboard kb, Mouse mouse)
        {
            if (string.IsNullOrEmpty(keyName)) return false;

            bool reqCtrl = false, reqShift = false, reqAlt = false;
            var tokens = new List<string>();
            foreach (var rawPart in keyName.Split('+'))
            {
                string part = rawPart.Trim();
                if (part.Length == 0) continue;
                switch (part.ToUpperInvariant())
                {
                    case "CTRL": case "CONTROL": case "LCTRL": case "RCTRL": reqCtrl = true; break;
                    case "SHIFT": case "LSHIFT": case "RSHIFT": reqShift = true; break;
                    case "ALT": case "LALT": case "RALT": reqAlt = true; break;
                    default: tokens.Add(part); break;
                }
            }
            if (tokens.Count == 0) return false;

            // Required modifiers must currently be held.
            if (reqCtrl && !(kb != null && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed))) return false;
            if (reqShift && !(kb != null && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed))) return false;
            if (reqAlt && !(kb != null && (kb.leftAltKey.isPressed || kb.rightAltKey.isPressed))) return false;

            // All tokens but the last must be held; the last must have been pressed this frame.
            for (int i = 0; i < tokens.Count - 1; i++)
            {
                var held = ResolveControl(tokens[i], kb, mouse);
                if (held == null || !held.isPressed) return false;
            }
            var trigger = ResolveControl(tokens[tokens.Count - 1], kb, mouse);
            return trigger != null && trigger.wasPressedThisFrame;
        }

        /// <summary>
        /// Resolves a single key/mouse token to its input control (or null if unknown).
        /// KeyControl derives from ButtonControl, so keyboard and mouse buttons share one return type.
        /// </summary>
        private ButtonControl ResolveControl(string token, Keyboard kb, Mouse mouse)
        {
            if (string.IsNullOrEmpty(token)) return null;
            string k = token.ToUpperInvariant().Trim();

            // Mouse buttons (Mouse0-5, MB4/MB5, LMB/RMB/MMB and friendly aliases).
            if (k.StartsWith("MOUSE") || k.StartsWith("MB") ||
                k == "LMB" || k == "RMB" || k == "MMB" ||
                k == "LEFT BUTTON" || k == "RIGHT BUTTON" || k == "MIDDLE BUTTON" ||
                k == "FORWARD" || k == "BACK")
            {
                if (mouse == null) return null;
                switch (k)
                {
                    case "MOUSE0": case "LMB": case "LEFT BUTTON": case "LEFTBUTTON": return mouse.leftButton;
                    case "MOUSE1": case "RMB": case "RIGHT BUTTON": case "RIGHTBUTTON": return mouse.rightButton;
                    case "MOUSE2": case "MMB": case "MIDDLE BUTTON": case "MIDDLEBUTTON": return mouse.middleButton;
                    // Mouse3/Mouse4/MB4 = forward button (matches the capture UI's GetFriendlyKeyName).
                    case "MOUSE3": case "MOUSE4": case "MB4": case "FORWARD": case "FORWARDBUTTON": return mouse.forwardButton;
                    // Mouse5/MB5 = back button.
                    case "MOUSE5": case "MB5": case "BACK": case "BACKBUTTON": return mouse.backButton;
                    default: return null;
                }
            }

            if (kb == null) return null;

            // Letters (A-Z).
            if (k.Length == 1 && char.IsLetter(k[0])) return GetLetterKey(kb, k[0]);
            // Top-row digits (0-9).
            if (k.Length == 1 && char.IsDigit(k[0])) return GetNumberKey(kb, k[0]);
            // "Digit1" / "Alpha1".
            if ((k.StartsWith("DIGIT") || k.StartsWith("ALPHA")) && k.Length == 6 && char.IsDigit(k[5]))
                return GetNumberKey(kb, k[5]);
            // "Keypad1" (capture format) / "Numpad1".
            if ((k.StartsWith("KEYPAD") || k.StartsWith("NUMPAD")) && k.Length == 7 && char.IsDigit(k[6]))
                return GetNumpadKey(kb, k[6]);
            // Function keys F1-F12.
            if (k.StartsWith("F") && k.Length <= 3 && int.TryParse(k.Substring(1), out int fNum) && fNum >= 1 && fNum <= 12)
                return GetFunctionKey(kb, fNum);

            switch (k)
            {
                case "SPACE": return kb.spaceKey;
                case "TAB": return kb.tabKey;
                case "ENTER": case "RETURN": return kb.enterKey;
                case "BACKQUOTE": case "GRAVE": case "`": case "TILDE": case "~": return kb.backquoteKey;
                case "MINUS": case "-": case "HYPHEN": return kb.minusKey;
                case "EQUALS": case "=": case "PLUS": return kb.equalsKey;
                case "LEFTBRACKET": case "[": case "OPENBRACKET": return kb.leftBracketKey;
                case "RIGHTBRACKET": case "]": case "CLOSEBRACKET": return kb.rightBracketKey;
                case "BACKSLASH": case "\\": case "PIPE": return kb.backslashKey;
                case "SEMICOLON": case ";": return kb.semicolonKey;
                case "QUOTE": case "'": case "APOSTROPHE": return kb.quoteKey;
                case "COMMA": case ",": return kb.commaKey;
                case "PERIOD": case ".": return kb.periodKey;
                case "SLASH": case "/": return kb.slashKey;
                case "UPARROW": case "UP": return kb.upArrowKey;
                case "DOWNARROW": case "DOWN": return kb.downArrowKey;
                case "LEFTARROW": case "LEFT": return kb.leftArrowKey;
                case "RIGHTARROW": case "RIGHT": return kb.rightArrowKey;
                default: return null;
            }
        }
        
        private KeyControl GetLetterKey(Keyboard kb, char letter)
        {
            switch (char.ToUpperInvariant(letter))
            {
                case 'A': return kb.aKey;
                case 'B': return kb.bKey;
                case 'C': return kb.cKey;
                case 'D': return kb.dKey;
                case 'E': return kb.eKey;
                case 'F': return kb.fKey;
                case 'G': return kb.gKey;
                case 'H': return kb.hKey;
                case 'I': return kb.iKey;
                case 'J': return kb.jKey;
                case 'K': return kb.kKey;
                case 'L': return kb.lKey;
                case 'M': return kb.mKey;
                case 'N': return kb.nKey;
                case 'O': return kb.oKey;
                case 'P': return kb.pKey;
                case 'Q': return kb.qKey;
                case 'R': return kb.rKey;
                case 'S': return kb.sKey;
                case 'T': return kb.tKey;
                case 'U': return kb.uKey;
                case 'V': return kb.vKey;
                case 'W': return kb.wKey;
                case 'X': return kb.xKey;
                case 'Y': return kb.yKey;
                case 'Z': return kb.zKey;
                default: return null;
            }
        }
        
        private KeyControl GetNumberKey(Keyboard kb, char digit)
        {
            switch (digit)
            {
                case '0': return kb.digit0Key;
                case '1': return kb.digit1Key;
                case '2': return kb.digit2Key;
                case '3': return kb.digit3Key;
                case '4': return kb.digit4Key;
                case '5': return kb.digit5Key;
                case '6': return kb.digit6Key;
                case '7': return kb.digit7Key;
                case '8': return kb.digit8Key;
                case '9': return kb.digit9Key;
                default: return null;
            }
        }
        
        private KeyControl GetNumpadKey(Keyboard kb, char digit)
        {
            switch (digit)
            {
                case '0': return kb.numpad0Key;
                case '1': return kb.numpad1Key;
                case '2': return kb.numpad2Key;
                case '3': return kb.numpad3Key;
                case '4': return kb.numpad4Key;
                case '5': return kb.numpad5Key;
                case '6': return kb.numpad6Key;
                case '7': return kb.numpad7Key;
                case '8': return kb.numpad8Key;
                case '9': return kb.numpad9Key;
                default: return null;
            }
        }
        
        private KeyControl GetFunctionKey(Keyboard kb, int num)
        {
            switch (num)
            {
                case 1: return kb.f1Key;
                case 2: return kb.f2Key;
                case 3: return kb.f3Key;
                case 4: return kb.f4Key;
                case 5: return kb.f5Key;
                case 6: return kb.f6Key;
                case 7: return kb.f7Key;
                case 8: return kb.f8Key;
                case 9: return kb.f9Key;
                case 10: return kb.f10Key;
                case 11: return kb.f11Key;
                case 12: return kb.f12Key;
                default: return null;
            }
        }
    }
}
