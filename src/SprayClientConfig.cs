using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace SprayMod
{
    /// <summary>
    /// Client-side configuration. This is the ONLY config the mod uses now -
    /// SprayMod is fully client-side (see spraymod-b897-architecture). Sprays are
    /// shared peer-to-peer by reference over chat; there is no server config.
    /// </summary>
    [Serializable]
    public class SprayClientConfig
    {
        [Description("Enable debug logging")]
        public bool Debug { get; set; } = false;

        [Description("Show spray UI elements")]
        public bool ShowUI { get; set; } = true;

        [Description("Enable spray sound effects")]
        public bool EnableSound { get; set; } = true;

        [Description("Show other players' sprays")]
        public bool ShowOtherPlayerSprays { get; set; } = true;

        [Description("Only show sprays from your Steam friends (off = everyone with the mod)")]
        public bool FriendsOnly { get; set; } = true;

        [Description("Download other players' URL/link sprays from the web")]
        public bool DownloadSharedUrls { get; set; } = true;

        [Description("Share your sprays with other players")]
        public bool ShareSprays { get; set; } = true;

        [Description("Auto-upload spray images to a temporary host so everyone can see them (vs. local-file reference only)")]
        public bool AutoUpload { get; set; } = true;

        [Description("Spray opacity (0.0 - 1.0)")]
        public float SprayOpacity { get; set; } = 1.0f;

        [Description("Spray size multiplier (0.1 - 3.0)")]
        public float SpraySize { get; set; } = 1.0f;

        [Description("Maximum simultaneous sprays per player (oldest removed first, 0 = unlimited)")]
        public int MaxSpraysPerPlayer { get; set; } = 1;

        [Description("Spray lifetime in seconds before it fades out (0 = until limit reached)")]
        public float SprayLifetime { get; set; } = 30f;

        [Description("Minimum seconds between your own sprays")]
        public float SprayCooldown { get; set; } = 1.0f;

        [Description("Keybind to open spray wheel (e.g., 'T', 'F5', 'Mouse3')")]
        public string SprayWheelKey { get; set; } = "Z";

        [Description("Quick spray keybinds - maps a key to a spray name (survives reordering)")]
        public Dictionary<string, string> QuickSprayBinds { get; set; } = new Dictionary<string, string>();
    }
}
