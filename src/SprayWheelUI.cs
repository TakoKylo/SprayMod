using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace SprayMod
{
    /// <summary>
    /// Native (UI Toolkit) radial spray wheel. Attaches a VisualElement overlay to the
    /// game's own UIDocument so it matches b897's UI and renders on the same panel.
    /// Reads the current library from SprayManager at Show() time and animates GIF
    /// thumbnails by swapping the background image (throttled).
    /// </summary>
    public class SprayWheelUI : MonoBehaviour
    {
        private const float Radius = 185f;
        private const float ItemSize = 96f;

        private static readonly Color BgOverlay = new Color(0f, 0f, 0f, 0.82f);
        private static readonly Color ItemBg = new Color(0.20f, 0.20f, 0.22f, 0.96f);
        private static readonly Color ItemHover = new Color(0.40f, 0.62f, 0.92f, 1f);
        private static readonly Color CenterBg = new Color(0.14f, 0.14f, 0.16f, 0.98f);
        private static readonly Color Accent = new Color(0.40f, 0.70f, 1f, 1f);
        private static readonly Color TextCol = new Color(0.93f, 0.93f, 0.93f, 1f);
        private static readonly Color SubTextCol = new Color(0.62f, 0.62f, 0.66f, 1f);

        private VisualElement _root;       // game's rootVisualElement
        private VisualElement _overlay;    // our full-screen overlay
        private VisualElement _wheel;      // radial container
        private bool _visible;

        private Action<int> _onSelect;
        private Action _onClear;

        // Animated-thumbnail playback state
        private class AnimItem { public VisualElement Element; public SprayLibraryEntry Entry; public int Frame; public float Timer; }
        private readonly List<AnimItem> _animItems = new List<AnimItem>();

        public void Configure(Action<int> onSelect, Action onClear)
        {
            _onSelect = onSelect;
            _onClear = onClear;
        }

        public bool IsVisible() => _visible;

        private bool _prevMouseActive;

        public void Show()
        {
            if (!EnsureRoot()) return;
            Rebuild();
            _overlay.style.display = DisplayStyle.Flex;
            _overlay.BringToFront();
            _visible = true;

            // Free the cursor and suspend gameplay input while the wheel is open.
            _prevMouseActive = SprayUtilities.GetGameMouseActive();
            SprayUtilities.SetGameMouseActive(true);
        }

        public void Hide()
        {
            if (!_visible) return;
            _visible = false;
            CloseAddLinkPrompt();
            if (_overlay != null) _overlay.style.display = DisplayStyle.None;
            _animItems.Clear();
            SprayUtilities.SetGameMouseActive(_prevMouseActive);
        }

        private bool EnsureRoot()
        {
            if (_overlay != null && _root != null && _overlay.parent == _root) return true;

            _root = GetGameRoot();
            if (_root == null) return false;

            // Drop any stale overlay (e.g. if the game's root element was replaced).
            if (_overlay != null) _overlay.RemoveFromHierarchy();

            _overlay = new VisualElement { name = "SprayWheelOverlay" };
            _overlay.style.position = Position.Absolute;
            _overlay.style.left = 0; _overlay.style.right = 0; _overlay.style.top = 0; _overlay.style.bottom = 0;
            _overlay.style.backgroundColor = BgOverlay;
            _overlay.style.flexDirection = FlexDirection.Column; // wheel on top, button bar below
            _overlay.style.alignItems = Align.Center;
            _overlay.style.justifyContent = Justify.Center;
            _overlay.style.display = DisplayStyle.None;
            // Click on empty space closes.
            _overlay.RegisterCallback<ClickEvent>(evt => { if (evt.target == _overlay) Hide(); });

            _root.Add(_overlay);
            return true;
        }

        private static VisualElement GetGameRoot()
        {
            var uiMgr = MonoBehaviourSingleton<UIManager>.Instance;
            // Use the UIDocument's root (the true full-screen panel), exactly like the settings UI.
            // UIManager.RootVisualElement is a narrower sub-container, which made our overlay - and
            // therefore the centred wheel/buttons/prompt - sit in the left part of the screen.
            if (uiMgr != null && uiMgr.UIDocument != null && uiMgr.UIDocument.rootVisualElement != null)
                return uiMgr.UIDocument.rootVisualElement;
            var doc = UnityEngine.Object.FindFirstObjectByType<UIDocument>(FindObjectsInactive.Include);
            return doc != null ? doc.rootVisualElement : null;
        }

        private void Rebuild()
        {
            _overlay.Clear();
            _animItems.Clear();

            var entries = SprayManager.Instance != null ? SprayManager.Instance.Library.Entries : null;
            int count = entries?.Count ?? 0;

            // Grow the ring so items never overlap each other: arc spacing >= item size + gap.
            float radius = Radius;
            if (count > 1)
                radius = Mathf.Max(Radius, count * (ItemSize + 16f) / (2f * Mathf.PI));

            float wheelDim = radius * 2f + ItemSize;
            float barGap = 14f;     // space between ring and button bar
            float barHeight = 40f;  // reserved height for the button row

            // Centre the whole assembly (ring + button bar) with absolute positioning + translate,
            // the same technique the add-link prompt uses (which centres reliably here; flex centring
            // on the game's UI root did not). The ring sits at the top of this box; the bar is pinned
            // to the box's horizontal centre below it, so the buttons line up under the ring.
            var content = new VisualElement { name = "SprayWheelContent" };
            content.style.position = Position.Absolute;
            content.style.left = new Length(50, LengthUnit.Percent);
            content.style.top = new Length(50, LengthUnit.Percent);
            content.style.width = wheelDim;
            content.style.height = wheelDim + barGap + barHeight;
            content.style.translate = new Translate(new Length(-50, LengthUnit.Percent), new Length(-50, LengthUnit.Percent), 0);
            _overlay.Add(content);

            _wheel = new VisualElement { name = "SprayWheel" };
            _wheel.style.position = Position.Absolute;
            _wheel.style.left = 0; _wheel.style.top = 0;
            _wheel.style.width = wheelDim;
            _wheel.style.height = wheelDim;
            content.Add(_wheel);

            float cx = wheelDim / 2f;

            // Center hub
            var center = new VisualElement { name = "Center" };
            float centerSize = 150f;
            Absolute(center, cx - centerSize / 2f, cx - centerSize / 2f);
            center.style.width = centerSize; center.style.height = centerSize;
            center.style.alignItems = Align.Center;
            center.style.justifyContent = Justify.Center;
            Round(center, centerSize / 2f);
            center.style.backgroundColor = CenterBg;
            SetBorder(center, 2f, Accent);
            center.Add(MakeLabel("SELECT", 20, TextCol, true));
            center.Add(MakeLabel("SPRAY", 15, SubTextCol, true));
            center.Add(MakeLabel("Click  •  ESC", 11, SubTextCol, false));
            _wheel.Add(center);

            if (count == 0)
            {
                var empty = MakeLabel("No sprays found — use Open Folder", 14, SubTextCol, false);
                Absolute(empty, 0, cx - 10f);
                empty.style.width = wheelDim;
                empty.style.unityTextAlign = TextAnchor.MiddleCenter;
                _wheel.Add(empty);
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    float angle = (90f - 360f / count * i) * Mathf.Deg2Rad;
                    float x = cx + Mathf.Cos(angle) * radius - ItemSize / 2f;
                    float y = cx - Mathf.Sin(angle) * radius - ItemSize / 2f;
                    _wheel.Add(MakeItem(entries[i], i, x, y));
                }
            }

            // Action bar - pinned to the content box's horizontal centre (left:50% + translateX:-50%,
            // exactly like the prompt) and placed just below the ring, so the buttons are centred on
            // the ring's centre regardless of how the game's UI root lays things out.
            var bar = new VisualElement();
            bar.style.position = Position.Absolute;
            bar.style.top = wheelDim + barGap;
            bar.style.left = new Length(50, LengthUnit.Percent);
            bar.style.translate = new Translate(new Length(-50, LengthUnit.Percent), 0, 0);
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.alignItems = Align.Center;
            bar.Add(MakeButton("ADD LINK", ShowAddLinkPrompt));
            bar.Add(MakeButton("CLEAR ALL", () => { _onClear?.Invoke(); Hide(); }));
            bar.Add(MakeButton("CLOSE", Hide));
            content.Add(bar);
        }

        // ---- add-link prompt (paste an image URL straight from the wheel) ----

        private VisualElement _addLinkPrompt;

        /// <summary>Shows a small centred panel to paste an image URL and add it to the library.</summary>
        private void ShowAddLinkPrompt()
        {
            if (_overlay == null) return;
            CloseAddLinkPrompt();

            var prompt = new VisualElement { name = "AddLinkPrompt" };
            prompt.style.position = Position.Absolute;
            prompt.style.left = new Length(50, LengthUnit.Percent);
            prompt.style.top = new Length(50, LengthUnit.Percent);
            prompt.style.translate = new Translate(new Length(-50, LengthUnit.Percent), new Length(-50, LengthUnit.Percent), 0);
            prompt.style.width = 460;
            prompt.style.paddingLeft = 16; prompt.style.paddingRight = 16;
            prompt.style.paddingTop = 14; prompt.style.paddingBottom = 14;
            prompt.style.backgroundColor = CenterBg;
            prompt.style.flexDirection = FlexDirection.Column;
            Round(prompt, 10f);
            SetBorder(prompt, 2f, Accent);
            prompt.RegisterCallback<ClickEvent>(evt => evt.StopPropagation()); // clicking the panel must not close the wheel

            prompt.Add(MakeLabel("PASTE IMAGE LINK", 16, TextCol, true));
            var hint = MakeLabel("Direct image URL ending in .png / .jpg / .gif — not a gallery page or video.", 11, SubTextCol, false);
            hint.style.whiteSpace = WhiteSpace.Normal;
            hint.style.marginTop = 4; hint.style.marginBottom = 8;
            prompt.Add(hint);

            var linkLabel = MakeLabel("IMAGE LINK", 11, SubTextCol, true);
            linkLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
            linkLabel.style.marginBottom = 2;
            prompt.Add(linkLabel);
            var urlField = MakeInput("AddLinkUrl");
            prompt.Add(urlField);

            var nameLabel = MakeLabel("NAME (OPTIONAL)", 11, SubTextCol, true);
            nameLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
            nameLabel.style.marginTop = 4; nameLabel.style.marginBottom = 2;
            prompt.Add(nameLabel);
            var nameField = MakeInput("AddLinkName");
            nameField.style.marginBottom = 12;
            prompt.Add(nameField);

            void Submit()
            {
                string url = (urlField.value ?? string.Empty).Trim();
                if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    AddLinkSpray(url, (nameField.value ?? string.Empty).Trim());
                    CloseAddLinkPrompt();
                }
                else
                {
                    hint.text = "That isn't a valid http(s) link — paste a full image URL.";
                    hint.style.color = new Color(0.92f, 0.5f, 0.5f);
                }
            }

            // Enter in either field submits.
            urlField.RegisterCallback<KeyDownEvent>(e => { if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) Submit(); });
            nameField.RegisterCallback<KeyDownEvent>(e => { if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) Submit(); });

            var btnRow = new VisualElement();
            btnRow.style.flexDirection = FlexDirection.Row;
            btnRow.style.justifyContent = Justify.Center;
            btnRow.Add(MakeButton("ADD", Submit));
            btnRow.Add(MakeButton("CANCEL", CloseAddLinkPrompt));
            prompt.Add(btnRow);

            _overlay.Add(prompt);
            _addLinkPrompt = prompt;
            prompt.BringToFront();
            urlField.Focus();
        }

        /// <summary>A clearly-styled single-line text input (visible dark box + accent border).</summary>
        private TextField MakeInput(string name)
        {
            var tf = new TextField { name = name };
            tf.style.height = 32;
            tf.style.marginBottom = 6;
            tf.style.fontSize = 14;
            tf.style.color = TextCol;
            tf.RegisterCallback<AttachToPanelEvent>(_ =>
            {
                var input = tf.Q("unity-text-input");
                if (input == null) return;
                input.style.backgroundColor = new Color(0.16f, 0.16f, 0.18f);
                input.style.color = TextCol;
                input.style.fontSize = 14;
                input.style.paddingLeft = 8; input.style.paddingRight = 8;
                input.style.borderTopWidth = 1; input.style.borderBottomWidth = 1;
                input.style.borderLeftWidth = 1; input.style.borderRightWidth = 1;
                var bc = new Color(0.45f, 0.6f, 0.9f);
                input.style.borderTopColor = bc; input.style.borderBottomColor = bc;
                input.style.borderLeftColor = bc; input.style.borderRightColor = bc;
                input.style.borderTopLeftRadius = 6; input.style.borderTopRightRadius = 6;
                input.style.borderBottomLeftRadius = 6; input.style.borderBottomRightRadius = 6;
            });
            return tf;
        }

        private void CloseAddLinkPrompt()
        {
            _addLinkPrompt?.RemoveFromHierarchy();
            _addLinkPrompt = null;
        }

        /// <summary>Appends a named URL spray to the manifest, reloads the library, then rebuilds the wheel.</summary>
        private void AddLinkSpray(string url, string name)
        {
            try
            {
                var manifest = SprayConfigManager.LoadManifest();
                manifest.sprays.Add(new SpraySpec { name = name ?? string.Empty, url = url });
                SprayConfigManager.SaveManifest(manifest);
                SprayManager.Instance?.LoadLibrary(_ => { if (_visible) Rebuild(); });

                // Auto re-host any non-catbox link to a short, permanent URL and save it back into
                // the list (matched by its current URL), so stored links stay clean and shareable.
                if (!SprayManager.IsHostedShortLink(url))
                {
                    SprayManager.Instance?.ShortenLink(url, (newUrl, error) =>
                    {
                        if (string.IsNullOrEmpty(newUrl)) return;
                        var m = SprayConfigManager.LoadManifest();
                        bool changed = false;
                        foreach (var s in m.sprays)
                            if (s.IsUrl && string.Equals(s.url, url, StringComparison.OrdinalIgnoreCase)) { s.url = newUrl; changed = true; }
                        if (changed)
                        {
                            SprayConfigManager.SaveManifest(m);
                            SprayManager.Instance?.LoadLibrary(_ => { if (_visible) Rebuild(); });
                        }
                    });
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SprayMod] Add link from wheel failed: {e.Message}");
            }
        }

        private VisualElement MakeItem(SprayLibraryEntry entry, int index, float x, float y)
        {
            var item = new VisualElement { name = $"SprayItem_{index}" };
            Absolute(item, x, y);
            item.style.width = ItemSize; item.style.height = ItemSize;
            item.style.backgroundColor = ItemBg;
            Round(item, 12f);
            SetBorder(item, 2f, new Color(0, 0, 0, 0.5f));
            item.style.unityBackgroundScaleMode = new StyleEnum<ScaleMode>(ScaleMode.ScaleToFit);
            if (entry.Thumbnail != null)
                item.style.backgroundImage = new StyleBackground(entry.Thumbnail);

            // Number badge
            var badge = MakeLabel((index + 1).ToString(), 12, TextCol, true);
            badge.style.position = Position.Absolute;
            badge.style.left = 6; badge.style.bottom = 4;
            badge.style.paddingLeft = 5; badge.style.paddingRight = 5;
            badge.style.backgroundColor = new Color(0, 0, 0, 0.8f);
            Round(badge, 5f);
            item.Add(badge);

            item.RegisterCallback<MouseEnterEvent>(_ => SetBorder(item, 3f, ItemHover));
            item.RegisterCallback<MouseLeaveEvent>(_ => SetBorder(item, 2f, new Color(0, 0, 0, 0.5f)));
            int captured = index;
            item.RegisterCallback<ClickEvent>(_ => { _onSelect?.Invoke(captured); Hide(); });

            if (entry.IsAnimated)
                _animItems.Add(new AnimItem { Element = item, Entry = entry, Frame = 0, Timer = 0f });

            return item;
        }

        private void Update()
        {
            if (!_visible) return;

            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                Hide();
                return;
            }

            // Animate GIF thumbnails.
            float dt = Time.unscaledDeltaTime;
            for (int i = 0; i < _animItems.Count; i++)
            {
                var a = _animItems[i];
                if (a.Entry.Frames.Count < 2) continue;
                a.Timer += dt;
                float delay = a.Frame < a.Entry.Delays.Count ? a.Entry.Delays[a.Frame] : 0.1f;
                if (a.Timer >= delay)
                {
                    a.Timer = 0f;
                    a.Frame = (a.Frame + 1) % a.Entry.Frames.Count;
                    a.Element.style.backgroundImage = new StyleBackground(a.Entry.Frames[a.Frame]);
                }
            }
        }

        private void OnDestroy()
        {
            if (_overlay != null && _overlay.parent != null) _overlay.RemoveFromHierarchy();
            _overlay = null;
        }

        // ---- small UI helpers ----

        private static void Absolute(VisualElement e, float left, float top)
        {
            e.style.position = Position.Absolute;
            e.style.left = left;
            e.style.top = top;
        }

        private static void Round(VisualElement e, float r)
        {
            e.style.borderTopLeftRadius = r; e.style.borderTopRightRadius = r;
            e.style.borderBottomLeftRadius = r; e.style.borderBottomRightRadius = r;
        }

        private static void SetBorder(VisualElement e, float w, Color c)
        {
            e.style.borderTopWidth = w; e.style.borderBottomWidth = w;
            e.style.borderLeftWidth = w; e.style.borderRightWidth = w;
            e.style.borderTopColor = c; e.style.borderBottomColor = c;
            e.style.borderLeftColor = c; e.style.borderRightColor = c;
        }

        private static Label MakeLabel(string text, int size, Color color, bool bold)
        {
            var l = new Label(text);
            l.style.fontSize = size;
            l.style.color = color;
            l.style.unityFontStyleAndWeight = bold ? FontStyle.Bold : FontStyle.Normal;
            l.style.unityTextAlign = TextAnchor.MiddleCenter;
            return l;
        }

        private VisualElement MakeButton(string label, Action onClick)
        {
            var b = new Button(() => onClick?.Invoke()) { text = label };
            b.style.height = 34;
            b.style.minWidth = 110;
            b.style.marginLeft = 6; b.style.marginRight = 6;
            b.style.paddingTop = 0; b.style.paddingBottom = 0;
            b.style.paddingLeft = 10; b.style.paddingRight = 10;
            b.style.backgroundColor = ItemBg;
            b.style.color = TextCol;
            b.style.unityFontStyleAndWeight = FontStyle.Bold;
            b.style.unityTextAlign = TextAnchor.MiddleCenter; // centre the label in the button (was top-left)
            b.style.whiteSpace = WhiteSpace.NoWrap;
            b.style.fontSize = 13;
            Round(b, 8f);
            SetBorder(b, 1f, new Color(1, 1, 1, 0.12f));
            b.RegisterCallback<MouseEnterEvent>(_ => SetBorder(b, 2f, Accent));
            b.RegisterCallback<MouseLeaveEvent>(_ => SetBorder(b, 1f, new Color(1, 1, 1, 0.12f)));
            return b;
        }
    }
}
