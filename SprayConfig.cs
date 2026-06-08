using System;

namespace SprayMod
{
    /// <summary>
    /// Static configuration constants for the SprayMod (client-side only).
    /// </summary>
    public static class SprayConfig
    {
        // Input configuration
        public const string DEFAULT_SPRAY_KEY = "Z";

        // Spray limits
        public const int MAX_SPRAYS_PER_PLAYER = 10;
        public const float SPRAY_COOLDOWN = 1f; // seconds
        public const float MAX_SPRAY_DISTANCE = 10f; // meters
        public const float SPRAY_LIFETIME = 300f; // fallback lifetime in seconds

        // Texture configuration
        public const int MAX_TEXTURE_SIZE = 512; // Maximum dimension for loaded textures
        public const int DEFAULT_TEXTURE_SIZE = 256; // Size for default/placeholder texture

        // Decal rendering
        public const float DECAL_SIZE = 0.5f; // Base size multiplier
        public const float DECAL_OFFSET = 0.01f; // Offset from surface to prevent z-fighting
        public const float DECAL_FADE_TIME = 1f; // Fade out duration in seconds

        // Folder paths (relative to DLL location)
        public const string IMAGES_FOLDER = "SprayImages";

        // Supported formats
        public static readonly string[] SUPPORTED_IMAGE_FORMATS = { ".png", ".jpg", ".jpeg", ".gif" };

        // Layer masks to ignore for raycasting
        public static readonly string[] IGNORED_LAYERS = { "Player", "Puck", "Stick" };

        // Debug settings
        public const bool ENABLE_DEBUG_LOGS = true;
    }
}
