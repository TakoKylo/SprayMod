using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;
using UITK = UnityEngine.UIElements;

namespace SprayMod
{
    // KeyChord struct matching DashFall
    public struct KeyChord : IEquatable<KeyChord>
    {
        public KeyCode[] Keys;
        public bool Ctrl, Shift, Alt;

        public bool Equals(KeyChord other)
        {
            if (Ctrl != other.Ctrl || Shift != other.Shift || Alt != other.Alt) return false;
            if (Keys == null && other.Keys == null) return true;
            if (Keys == null || other.Keys == null) return false;
            if (Keys.Length != other.Keys.Length) return false;
            for (int i = 0; i < Keys.Length; i++)
                if (Keys[i] != other.Keys[i]) return false;
            return true;
        }

        public override bool Equals(object o) => o is KeyChord && Equals((KeyChord)o);

        public override int GetHashCode()
        {
            int h = (Ctrl ? 1 : 0) ^ (Shift ? 2 : 0) ^ (Alt ? 4 : 0);
            if (Keys != null)
                for (int i = 0; i < Keys.Length; i++)
                    h = (h * 397) ^ (int)Keys[i];
            return h;
        }
    }

    public class SpraySettingsUI : MonoBehaviour
    {
        // UI elements
        private UITK.UIDocument _doc;
        private UITK.VisualElement _panel;
        private UITK.VisualElement _backdrop;
        private UITK.ScrollView _scrollView;
        private UITK.VisualElement _tabBar;
        private bool _isVisible = false;
        
        // Tabs
        private enum SprayTab { General, Sprays, Binds }
        private SprayTab _activeTab = SprayTab.General;
        private UITK.Button _tabGeneral, _tabSprays, _tabBinds;
        
        // Config reference
        private SprayClientConfig _config;
        
        // Cursor state
        private bool _savedCursorState = false;
        private CursorLockMode _prevLockState = CursorLockMode.None;
        private bool _prevCursorVisible = false;
        private bool _prevMouseActive = false;
        
        // Menu button hiding
        private List<UITK.VisualElement> _hiddenMenuElements = new List<UITK.VisualElement>();
        private static FieldInfo _fiMainSettings;
        private static FieldInfo _fiPauseSettings;
        
        // Keybind capture
        private bool _isCapturing = false;
        private Action<string> _onKeyCaptured;
        private UITK.VisualElement _captureOverlay;
        private UITK.Label _captureLabel;
        private bool _panelHiddenForCapture = false;

        // UI palette (matching base game exactly)
        private static readonly Color32 TextFieldBg = new Color32(57, 57, 57, 255);
        private static readonly Color32 RowBg = new Color32(61, 61, 61, 255);
        private static readonly Color32 ButtonBg = new Color32(57, 57, 57, 255);
        private static readonly Color32 PanelBg = new Color32(48, 48, 47, 255);
        private static readonly Color32 TabActiveBg = new Color32(80, 80, 80, 255);
        private static readonly Color32 TabInactiveBg = new Color32(66, 66, 66, 255);
        private static readonly Color32 ChipBg = new Color32(80, 80, 80, 255);
        private static readonly Color32 ChipXBg = new Color32(100, 100, 100, 255);
        private static Font _uiFont;
        
        private void Awake()
        {
            _fiMainSettings = typeof(UIMainMenu).GetField("settingsButton", BindingFlags.Instance | BindingFlags.NonPublic);
            _fiPauseSettings = typeof(UIPauseMenu).GetField("settingsButton", BindingFlags.Instance | BindingFlags.NonPublic);
        }
        
        private void Start()
        {
            _config = SprayConfigManager.LoadClientConfig();
            
            // Register with ModMenuHub
            try
            {
                PonceMods.Shared.ModMenuHub.RegisterMod(
                    "SprayMod",
                    "SPRAYS",
                    () => ToggleUI(),
                    30 // Priority
                );
                PonceMods.Shared.ModMenuHub.Initialize("SprayMod");
                Debug.Log("[SprayMod] Registered with ModMenuHub");
            }
            catch (Exception e)
            {
                Debug.LogError($"[SprayMod] ModMenuHub registration failed: {e}");
            }
        }
        
        private void OnDestroy()
        {
            try
            {
                PonceMods.Shared.ModMenuHub.UnregisterMod("SprayMod");
            }
            catch { }
            
            _panel?.RemoveFromHierarchy();
            _backdrop?.RemoveFromHierarchy();
            _captureOverlay?.RemoveFromHierarchy();
        }
        
        public void ToggleUI()
        {
            if (_isVisible)
                HideUI();
            else
                ShowUI();
        }
        
        private void ShowUI()
        {
            if (_panel == null)
                CreateUI();
            
            if (_panel == null) return;

            // Save cursor state and request mouse mode (b897: drives cursor + suspends input)
            _prevMouseActive = SprayUtilities.GetGameMouseActive();
            SprayUtilities.SetGameMouseActive(true);
            _prevLockState = UnityEngine.Cursor.lockState;
            _prevCursorVisible = UnityEngine.Cursor.visible;
            _savedCursorState = true;
            
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;
            
            // Menu buttons are already hidden by hub, no need to hide them again
            
            // Show panel
            _backdrop.style.display = DisplayStyle.Flex;
            _panel.style.display = DisplayStyle.Flex;
            _isVisible = true;
            
            // Reload config
            _config = SprayConfigManager.LoadClientConfig();
            RefreshUI();
            
            Debug.Log("[SprayMod] Settings UI opened");
        }
        
        private void HideUI()
        {
            if (_panel == null) return;
            
            // Check if panel is already closed - don't re-open hub if so
            bool wasVisible = _isVisible;
            
            // Save config before closing
            if (wasVisible)
            {
                SaveConfig();
            }
            
            _panel.style.display = DisplayStyle.None;
            _backdrop.style.display = DisplayStyle.None;
            _isVisible = false;
            
            // Only return to hub if the panel was actually visible
            if (!wasVisible) return;
            
            // Don't restore menu buttons - hub will manage them
            // Don't restore cursor state - hub will manage it
            
            Debug.Log("[SprayMod] Settings UI closed, returning to hub");
            
            // Return to ModMenuHub
            try
            {
                PonceMods.Shared.ModMenuHub.OpenPanel();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SprayMod] Failed to open hub: {e}");
                // Fallback: restore state manually
                HideMenuButtons(false);
                if (_savedCursorState)
                {
                    SprayUtilities.SetGameMouseActive(_prevMouseActive);
                    UnityEngine.Cursor.lockState = _prevLockState;
                    UnityEngine.Cursor.visible = _prevCursorVisible;
                    _savedCursorState = false;
                }
            }
        }
        
        /// <summary>
        /// Fully close panel without returning to hub (ESC behavior).
        /// </summary>
        private void FullCloseUI()
        {
            if (_panel == null) return;
            
            // If capturing, cancel capture first
            if (_isCapturing)
            {
                CancelCapture();
                return;
            }
            
            // Save config before closing
            if (_isVisible)
            {
                SaveConfig();
            }
            
            _panel.style.display = DisplayStyle.None;
            _backdrop.style.display = DisplayStyle.None;
            _isVisible = false;
            
            Debug.Log("[SprayMod] Settings UI fully closed via ESC");
            
            // Use ModMenuHub's FullClose to handle cursor and menu buttons properly
            try
            {
                PonceMods.Shared.ModMenuHub.FullClose();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SprayMod] Failed to full close: {e}");
                // Fallback
                HideMenuButtons(false);
                if (_savedCursorState)
                {
                    SprayUtilities.SetGameMouseActive(_prevMouseActive);
                    UnityEngine.Cursor.lockState = _prevLockState;
                    UnityEngine.Cursor.visible = _prevCursorVisible;
                    _savedCursorState = false;
                }
            }
        }
        
        private void Update()
        {
            // Handle ESC key to fully close panel
            if (_isVisible && !_isCapturing)
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null && kb.escapeKey.wasPressedThisFrame)
                {
                    FullCloseUI();
                    return;
                }
            }
            
            // Keep cursor unlocked when panel is visible (but not during capture)
            if (_isVisible && !_isCapturing)
            {
                if (UnityEngine.Cursor.lockState != CursorLockMode.None)
                    UnityEngine.Cursor.lockState = CursorLockMode.None;
                if (!UnityEngine.Cursor.visible)
                    UnityEngine.Cursor.visible = true;
            }
        }
        
        private void CreateUI()
        {
            try
            {
                var uiMgr = FindFirstObjectByType<UIManager>(FindObjectsInactive.Include);
                _doc = uiMgr?.UIDocument ?? FindFirstObjectByType<UITK.UIDocument>(FindObjectsInactive.Include);
                if (_doc?.rootVisualElement == null) return;
                
                var root = _doc.rootVisualElement;
                
                // Backdrop
                _backdrop = new UITK.VisualElement { name = "Spray_Backdrop" };
                _backdrop.style.position = UITK.Position.Absolute;
                _backdrop.style.left = 0; _backdrop.style.top = 0;
                _backdrop.style.right = 0; _backdrop.style.bottom = 0;
                _backdrop.style.backgroundColor = new UITK.StyleColor(new Color(0, 0, 0, 0.0f));
                _backdrop.style.display = UITK.DisplayStyle.None;
                _backdrop.pickingMode = UITK.PickingMode.Position;
                _backdrop.RegisterCallback<UITK.PointerUpEvent>(_ => HideUI());

                // Main panel
                _panel = new UITK.VisualElement { name = "Spray_Panel" };
                _panel.style.position = UITK.Position.Absolute;
                _panel.style.left = new UITK.Length(50, UITK.LengthUnit.Percent);
                _panel.style.top = new UITK.Length(50, UITK.LengthUnit.Percent);
                _panel.style.translate = new UITK.Translate(
                    new UITK.Length(-50, UITK.LengthUnit.Percent),
                    new UITK.Length(-50, UITK.LengthUnit.Percent), 0f);
                int targetW = Mathf.Clamp(Mathf.RoundToInt(Screen.width * 0.58f), 680, 980);
                _panel.style.width = targetW;
                _panel.style.height = new UITK.Length(84, UITK.LengthUnit.Percent);
                _panel.style.minHeight = new UITK.Length(56, UITK.LengthUnit.Percent);
                _panel.style.maxHeight = new UITK.Length(56, UITK.LengthUnit.Percent);
                _panel.style.overflow = UITK.Overflow.Hidden;
                _panel.style.flexDirection = UITK.FlexDirection.Column;
                _panel.style.backgroundColor = new UITK.StyleColor(PanelBg);
                _panel.style.paddingLeft = 8; _panel.style.paddingRight = 8;
                _panel.style.paddingTop = 8; _panel.style.paddingBottom = 12;
                _panel.style.display = UITK.DisplayStyle.None;
                _panel.pickingMode = UITK.PickingMode.Position;
                _panel.RegisterCallback<UITK.PointerUpEvent>(e => e.StopPropagation());
                
                // Title
                var title = new UITK.Label("SPRAYS");
                title.style.fontSize = 50;
                title.style.marginBottom = 8;
                MakeReadable(title);
                _panel.Add(title);
                
                // Tab bar
                _tabBar = new UITK.VisualElement();
                _tabBar.style.flexDirection = UITK.FlexDirection.Row;
                _tabBar.style.marginBottom = 26;
                _tabBar.style.height = 50;
                
                _tabGeneral = MakeTabButton("GENERAL", true, () => SwitchToTab(SprayTab.General));
                _tabSprays = MakeTabButton("SPRAYS", false, () => SwitchToTab(SprayTab.Sprays));
                _tabBinds = MakeTabButton("KEYBINDS", false, () => SwitchToTab(SprayTab.Binds));
                
                _tabBar.Add(_tabGeneral);
                _tabBar.Add(_tabSprays);
                _tabBar.Add(_tabBinds);
                _panel.Add(_tabBar);
                
                // Scroll view
                _scrollView = new UITK.ScrollView();
                _scrollView.style.flexGrow = 1;
                _scrollView.verticalScrollerVisibility = ScrollerVisibility.Auto;
                _scrollView.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
                // Keep content clear of the vertical scrollbar so nothing is clipped on the right.
                _scrollView.contentContainer.style.paddingRight = 14;
                _panel.Add(_scrollView);
                
            // Footer row: evenly spread (COFFEE? left, RESET centre, CLOSE right) via flexbox -
            // no hardcoded pixel margins, which previously left the buttons misaligned.
            var buttonRow = new UITK.VisualElement();
            buttonRow.style.flexDirection = UITK.FlexDirection.Row;
            buttonRow.style.justifyContent = UITK.Justify.SpaceBetween;
            buttonRow.style.alignItems = UITK.Align.Center;
            buttonRow.style.marginTop = 8;

            var donateBtn = MakeDonateButton("COFFEE?", () => Application.OpenURL("https://buymeacoffee.com/amikiir"));
            var resetBtn = MakeResetButton("RESET TO DEFAULTS", ResetToDefaults);
            var closeBtn = MakeCloseButton("CLOSE", () =>
            {
                SaveConfig();
                HideUI();
            });

            buttonRow.Add(donateBtn);
            buttonRow.Add(resetBtn);
            buttonRow.Add(closeBtn);
            _panel.Add(buttonRow);
                
                // Capture overlay - matching DashFall styling
                _captureOverlay = new UITK.VisualElement { name = "Spray_CaptureOverlay" };
                _captureOverlay.style.position = UITK.Position.Absolute;
                _captureOverlay.style.left = 0; _captureOverlay.style.top = 0;
                _captureOverlay.style.right = 0; _captureOverlay.style.bottom = 0;
                _captureOverlay.style.backgroundColor = new UITK.StyleColor(new Color(0.1f, 0.1f, 0.15f, 0.95f));
                _captureOverlay.style.display = UITK.DisplayStyle.None;
                _captureOverlay.pickingMode = UITK.PickingMode.Position;
                ForceUIFont(_captureOverlay);

                var centerContainer = new UITK.VisualElement();
                centerContainer.style.position = UITK.Position.Absolute;
                centerContainer.style.left = new UITK.Length(50, UITK.LengthUnit.Percent);
                centerContainer.style.top = new UITK.Length(50, UITK.LengthUnit.Percent);
                centerContainer.style.translate = new UITK.Translate(
                    new UITK.Length(-50, UITK.LengthUnit.Percent),
                    new UITK.Length(-50, UITK.LengthUnit.Percent), 0);
                centerContainer.style.alignItems = UITK.Align.Center;
                centerContainer.style.justifyContent = UITK.Justify.Center;
                centerContainer.style.flexDirection = UITK.FlexDirection.Column;

                var captureTitle = new UITK.Label("KEY REBIND");
                captureTitle.style.fontSize = 72;
                captureTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
                captureTitle.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
                captureTitle.style.marginBottom = 32;
                MakeReadable(captureTitle);
                centerContainer.Add(captureTitle);

                _captureLabel = new UITK.Label("Press a key or combination to bind.\n(ESC to cancel)");
                _captureLabel.style.fontSize = 24;
                _captureLabel.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
                _captureLabel.style.whiteSpace = UITK.WhiteSpace.Normal;
                _captureLabel.style.maxWidth = 600;
                MakeReadable(_captureLabel);
                centerContainer.Add(_captureLabel);

                _captureOverlay.Add(centerContainer);
                
                root.Add(_backdrop);
                _backdrop.Add(_panel);
                root.Add(_captureOverlay);
                
                RefreshUI();
                Debug.Log("[SprayMod] Settings UI created");
            }
            catch (Exception e)
            {
                Debug.LogError($"[SprayMod] Failed to create settings UI: {e}");
            }
        }
        
        private void RefreshUI()
        {
            if (_scrollView == null) return;
            _scrollView.Clear();
            
            if (_activeTab == SprayTab.General)
                BuildGeneralTab();
            else if (_activeTab == SprayTab.Sprays)
                BuildSpraysTab();
            else if (_activeTab == SprayTab.Binds)
                BuildBindsTab();
        }
        
        private void BuildGeneralTab()
        {
            AddToggleRow("Enable Sound Effects", _config.EnableSound, v => { _config.EnableSound = v; SaveConfig(); });
            AddToggleRow("Share My Sprays With Others", _config.ShareSprays, v => { _config.ShareSprays = v; SaveConfig(); });
            AddToggleRow("Auto-Upload Sprays (so everyone sees them)", _config.AutoUpload, v => { _config.AutoUpload = v; SaveConfig(); });
            AddToggleRow("Show Other Players' Sprays", _config.ShowOtherPlayerSprays, v => { _config.ShowOtherPlayerSprays = v; SaveConfig(); });
            AddToggleRow("Only Show Friends' Sprays", _config.FriendsOnly, v => { _config.FriendsOnly = v; SaveConfig(); });
            AddToggleRow("Debug Logging", _config.Debug, v => { _config.Debug = v; SaveConfig(); });

            // Size & opacity are local and apply to ALL sprays (yours + others'), live.
            AddSliderRow("Spray Opacity", _config.SprayOpacity, 0f, 1f, v =>
            {
                _config.SprayOpacity = v;
                SprayManager.Instance?.ApplyDisplaySettings(_config.SpraySize, v);
            }); // saved on close
            AddSliderRow("Spray Size", _config.SpraySize, 0.1f, 3f, v =>
            {
                _config.SpraySize = v;
                SprayManager.Instance?.ApplyDisplaySettings(v, _config.SprayOpacity);
                SaveConfig();
            });
            
            // Spray folder info
            var infoLabel = new UITK.Label($"Spray images folder:\n{SprayConfigManager.GetSprayImagesFolderPath()}");
            infoLabel.style.fontSize = 14;
            infoLabel.style.color = new Color(0.6f, 0.6f, 0.6f);
            infoLabel.style.marginTop = 20;
            infoLabel.style.whiteSpace = UITK.WhiteSpace.Normal;
            ForceUIFont(infoLabel);
            _scrollView.Add(infoLabel);
        }
        
        private void BuildSpraysTab()
        {
            var header = new UITK.Label("YOUR SPRAYS");
            header.style.fontSize = 24;
            header.style.marginBottom = 12;
            header.style.marginTop = 8;
            MakeReadable(header);
            _scrollView.Add(header);

            // The manifest (sprays.json) is the source of truth - edits here are written to it
            // and applied live.
            var manifest = SprayConfigManager.LoadManifest();

            if (manifest.sprays.Count == 0)
            {
                var none = new UITK.Label("No sprays yet.\n\nDrop .png / .jpg / .gif files in the folder below,\nor paste an image link and press ADD LINK.");
                none.style.fontSize = 16;
                none.style.marginTop = 12;
                none.style.whiteSpace = UITK.WhiteSpace.Normal;
                MakeReadable(none);
                _scrollView.Add(none);
            }
            else
            {
                for (int i = 0; i < manifest.sprays.Count; i++)
                    _scrollView.Add(BuildSprayRow(manifest, i));
            }

            // Add-image-link control (link-first: this is the main way to add a spray).
            _scrollView.Add(BuildAddLinkRow(manifest));

            var addLinkBtn = MakeButton("ADD IMAGE LINK", () => AddLinkFromField(manifest));
            addLinkBtn.style.marginTop = 8;
            _scrollView.Add(addLinkBtn);

            var reloadBtn = MakeButton("RELOAD SPRAYS", ReloadAndRefresh);
            _scrollView.Add(reloadBtn);

            // Optional: use local image files instead of links (advanced).
            var folderBtn = MakeButton("OPEN LOCAL IMAGES FOLDER", () =>
            {
                try { System.Diagnostics.Process.Start("explorer.exe", SprayConfigManager.EnsureSprayImagesFolder()); }
                catch (Exception e) { Debug.LogError($"[SprayMod] Open folder failed: {e.Message}"); }
            });
            _scrollView.Add(folderBtn);

            var hint = new UITK.Label("Paste an image/GIF link above and press ADD IMAGE LINK — everyone with the mod can see it. Local image files are optional (others only see them if they have the same file).");
            hint.style.fontSize = 12;
            hint.style.color = new Color(0.6f, 0.6f, 0.6f);
            hint.style.marginTop = 8;
            hint.style.whiteSpace = UITK.WhiteSpace.Normal;
            ForceUIFont(hint);
            _scrollView.Add(hint);
        }

        private UITK.TextField _addLinkField;

        /// <summary>Adds the link currently typed in the add-link field to the manifest.</summary>
        private void AddLinkFromField(SprayManifest manifest)
        {
            string url = (_addLinkField?.value ?? "").Trim();
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                manifest.sprays.Add(new SpraySpec { name = "", url = url });
                SprayConfigManager.SaveManifest(manifest);
                if (_addLinkField != null) _addLinkField.value = "";
                // The rebuilt row auto-shortens long links itself (see BuildSprayRow), with status.
                ReloadAndRefresh();
            }
        }

        // Per-session link-shorten state, keyed by the original (long) URL, so opening the tab or
        // adding a link auto-shortens once and shows progress/errors without retrying on every refresh.
        private readonly HashSet<string> _shortenWorking = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _shortenError = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Auto re-hosts a too-long link to a short permanent URL and writes it back into sprays.json
        /// (matched by its current URL). Runs once per URL per session; result drives the row status.
        /// </summary>
        private void EnsureShortened(string longUrl)
        {
            if (string.IsNullOrEmpty(longUrl)) return;
            if (_shortenWorking.Contains(longUrl) || _shortenError.ContainsKey(longUrl)) return;
            var mgr = SprayManager.Instance;
            if (mgr == null) return;

            _shortenWorking.Add(longUrl);
            mgr.ShortenLink(longUrl, (newUrl, error) =>
            {
                _shortenWorking.Remove(longUrl);
                if (!string.IsNullOrEmpty(newUrl))
                {
                    var m = SprayConfigManager.LoadManifest();
                    bool changed = false;
                    foreach (var s in m.sprays)
                        if (s.IsUrl && string.Equals(s.url, longUrl, StringComparison.OrdinalIgnoreCase)) { s.url = newUrl; changed = true; }
                    if (changed) { SprayConfigManager.SaveManifest(m); ReloadAndRefresh(); }
                    else if (_isVisible) RefreshUI();
                }
                else
                {
                    _shortenError[longUrl] = error ?? "Couldn't shorten this link.";
                    if (_isVisible) RefreshUI();
                }
            });
        }

        private UITK.VisualElement BuildSprayRow(SprayManifest manifest, int index)
        {
            var spec = manifest.sprays[index];
            var mgr = SprayManager.Instance;

            var row = new UITK.VisualElement();
            row.style.flexDirection = UITK.FlexDirection.Row;
            row.style.alignItems = UITK.Align.Center;
            row.style.height = 50;
            row.style.marginBottom = 8;
            row.style.backgroundColor = new UITK.StyleColor(RowBg);
            row.style.paddingLeft = 12;
            row.style.paddingRight = 12;
            row.style.borderTopLeftRadius = 4;
            row.style.borderTopRightRadius = 4;
            row.style.borderBottomLeftRadius = 4;
            row.style.borderBottomRightRadius = 4;
            row.style.overflow = UITK.Overflow.Hidden;

            var thumb = CreateThumbnailFromTexture(mgr != null ? mgr.FindThumbnailForSpec(spec) : null, 34);
            thumb.style.marginRight = 8;
            thumb.style.flexShrink = 0;
            row.Add(thumb);

            // Editable display name (defaults to the file name; for files there is no separate
            // filename label - the name IS the filename unless you rename it).
            var nameField = new UITK.TextField { value = spec.name ?? "" };
            nameField.style.flexGrow = 1;
            nameField.style.flexShrink = 1;
            nameField.style.minWidth = 60;
            nameField.style.marginRight = 6;
            StyleTextField(nameField);
            nameField.RegisterCallback<UITK.FocusOutEvent>(_ =>
            {
                string v = (nameField.value ?? "").Trim();
                if (v != (spec.name ?? ""))
                {
                    spec.name = v;
                    SprayConfigManager.SaveManifest(manifest);
                    mgr?.SyncMetadata(manifest);
                    RefreshUI();
                }
            });
            row.Add(nameField);

            // Links show a read-only status (not an editable field): a short/shareable link, a
            // "Shortening…" spinner while it's auto re-hosted, or a clear reason it can't be used.
            if (spec.IsUrl)
            {
                var status = new UITK.Label();
                status.style.flexGrow = 1;
                status.style.flexShrink = 1;
                status.style.minWidth = 60;
                status.style.marginRight = 6;
                status.style.fontSize = 13;
                status.style.whiteSpace = UITK.WhiteSpace.NoWrap;
                status.style.textOverflow = UITK.TextOverflow.Ellipsis;
                status.style.overflow = UITK.Overflow.Hidden;
                status.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleLeft);
                ForceUIFont(status);

                if (SprayManager.IsHostedShortLink(spec.url))
                {
                    status.text = "Ready - shareable";   // already a clean, permanent, short link
                    status.style.color = new Color(0.55f, 0.8f, 0.55f);
                }
                else if (_shortenWorking.Contains(spec.url))
                {
                    status.text = "Shortening link…";
                    status.style.color = new Color(0.9f, 0.82f, 0.4f);
                }
                else if (_shortenError.TryGetValue(spec.url, out var err))
                {
                    status.text = err;
                    // Red if it can't even be shared as-is; amber if it still works but wasn't shortened.
                    status.style.color = SprayChatSync.UrlFitsInChat(spec.url)
                        ? new Color(0.9f, 0.82f, 0.4f)
                        : new Color(0.92f, 0.5f, 0.5f);
                }
                else
                {
                    status.text = "Shortening link…";
                    status.style.color = new Color(0.9f, 0.82f, 0.4f);
                    EnsureShortened(spec.url); // auto-start; refreshes the row when done
                }
                row.Add(status);
            }

            // Reorder + remove. flexShrink 0 so they never get squeezed/clipped. ASCII labels
            // because the game UI font lacks ▲▼✕ glyphs (they render as boxes).
            row.Add(MakeMiniButton("Up", () => MoveSpray(manifest, index, -1)));
            row.Add(MakeMiniButton("Dn", () => MoveSpray(manifest, index, +1)));

            if (spec.IsUrl) // remove a file by deleting it in the folder
            {
                row.Add(MakeMiniButton("X", () =>
                {
                    if (index >= 0 && index < manifest.sprays.Count)
                    {
                        manifest.sprays.RemoveAt(index);
                        SprayConfigManager.SaveManifest(manifest);
                        ReloadAndRefresh();
                    }
                }));
            }

            return row;
        }

        private void MoveSpray(SprayManifest manifest, int index, int dir)
        {
            int target = index + dir;
            if (index < 0 || index >= manifest.sprays.Count || target < 0 || target >= manifest.sprays.Count) return;
            var tmp = manifest.sprays[target];
            manifest.sprays[target] = manifest.sprays[index];
            manifest.sprays[index] = tmp;
            SprayConfigManager.SaveManifest(manifest);
            SprayManager.Instance?.SyncMetadata(manifest);
            RefreshUI();
        }

        private UITK.VisualElement BuildAddLinkRow(SprayManifest manifest)
        {
            var container = new UITK.VisualElement();
            container.style.marginTop = 14;

            var lbl = new UITK.Label("ADD AN IMAGE LINK");
            lbl.style.fontSize = 14;
            lbl.style.marginBottom = 4;
            MakeReadable(lbl);
            container.Add(lbl);

            _addLinkField = new UITK.TextField();
            _addLinkField.style.flexGrow = 1;
            StyleTextField(_addLinkField);
            // Enter in the field also adds the link.
            _addLinkField.RegisterCallback<UITK.KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                    AddLinkFromField(manifest);
            });
            container.Add(_addLinkField);

            return container;
        }

        private UITK.Button MakeMiniButton(string text, Action onClick, float width = 38)
        {
            var b = new UITK.Button(onClick) { text = text };
            b.style.width = width;
            b.style.height = 32;
            b.style.flexShrink = 0;                 // never squeezed out by flexible fields
            b.style.marginLeft = 3;
            b.style.marginTop = 0; b.style.marginBottom = 0;
            b.style.paddingLeft = 0; b.style.paddingRight = 0;
            b.style.fontSize = 15;                  // default game font is large; keep it inside the button
            b.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            b.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            MakeReadable(b);
            AddButtonFlash(b);
            return b;
        }

        // Matches CompAdjust/DashFall input look: dark fill + medium-gray border so the
        // field reads as an editable box against the dark rows. The ".unity-text-input"
        // child only exists once parented, so style it on AttachToPanel.
        private void StyleTextField(UITK.TextField tf)
        {
            tf.style.height = 34;
            tf.style.fontSize = 15;
            tf.style.color = Color.white;
            ForceUIFont(tf);
            tf.RegisterCallback<UITK.AttachToPanelEvent>(_ =>
            {
                var input = tf.Q("unity-text-input");
                if (input == null) return;
                input.style.backgroundColor = new UITK.StyleColor(new Color(0.15f, 0.15f, 0.15f));
                input.style.color = Color.white;
                input.style.fontSize = 15;
                input.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleLeft);
                input.style.overflow = UITK.Overflow.Hidden;
                var border = new UITK.StyleColor(new Color(0.4f, 0.4f, 0.4f));
                input.style.borderTopWidth = 1; input.style.borderBottomWidth = 1;
                input.style.borderLeftWidth = 1; input.style.borderRightWidth = 1;
                input.style.borderTopColor = border; input.style.borderBottomColor = border;
                input.style.borderLeftColor = border; input.style.borderRightColor = border;
                var f = GetUIFont();
                if (f != null) input.style.unityFont = f;
            });
        }

        /// <summary>Reloads the spray library from sprays.json, then refreshes the panel.</summary>
        private void ReloadAndRefresh()
        {
            var mgr = SprayManager.Instance;
            if (mgr != null) mgr.LoadLibrary(_ => { if (_isVisible) RefreshUI(); });
            else RefreshUI();
        }

        /// <summary>Builds a fixed-size thumbnail element from an already-loaded texture.</summary>
        private UITK.VisualElement CreateThumbnailFromTexture(Texture2D tex, int size)
        {
            var container = new UITK.VisualElement();
            container.style.width = size;
            container.style.height = size;
            container.style.flexShrink = 0;
            container.style.backgroundColor = new UITK.StyleColor(new Color(0.2f, 0.2f, 0.2f, 1f));
            container.style.borderTopLeftRadius = 4;
            container.style.borderTopRightRadius = 4;
            container.style.borderBottomLeftRadius = 4;
            container.style.borderBottomRightRadius = 4;
            if (tex != null)
            {
                container.style.backgroundImage = new UITK.StyleBackground(tex);
                container.style.unityBackgroundScaleMode = new UITK.StyleEnum<ScaleMode>(ScaleMode.ScaleToFit);
            }
            return container;
        }
        
        private void BuildBindsTab()
        {
            var header = new UITK.Label("KEYBINDS");
            header.style.fontSize = 24;
            header.style.marginBottom = 16;
            header.style.marginTop = 8;
            MakeReadable(header);
            _scrollView.Add(header);
            
            // Spray wheel key
            AddBindRow("Open Spray Wheel", _config.SprayWheelKey, key => { _config.SprayWheelKey = key; SaveConfig(); });
            
            // Quick spray binds
            var subHeader = new UITK.Label("Quick Spray Binds (direct spray without wheel)");
            subHeader.style.fontSize = 16;
            subHeader.style.marginTop = 20;
            subHeader.style.marginBottom = 8;
            subHeader.style.color = new Color(0.7f, 0.7f, 0.9f);
            ForceUIFont(subHeader);
            _scrollView.Add(subHeader);
            
            // Bind by stable source (file name / URL) so binds survive renames & reordering.
            var manifest = SprayConfigManager.LoadManifest();
            var mgr = SprayManager.Instance;

            if (manifest.sprays.Count == 0)
            {
                var noSpraysLabel = new UITK.Label("Add spray images first to set up quick binds.");
                noSpraysLabel.style.fontSize = 16;
                noSpraysLabel.style.marginTop = 8;
                MakeReadable(noSpraysLabel);
                _scrollView.Add(noSpraysLabel);
            }
            else
            {
                for (int i = 0; i < manifest.sprays.Count; i++)
                {
                    var spec = manifest.sprays[i];
                    string source = spec.Source;
                    string currentKey = "";
                    foreach (var kvp in _config.QuickSprayBinds)
                    {
                        if (string.Equals(kvp.Value, source, StringComparison.OrdinalIgnoreCase))
                        {
                            currentKey = kvp.Key;
                            break;
                        }
                    }

                    var tex = mgr != null ? mgr.FindThumbnailForSpec(spec) : null;
                    string label = string.IsNullOrEmpty(spec.name)
                        ? (spec.IsUrl ? $"Link {i + 1}" : spec.file)
                        : spec.name;

                    AddBindRowWithThumbnail($"{i + 1}. {label}", tex, currentKey, key =>
                    {
                        var toRemove = _config.QuickSprayBinds
                            .Where(kvp => string.Equals(kvp.Value, source, StringComparison.OrdinalIgnoreCase))
                            .Select(kvp => kvp.Key).ToList();
                        foreach (var k in toRemove) _config.QuickSprayBinds.Remove(k);

                        if (!string.IsNullOrEmpty(key))
                            _config.QuickSprayBinds[key] = source;

                        SaveConfig();
                    });
                }
            }
        }
        
        private void SwitchToTab(SprayTab tab)
        {
            _activeTab = tab;
            UpdateTabStyles();
            RefreshUI();
        }
        
        private void UpdateTabStyles()
        {
            UpdateSingleTabStyle(_tabGeneral, _activeTab == SprayTab.General);
            UpdateSingleTabStyle(_tabSprays, _activeTab == SprayTab.Sprays);
            UpdateSingleTabStyle(_tabBinds, _activeTab == SprayTab.Binds);
        }
        
        private void UpdateSingleTabStyle(UITK.Button btn, bool active)
        {
            if (btn == null) return;
            btn.style.backgroundColor = new UITK.StyleColor(active ? TabActiveBg : TabInactiveBg);
            btn.style.color = active ? Color.white : new Color(0.7f, 0.7f, 0.7f);
            btn.style.borderBottomWidth = active ? 3 : 0;
        }
        
        private UITK.Button MakeTabButton(string text, bool isActive, Action onClick)
        {
            var btn = new UITK.Button(onClick) { text = text };
            btn.style.height = 50;
            btn.style.flexGrow = 1;
            btn.style.paddingLeft = 8;
            btn.style.paddingRight = 8;
            btn.style.marginRight = 8;
            btn.style.marginBottom = 26;
            btn.style.fontSize = 24;
            btn.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            btn.style.borderTopLeftRadius = 6;
            btn.style.borderTopRightRadius = 6;
            btn.style.borderBottomLeftRadius = 0;
            btn.style.borderBottomRightRadius = 0;
            btn.style.borderBottomWidth = isActive ? 3 : 0;
            btn.style.borderBottomColor = new UITK.StyleColor(Color.white);
            btn.style.backgroundColor = new UITK.StyleColor(isActive ? TabActiveBg : TabInactiveBg);
            btn.style.color = isActive ? Color.white : new Color(0.7f, 0.7f, 0.7f);
            ForceUIFont(btn);
            
            // Hover effect
            btn.RegisterCallback<UITK.PointerEnterEvent>(_ => {
                if (btn.resolvedStyle.borderBottomWidth < 1)
                {
                    btn.style.backgroundColor = new UITK.StyleColor(Color.white);
                    btn.style.color = Color.black;
                }
            });
            btn.RegisterCallback<UITK.PointerLeaveEvent>(_ => {
                if (btn.resolvedStyle.borderBottomWidth < 1)
                {
                    btn.style.backgroundColor = new UITK.StyleColor(TabInactiveBg);
                    btn.style.color = new Color(0.7f, 0.7f, 0.7f);
                }
            });
            
            return btn;
        }
        
        private void AddToggleRow(string label, bool initialValue, Action<bool> onChanged)
        {
            var row = new UITK.VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.alignItems = Align.Center;
            row.style.backgroundColor = new StyleColor(RowBg);
            row.style.paddingLeft = 12;
            row.style.paddingRight = 12;
            row.style.paddingTop = 10;
            row.style.paddingBottom = 10;
            row.style.marginBottom = 6;
            
            var lbl = new UITK.Label(label.ToUpperInvariant());
            lbl.style.fontSize = 24;
            MakeReadable(lbl);
            row.Add(lbl);
            
            var toggle = new UITK.Toggle();
            toggle.value = initialValue;
            toggle.RegisterValueChangedCallback(e => onChanged(e.newValue));
            row.Add(toggle);
            
            _scrollView.Add(row);
        }
        
        private void AddSliderRow(string label, float initialValue, float min, float max, Action<float> onChanged)
        {
            var row = new UITK.VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.whiteSpace = WhiteSpace.NoWrap;
            row.style.height = 50;
            row.style.backgroundColor = new StyleColor(RowBg);
            row.style.paddingLeft = 12;
            row.style.paddingRight = 12;
            row.style.paddingTop = 8;
            row.style.paddingBottom = 8;
            row.style.marginBottom = 6;
            
            var lbl = new UITK.Label(label.ToUpperInvariant());
            lbl.style.fontSize = 24;
            lbl.style.whiteSpace = WhiteSpace.NoWrap;
            lbl.style.minWidth = 220;
            lbl.style.maxWidth = 220;
            MakeReadable(lbl);
            row.Add(lbl);
            
            // Spacer
            var spacer = new UITK.VisualElement();
            spacer.style.flexGrow = 1;
            row.Add(spacer);
            
            // Slider
            var slider = new UITK.Slider(min, max);
            slider.value = initialValue;
            slider.style.width = 300;
            slider.style.height = 20;
            slider.style.marginLeft = 8;
            slider.style.marginRight = 8;
            StyleSlider(slider);
            row.Add(slider);
            
            // Text field for value
            var valueField = new UITK.TextField();
            valueField.value = initialValue.ToString("F2");
            valueField.style.width = 60;
            valueField.style.unityTextAlign = TextAnchor.MiddleRight;
            valueField.style.backgroundColor = new StyleColor(TextFieldBg);
            valueField.style.color = Color.white;
            ForceUIFont(valueField);
            row.Add(valueField);
            
            // Two-way binding
            slider.RegisterValueChangedCallback(e =>
            {
                float clamped = Mathf.Clamp(e.newValue, min, max);
                valueField.SetValueWithoutNotify(clamped.ToString("F2"));
                onChanged(clamped);
            });
            
            valueField.RegisterValueChangedCallback(e =>
            {
                if (float.TryParse(e.newValue, out float newVal))
                {
                    float clamped = Mathf.Clamp(newVal, min, max);
                    slider.SetValueWithoutNotify(clamped);
                    onChanged(clamped);
                }
            });
            
            _scrollView.Add(row);
        }
        
        private void AddBindRow(string label, string currentKey, Action<string> onChanged)
        {
            var row = new UITK.VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.height = 50;
            row.style.backgroundColor = new StyleColor(RowBg);
            row.style.paddingLeft = 12;
            row.style.paddingRight = 10;
            row.style.paddingTop = 8;
            row.style.paddingBottom = 8;
            row.style.marginBottom = 6;
            
            // Label
            var lbl = new UITK.Label(label.ToUpperInvariant());
            lbl.style.minWidth = 220;
            lbl.style.maxWidth = 220;
            lbl.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleLeft);
            lbl.style.color = Color.white;
            lbl.style.fontSize = 24;
            lbl.style.whiteSpace = UITK.WhiteSpace.NoWrap;
            lbl.style.textOverflow = UITK.TextOverflow.Ellipsis;
            ForceUIFont(lbl);
            row.Add(lbl);
            
            // Chip container (shows bound key as a chip)
            var chipContainer = new UITK.VisualElement();
            chipContainer.style.flexDirection = FlexDirection.Row;
            chipContainer.style.justifyContent = Justify.FlexEnd;
            chipContainer.style.alignItems = Align.Center;
            chipContainer.style.flexGrow = 1;
            chipContainer.style.flexShrink = 1;
            chipContainer.style.minWidth = 0;
            chipContainer.style.marginLeft = 4;
            chipContainer.style.marginRight = 8;
            row.Add(chipContainer);
            
            // Buttons container
            var buttonsContainer = new UITK.VisualElement();
            buttonsContainer.style.flexDirection = FlexDirection.Row;
            buttonsContainer.style.alignItems = Align.Center;
            buttonsContainer.style.flexShrink = 0;
            row.Add(buttonsContainer);
            
            // BIND button
            var bindBtn = new UITK.Button() { text = "BIND" };
            bindBtn.style.width = 80;
            bindBtn.style.height = 34;
            bindBtn.style.marginLeft = 4;
            bindBtn.style.backgroundColor = new StyleColor(ButtonBg);
            bindBtn.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            MakeReadable(bindBtn);
            AddButtonFlash(bindBtn);
            buttonsContainer.Add(bindBtn);
            
            // Function to refresh chips
            void RefreshChips()
            {
                chipContainer.Clear();
                if (!string.IsNullOrEmpty(currentKey))
                {
                    chipContainer.Add(MakeChip(currentKey.ToUpper(), true, () =>
                    {
                        currentKey = "";
                        onChanged("");
                        RefreshChips();
                    }));
                }
            }
            
            bindBtn.clicked += () =>
            {
                StartCapture(key =>
                {
                    if (!string.IsNullOrEmpty(key))
                    {
                        currentKey = key;
                        onChanged(key);
                        RefreshChips();
                    }
                });
            };
            
            RefreshChips();
            _scrollView.Add(row);
        }
        
        /// <summary>
        /// Bind row with a thumbnail image on the left.
        /// </summary>
        private void AddBindRowWithThumbnail(string label, Texture2D thumbnailTex, string currentKey, Action<string> onChanged)
        {
            var row = new UITK.VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.height = 50;
            row.style.backgroundColor = new StyleColor(RowBg);
            row.style.paddingLeft = 8;
            row.style.paddingRight = 10;
            row.style.paddingTop = 4;
            row.style.paddingBottom = 4;
            row.style.marginBottom = 6;

            // Thumbnail
            var thumbnail = CreateThumbnailFromTexture(thumbnailTex, 40);
            thumbnail.style.marginRight = 8;
            row.Add(thumbnail);
            
            // Label
            var lbl = new UITK.Label(label.ToUpperInvariant());
            lbl.style.minWidth = 120;
            lbl.style.maxWidth = 180;
            lbl.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleLeft);
            lbl.style.color = Color.white;
            lbl.style.fontSize = 20;
            lbl.style.whiteSpace = UITK.WhiteSpace.NoWrap;
            lbl.style.textOverflow = UITK.TextOverflow.Ellipsis;
            ForceUIFont(lbl);
            row.Add(lbl);
            
            // Chip container (shows bound key as a chip)
            var chipContainer = new UITK.VisualElement();
            chipContainer.style.flexDirection = FlexDirection.Row;
            chipContainer.style.justifyContent = Justify.FlexEnd;
            chipContainer.style.alignItems = Align.Center;
            chipContainer.style.flexGrow = 1;
            chipContainer.style.flexShrink = 1;
            chipContainer.style.minWidth = 0;
            chipContainer.style.marginLeft = 4;
            chipContainer.style.marginRight = 8;
            row.Add(chipContainer);
            
            // Buttons container
            var buttonsContainer = new UITK.VisualElement();
            buttonsContainer.style.flexDirection = FlexDirection.Row;
            buttonsContainer.style.alignItems = Align.Center;
            buttonsContainer.style.flexShrink = 0;
            row.Add(buttonsContainer);
            
            // BIND button
            var bindBtn = new UITK.Button() { text = "BIND" };
            bindBtn.style.width = 80;
            bindBtn.style.height = 34;
            bindBtn.style.marginLeft = 4;
            bindBtn.style.backgroundColor = new StyleColor(ButtonBg);
            bindBtn.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            MakeReadable(bindBtn);
            AddButtonFlash(bindBtn);
            buttonsContainer.Add(bindBtn);
            
            // Function to refresh chips
            void RefreshChips()
            {
                chipContainer.Clear();
                if (!string.IsNullOrEmpty(currentKey))
                {
                    chipContainer.Add(MakeChip(currentKey.ToUpper(), true, () =>
                    {
                        currentKey = "";
                        onChanged("");
                        RefreshChips();
                    }));
                }
            }
            
            bindBtn.clicked += () =>
            {
                StartCapture(key =>
                {
                    if (!string.IsNullOrEmpty(key))
                    {
                        currentKey = key;
                        onChanged(key);
                        RefreshChips();
                    }
                });
            };
            
            RefreshChips();
            _scrollView.Add(row);
        }
        
        private UITK.VisualElement MakeChip(string text, bool enabled, Action onRemove)
        {
            var chip = new UITK.VisualElement();
            chip.style.flexDirection = FlexDirection.Row;
            chip.style.alignItems = Align.Center;
            chip.style.backgroundColor = new StyleColor(ChipBg);
            chip.style.paddingLeft = 8; chip.style.paddingRight = 4;
            chip.style.paddingTop = 4; chip.style.paddingBottom = 4;
            chip.style.marginRight = 4;
            chip.style.borderTopLeftRadius = 4; chip.style.borderTopRightRadius = 4;
            chip.style.borderBottomLeftRadius = 4; chip.style.borderBottomRightRadius = 4;
            chip.style.opacity = enabled ? 1f : 0.6f;

            var chipLabel = new UITK.Label(text);
            chipLabel.style.fontSize = 14;
            chipLabel.style.color = Color.white;
            ForceUIFont(chipLabel);
            chip.Add(chipLabel);

            var xBtn = new UITK.Button(onRemove) { text = "×" };
            xBtn.style.width = 20; xBtn.style.height = 20;
            xBtn.style.marginLeft = 4;
            xBtn.style.backgroundColor = new StyleColor(ChipXBg);
            xBtn.style.fontSize = 14;
            xBtn.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            xBtn.style.paddingLeft = 0; xBtn.style.paddingRight = 0;
            xBtn.style.paddingTop = 0; xBtn.style.paddingBottom = 0;
            xBtn.SetEnabled(enabled);
            xBtn.style.color = Color.white;
            ForceUIFont(xBtn);
            if (enabled) AddChipButtonFlash(xBtn);
            chip.Add(xBtn);

            return chip;
        }

        private static void AddChipButtonFlash(UITK.Button btn)
        {
            btn.RegisterCallback<UITK.PointerEnterEvent>(_ =>
            {
                btn.style.backgroundColor = new StyleColor(new Color32(180, 80, 80, 255));
                btn.style.color = Color.white;
            });
            btn.RegisterCallback<UITK.PointerLeaveEvent>(_ =>
            {
                btn.style.backgroundColor = new StyleColor(ChipXBg);
                btn.style.color = Color.white;
            });
        }
        
        // ========== CHORD CAPTURE SYSTEM (matching DashFall) ==========
        
        private void StartCapture(Action<string> onCaptured)
        {
            _onKeyCaptured = onCaptured;
            _isCapturing = true;

            HidePanelDuringCapture(true);

            if (_captureLabel != null) _captureLabel.text = "Press a key or combination to bind.\n(ESC to cancel)";
            if (_captureOverlay != null)
                _captureOverlay.style.display = DisplayStyle.Flex;
            StartCoroutine(CaptureChordRoutine());
        }

        private void CancelCapture()
        {
            _isCapturing = false;
            if (_captureOverlay != null) _captureOverlay.style.display = DisplayStyle.None;
            HidePanelDuringCapture(false);
            _onKeyCaptured = null;

            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;
        }

        private void HidePanelDuringCapture(bool hide)
        {
            if (_panel == null) return;

            if (hide)
            {
                _panelHiddenForCapture = (_panel.style.display == DisplayStyle.Flex);
                _panel.style.display = DisplayStyle.None;
                if (_backdrop != null) _backdrop.style.display = DisplayStyle.Flex;
            }
            else
            {
                if (_panelHiddenForCapture)
                {
                    _panel.style.display = DisplayStyle.Flex;
                    _panelHiddenForCapture = false;
                }
            }
        }

        private static bool IsModifierKey(KeyCode k) =>
            k == KeyCode.LeftShift || k == KeyCode.RightShift ||
            k == KeyCode.LeftControl || k == KeyCode.RightControl ||
            k == KeyCode.LeftAlt || k == KeyCode.RightAlt;

        private static bool IsAllowedKey(KeyCode k)
        {
            if (k == KeyCode.None || k == KeyCode.Escape) return false;
            // Allow all keys including mouse buttons
            return true;
        }

        private static bool IsKeyDown(KeyCode k)
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null && k != KeyCode.Mouse0 && k != KeyCode.Mouse1 && k != KeyCode.Mouse2 && 
                k != KeyCode.Mouse3 && k != KeyCode.Mouse4) return false;

            // Letters
            if (k >= KeyCode.A && k <= KeyCode.Z)
            {
                int idx = (int)k - (int)KeyCode.A;
                var key = kb[(UnityEngine.InputSystem.Key)((int)UnityEngine.InputSystem.Key.A + idx)];
                return key != null && key.isPressed;
            }

            // Function keys
            if (k >= KeyCode.F1 && k <= KeyCode.F12)
            {
                int n = (int)k - (int)KeyCode.F1 + 1;
                var key = kb[UnityEngine.InputSystem.Key.F1 + (n - 1)];
                return key != null && key.isPressed;
            }

            if (kb != null)
            {
                switch (k)
                {
                    case KeyCode.Space: return kb.spaceKey?.isPressed ?? false;
                    case KeyCode.Tab: return kb.tabKey?.isPressed ?? false;
                    case KeyCode.Escape: return kb.escapeKey?.isPressed ?? false;
                    case KeyCode.LeftShift: return kb.leftShiftKey?.isPressed ?? false;
                    case KeyCode.RightShift: return kb.rightShiftKey?.isPressed ?? false;
                    case KeyCode.LeftControl: return kb.leftCtrlKey?.isPressed ?? false;
                    case KeyCode.RightControl: return kb.rightCtrlKey?.isPressed ?? false;
                    case KeyCode.LeftAlt: return kb.leftAltKey?.isPressed ?? false;
                    case KeyCode.RightAlt: return kb.rightAltKey?.isPressed ?? false;
                    case KeyCode.UpArrow: return kb.upArrowKey?.isPressed ?? false;
                    case KeyCode.DownArrow: return kb.downArrowKey?.isPressed ?? false;
                    case KeyCode.LeftArrow: return kb.leftArrowKey?.isPressed ?? false;
                    case KeyCode.RightArrow: return kb.rightArrowKey?.isPressed ?? false;
                    case KeyCode.BackQuote: return kb.backquoteKey?.isPressed ?? false;
                    case KeyCode.Minus: return kb.minusKey?.isPressed ?? false;
                    case KeyCode.Equals: return kb.equalsKey?.isPressed ?? false;
                    case KeyCode.LeftBracket: return kb.leftBracketKey?.isPressed ?? false;
                    case KeyCode.RightBracket: return kb.rightBracketKey?.isPressed ?? false;
                    case KeyCode.Semicolon: return kb.semicolonKey?.isPressed ?? false;
                    case KeyCode.Quote: return kb.quoteKey?.isPressed ?? false;
                    case KeyCode.Comma: return kb.commaKey?.isPressed ?? false;
                    case KeyCode.Period: return kb.periodKey?.isPressed ?? false;
                    case KeyCode.Slash: return kb.slashKey?.isPressed ?? false;
                    case KeyCode.Backslash: return kb.backslashKey?.isPressed ?? false;
                    case KeyCode.Alpha0: return kb.digit0Key?.isPressed ?? false;
                    case KeyCode.Alpha1: return kb.digit1Key?.isPressed ?? false;
                    case KeyCode.Alpha2: return kb.digit2Key?.isPressed ?? false;
                    case KeyCode.Alpha3: return kb.digit3Key?.isPressed ?? false;
                    case KeyCode.Alpha4: return kb.digit4Key?.isPressed ?? false;
                    case KeyCode.Alpha5: return kb.digit5Key?.isPressed ?? false;
                    case KeyCode.Alpha6: return kb.digit6Key?.isPressed ?? false;
                    case KeyCode.Alpha7: return kb.digit7Key?.isPressed ?? false;
                    case KeyCode.Alpha8: return kb.digit8Key?.isPressed ?? false;
                    case KeyCode.Alpha9: return kb.digit9Key?.isPressed ?? false;
                    case KeyCode.Keypad0: return kb.numpad0Key?.isPressed ?? false;
                    case KeyCode.Keypad1: return kb.numpad1Key?.isPressed ?? false;
                    case KeyCode.Keypad2: return kb.numpad2Key?.isPressed ?? false;
                    case KeyCode.Keypad3: return kb.numpad3Key?.isPressed ?? false;
                    case KeyCode.Keypad4: return kb.numpad4Key?.isPressed ?? false;
                    case KeyCode.Keypad5: return kb.numpad5Key?.isPressed ?? false;
                    case KeyCode.Keypad6: return kb.numpad6Key?.isPressed ?? false;
                    case KeyCode.Keypad7: return kb.numpad7Key?.isPressed ?? false;
                    case KeyCode.Keypad8: return kb.numpad8Key?.isPressed ?? false;
                    case KeyCode.Keypad9: return kb.numpad9Key?.isPressed ?? false;
                }
            }

            // Mouse buttons
            var mouse = UnityEngine.InputSystem.Mouse.current;
            return mouse != null && (
                   (k == KeyCode.Mouse0 && mouse.leftButton.isPressed) ||
                   (k == KeyCode.Mouse1 && mouse.rightButton.isPressed) ||
                   (k == KeyCode.Mouse2 && mouse.middleButton.isPressed) ||
                   (k == KeyCode.Mouse3 && (mouse.forwardButton?.isPressed ?? false)) ||
                   (k == KeyCode.Mouse4 && (mouse.backButton?.isPressed ?? false)));
        }

        private KeyChord SnapshotChord()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            bool ctrl = (kb?.leftCtrlKey?.isPressed ?? false) || (kb?.rightCtrlKey?.isPressed ?? false);
            bool shift = (kb?.leftShiftKey?.isPressed ?? false) || (kb?.rightShiftKey?.isPressed ?? false);
            bool alt = (kb?.leftAltKey?.isPressed ?? false) || (kb?.rightAltKey?.isPressed ?? false);

            var keys = new List<KeyCode>();
            foreach (KeyCode k in Enum.GetValues(typeof(KeyCode)))
            {
                if (!IsAllowedKey(k) || IsModifierKey(k)) continue;
                if (IsKeyDown(k)) keys.Add(k);
            }
            keys.Sort((a, b) => a.CompareTo(b));
            return new KeyChord { Keys = keys.ToArray(), Ctrl = ctrl, Shift = shift, Alt = alt };
        }

        private static string KeyChordToSpec(KeyChord kc)
        {
            var sb = new System.Text.StringBuilder();
            if (kc.Ctrl) sb.Append("Ctrl+");
            if (kc.Shift) sb.Append("Shift+");
            if (kc.Alt) sb.Append("Alt+");
            if (kc.Keys != null && kc.Keys.Length > 0)
                sb.Append(string.Join("+", kc.Keys.Select(k => GetFriendlyKeyName(k))));
            return sb.ToString();
        }
        
        private static string GetFriendlyKeyName(KeyCode k)
        {
            switch (k)
            {
                case KeyCode.Mouse0: return "LMB";
                case KeyCode.Mouse1: return "RMB";
                case KeyCode.Mouse2: return "MMB";
                case KeyCode.Mouse3: return "MB4";
                case KeyCode.Mouse4: return "MB5";
                default: return k.ToString();
            }
        }

        private IEnumerator CaptureChordRoutine()
        {
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;

            var kb = UnityEngine.InputSystem.Keyboard.current;
            var mouse = UnityEngine.InputSystem.Mouse.current;

            float startTimeout = Time.unscaledTime + 5f;
            bool started = false;
            float windowEnd = 2f;

            KeyChord best = default;
            int bestWeight = -1;
            float lastBestAt = 0f;

            bool HasAnyInputDown() =>
                (kb != null && kb.anyKey.isPressed) ||
                (mouse != null && (mouse.leftButton.isPressed || mouse.rightButton.isPressed || mouse.middleButton.isPressed ||
                                   (mouse.forwardButton?.isPressed ?? false) || (mouse.backButton?.isPressed ?? false)));

            int Weight(KeyChord kc)
            {
                int w = (kc.Keys?.Length ?? 0);
                if (kc.Ctrl) w++;
                if (kc.Shift) w++;
                if (kc.Alt) w++;
                return w;
            }

            while (_isCapturing && Time.unscaledTime < startTimeout)
            {
                if (kb?.escapeKey?.wasPressedThisFrame ?? false)
                {
                    CancelCapture();
                    yield break;
                }

                if (!started)
                {
                    if ((kb?.anyKey.wasPressedThisFrame ?? false) ||
                        (mouse?.leftButton.wasPressedThisFrame ?? false) ||
                        (mouse?.rightButton.wasPressedThisFrame ?? false) ||
                        (mouse?.middleButton.wasPressedThisFrame ?? false) ||
                        (mouse?.forwardButton?.wasPressedThisFrame ?? false) ||
                        (mouse?.backButton?.wasPressedThisFrame ?? false))
                    {
                        started = true;
                        windowEnd = Time.unscaledTime + 1.0f;
                        if (_captureLabel != null) _captureLabel.text = "Release keys to confirm...";
                    }
                }
                else
                {
                    var kc = SnapshotChord();
                    bool any = (kc.Keys?.Length ?? 0) > 0 || kc.Ctrl || kc.Shift || kc.Alt;
                    if (any)
                    {
                        int w = Weight(kc);
                        if (w > bestWeight)
                        {
                            best = kc; bestWeight = w; lastBestAt = Time.unscaledTime;
                            if (_captureLabel != null) _captureLabel.text = KeyChordToSpec(kc) + " - Release to confirm";
                        }
                    }

                    bool allReleased = !HasAnyInputDown();
                    if (bestWeight >= 0 && (allReleased || Time.unscaledTime >= windowEnd || Time.unscaledTime - lastBestAt > 0.15f))
                    {
                        _onKeyCaptured?.Invoke(KeyChordToSpec(best));
                        _isCapturing = false;
                        if (_captureOverlay != null) _captureOverlay.style.display = DisplayStyle.None;
                        HidePanelDuringCapture(false);
                        yield break;
                    }
                }

                yield return null;
            }

            CancelCapture();
        }
        
        private UITK.Button MakeButton(string text, Action onClick)
        {
            var btn = new UITK.Button(onClick) { text = text.ToUpperInvariant() };
            btn.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            btn.style.height = 50;
            btn.style.marginTop = 8;
            btn.style.marginBottom = 8;
            btn.style.paddingLeft = 18;
            btn.style.paddingRight = 18;
            btn.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            MakeReadable(btn);
            AddButtonFlash(btn);
            return btn;
        }
        
        private UITK.Button MakeDonateButton(string t, Action onClick)
        {
            var b = new UITK.Button(onClick) { text = t.ToUpperInvariant() };
            b.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            b.style.height = 50;
            b.style.marginTop = 8;
            b.style.marginBottom = 8;
            b.style.paddingLeft = 18; b.style.paddingRight = 18;
            b.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            MakeReadable(b);
            AddButtonFlash(b);
            return b;
        }
        
        private UITK.Button MakeResetButton(string t, Action onClick)
        {
            var b = new UITK.Button(onClick) { text = t.ToUpperInvariant() };
            b.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            b.style.height = 50;
            b.style.marginTop = 8;
            b.style.marginBottom = 8;
            b.style.marginLeft = 6; b.style.marginRight = 6;
            b.style.paddingLeft = 18; b.style.paddingRight = 18;
            b.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            MakeReadable(b);
            AddButtonFlash(b);
            return b;
        }

        private UITK.Button MakeCloseButton(string t, Action onClick)
        {
            var b = new UITK.Button(onClick) { text = t.ToUpperInvariant() };
            b.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            b.style.height = 50;
            b.style.marginTop = 8;
            b.style.marginBottom = 8;
            b.style.paddingLeft = 18; b.style.paddingRight = 18;
            b.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            MakeReadable(b);
            AddButtonFlash(b);
            return b;
        }
        
        private void SaveConfig()
        {
            SprayConfigManager.SaveClientConfig(_config);
            
            // Notify input handler to reload binds
            var inputHandler = FindAnyObjectByType<SprayInputHandler>();
            if (inputHandler != null)
            {
                inputHandler.ReloadConfig();
                Debug.Log("[SprayMod] Input handler binds reloaded");
            }
            
            // Notify spray manager to reload general settings
            var sprayManager = SprayManager.Instance;
            if (sprayManager != null)
            {
                sprayManager.ReloadClientConfig();
                Debug.Log("[SprayMod] Spray manager config reloaded");
            }
            
            Debug.Log("[SprayMod] Config saved");
        }
        
        private void ResetToDefaults()
        {
            _config = new SprayClientConfig();
            SaveConfig();
            RefreshUI();
            Debug.Log("[SprayMod] Config reset to defaults");
        }
        
        private static Font GetUIFont()
        {
            if (_uiFont != null) return _uiFont;
            try
            {
                var uiManager = MonoBehaviourSingleton<UIManager>.Instance;
                if (uiManager != null && uiManager.PanelSettings != null)
                {
                    var textSettings = uiManager.PanelSettings.textSettings;
                    if (textSettings != null && textSettings.defaultFontAsset != null)
                    {
                        _uiFont = textSettings.defaultFontAsset.sourceFontFile;
                        if (_uiFont != null) return _uiFont;
                    }
                }
            }
            catch { }
            try { _uiFont = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
            if (_uiFont == null)
            {
                try { _uiFont = Font.CreateDynamicFontFromOSFont(new[] { "Arial", "Segoe UI" }, 16); } catch { }
            }
            return _uiFont;
        }
        
        private static void ForceUIFont(UITK.VisualElement ve)
        {
            var f = GetUIFont();
            if (f != null) ve.style.unityFont = f;
        }
        
        private static void MakeReadable(UITK.Label l)
        {
            l.style.color = Color.white;
            ForceUIFont(l);
        }
        
        private static void MakeReadable(UITK.Button b)
        {
            b.style.color = Color.white;
            ForceUIFont(b);
        }
        
        private static void StyleSlider(UITK.Slider slider)
        {
            slider.style.height = 26;
            slider.style.marginLeft = 6;
            slider.style.marginRight = 6;
            
            var tracker = slider.Q<VisualElement>(className: "unity-slider__tracker");
            var dragger = slider.Q<VisualElement>(className: "unity-slider__dragger");
            if (tracker != null)
            {
                tracker.style.height = 4;
                tracker.style.marginTop = 11;
                tracker.style.backgroundColor = new StyleColor(new Color(1, 1, 1, 0.15f));
                tracker.style.borderTopLeftRadius = 2;
                tracker.style.borderTopRightRadius = 2;
                tracker.style.borderBottomLeftRadius = 2;
                tracker.style.borderBottomRightRadius = 2;
            }
            if (dragger != null)
            {
                dragger.style.width = 18;
                dragger.style.height = 18;
                dragger.style.marginTop = 4;
                dragger.style.backgroundColor = new StyleColor(Color.white);
                dragger.style.borderTopLeftRadius = 9;
                dragger.style.borderTopRightRadius = 9;
                dragger.style.borderBottomLeftRadius = 9;
                dragger.style.borderBottomRightRadius = 9;
            }
        }
        
        private static void AddButtonFlash(UITK.Button b)
        {
            var baseBg = b.style.backgroundColor.value;
            b.focusable = true;
            
            void SetBase()
            {
                b.style.backgroundColor = new StyleColor(baseBg);
                b.style.color = new StyleColor(Color.white);
            }
            
            SetBase();
            bool hover = false, flashing = false;
            
            b.RegisterCallback<PointerEnterEvent>(_ =>
            {
                hover = true;
                b.style.backgroundColor = new StyleColor(Color.white);
                b.style.color = new StyleColor(Color.black);
            });
            
            b.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                hover = false;
                if (!flashing) SetBase();
            });
            
            b.RegisterCallback<PointerUpEvent>(_ =>
            {
                flashing = true;
                b.style.backgroundColor = new StyleColor(Color.white);
                b.style.color = new StyleColor(Color.black);
                b.schedule.Execute(() => { flashing = false; if (!hover) SetBase(); }).StartingIn(140);
            });
        }
        
        private void HideMenuButtons(bool hide)
        {
            if (hide)
            {
                _hiddenMenuElements.Clear();
                
                var main = FindFirstObjectByType<UIMainMenu>(FindObjectsInactive.Include);
                if (main != null && _fiMainSettings != null)
                {
                    var refBtn = _fiMainSettings.GetValue(main) as UITK.Button;
                    if (refBtn?.parent != null)
                    {
                        foreach (var child in refBtn.parent.Children())
                        {
                            if (child.style.display != DisplayStyle.None)
                            {
                                _hiddenMenuElements.Add(child);
                                child.style.display = DisplayStyle.None;
                            }
                        }
                    }
                }
                
                var pause = FindFirstObjectByType<UIPauseMenu>(FindObjectsInactive.Include);
                if (pause != null && _fiPauseSettings != null)
                {
                    var refBtn = _fiPauseSettings.GetValue(pause) as UITK.Button;
                    if (refBtn?.parent != null)
                    {
                        foreach (var child in refBtn.parent.Children())
                        {
                            if (child.style.display != DisplayStyle.None)
                            {
                                _hiddenMenuElements.Add(child);
                                child.style.display = DisplayStyle.None;
                            }
                        }
                    }
                }
            }
            else
            {
                foreach (var elem in _hiddenMenuElements)
                {
                    if (elem != null)
                        elem.style.display = DisplayStyle.Flex;
                }
                _hiddenMenuElements.Clear();
            }
        }
    }
}
