using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace SprayMod
{
    /// <summary>
    /// Loads/saves the whole mod config from a SINGLE file and resolves the folder layout.
    ///
    /// Layout:  &lt;PuckRoot&gt;/config/ModHub/Sprays/
    ///            spraymod.json          (settings + spray list - one file for everything)
    ///            SpraySound.wav
    ///            SprayImages/*.png|jpg|gif   (optional; only if the user adds local files)
    ///
    /// Migrated once from the old split files (spray_client_config.json + sprays.json).
    /// </summary>
    public static class SprayConfigManager
    {
        private const string DATA_FILENAME = "spraymod.json";
        private const string LEGACY_CLIENT_CONFIG_FILENAME = "spray_client_config.json";
        private const string LEGACY_MANIFEST_FILENAME = "sprays.json";
        private const string SPRAY_FOLDER_NAME = "Sprays";
        private const string SPRAY_IMAGES_FOLDER_NAME = "SprayImages";
        private const string SPRAY_SOUND_FILENAME = "SpraySound.wav";

        // ---- folder layout ----

        /// <summary>
        /// Returns &lt;PuckRoot&gt;/config/ModHub/Sprays, creating it if needed.
        /// Migrates from the legacy config/Sprays location once.
        /// </summary>
        public static string GetSprayFolderPath()
        {
            string root = Application.dataPath;
            if (root.EndsWith("Puck_Data"))
            {
                root = Directory.GetParent(root).FullName;
            }

            string configFolder = Path.Combine(root, "config");
            string modHubFolder = Path.Combine(configFolder, "ModHub");
            string sprayFolder = Path.Combine(modHubFolder, SPRAY_FOLDER_NAME);

            // One-time migration from the old config/Sprays location.
            string legacySprayFolder = Path.Combine(configFolder, SPRAY_FOLDER_NAME);
            string legacyMigratedFolder = legacySprayFolder + "_migrated";
            if (Directory.Exists(legacySprayFolder) && !Directory.Exists(legacyMigratedFolder))
            {
                try
                {
                    Directory.CreateDirectory(modHubFolder);
                    CopyDirectory(legacySprayFolder, sprayFolder);
                    Debug.Log($"[SprayMod] Migrated config folder from {legacySprayFolder} to {sprayFolder}");
                    try { Directory.Move(legacySprayFolder, legacyMigratedFolder); }
                    catch (Exception renameEx) { Debug.LogWarning($"[SprayMod] Could not rename old folder: {renameEx.Message}"); }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SprayMod] Failed to migrate config folder: {ex.Message}");
                }
            }

            if (!Directory.Exists(sprayFolder))
            {
                Directory.CreateDirectory(sprayFolder);
            }
            return sprayFolder;
        }

        public static string GetDataPath() => Path.Combine(GetSprayFolderPath(), DATA_FILENAME);
        public static string GetSpraySoundPath() => Path.Combine(GetSprayFolderPath(), SPRAY_SOUND_FILENAME);

        /// <summary>
        /// Path to the optional local-image folder. NOT auto-created - the mod is link-first now,
        /// so this folder only exists if the user adds local image files (see EnsureSprayImagesFolder).
        /// </summary>
        public static string GetSprayImagesFolderPath() => Path.Combine(GetSprayFolderPath(), SPRAY_IMAGES_FOLDER_NAME);

        /// <summary>Creates the local-image folder on demand (e.g. when the user opens it).</summary>
        public static string EnsureSprayImagesFolder()
        {
            string folder = GetSprayImagesFolderPath();
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            return folder;
        }

        // ---- single-file data (settings + sprays) ----

        /// <summary>
        /// Reads spraymod.json. On first run, migrates the old split files (or seeds the shipped
        /// preset links), writes spraymod.json, and retires the old files. Always returns data.
        /// </summary>
        private static SprayModData LoadOrCreateData()
        {
            string dataPath = GetDataPath();

            if (File.Exists(dataPath))
            {
                var existing = ReadJsonFile<SprayModData>(dataPath);
                if (existing != null)
                {
                    if (existing.settings == null) existing.settings = new SprayClientConfig();
                    if (existing.sprays == null) existing.sprays = new List<SpraySpec>();
                    return existing;
                }

                // File exists but is unreadable/corrupt: return safe in-memory defaults but DON'T
                // overwrite the file or retire the old ones - so a transient lock or a recoverable
                // file doesn't silently wipe the user's data.
                Debug.LogError("[SprayMod] spraymod.json is unreadable; using in-memory defaults (file left intact).");
                return new SprayModData
                {
                    settings = new SprayClientConfig(),
                    sprays = ReadDefaultManifest()?.sprays ?? new List<SpraySpec>()
                };
            }

            // First run: migrate the old split files, or seed the shipped preset links.
            var data = new SprayModData();

            var oldSettings = ReadJsonFile<SprayClientConfig>(Path.Combine(GetSprayFolderPath(), LEGACY_CLIENT_CONFIG_FILENAME));
            if (oldSettings != null) data.settings = oldSettings;

            bool hadSprays = false;
            var oldManifest = ReadJsonFile<SprayManifest>(Path.Combine(GetSprayFolderPath(), LEGACY_MANIFEST_FILENAME));
            if (oldManifest?.sprays != null) { data.sprays = oldManifest.sprays; hadSprays = true; }

            if (oldSettings != null || hadSprays)
                Debug.Log("[SprayMod] Migrated old config into spraymod.json");

            // Seed default preset links only when there was no existing spray list at all (fresh
            // install or settings-only migration) - never overwrite an intentionally-empty list.
            if (!hadSprays)
            {
                var def = ReadDefaultManifest();
                if (def?.sprays != null) data.sprays = def.sprays;
            }

            WriteData(data);
            RetireLegacyFile(LEGACY_CLIENT_CONFIG_FILENAME);
            RetireLegacyFile(LEGACY_MANIFEST_FILENAME);
            return data;
        }

        private static void WriteData(SprayModData data)
        {
            try { File.WriteAllText(GetDataPath(), JsonConvert.SerializeObject(data, Formatting.Indented)); }
            catch (Exception ex) { Debug.LogError($"[SprayMod] Error saving spraymod.json: {ex.Message}"); }
        }

        private static void RetireLegacyFile(string filename)
        {
            try
            {
                string path = Path.Combine(GetSprayFolderPath(), filename);
                if (File.Exists(path)) File.Move(path, path + ".migrated");
            }
            catch { }
        }

        private static T ReadJsonFile<T>(string path) where T : class
        {
            try
            {
                if (File.Exists(path)) return JsonConvert.DeserializeObject<T>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SprayMod] Error reading {Path.GetFileName(path)}: {ex.Message}");
            }
            return null;
        }

        // ---- client config ----

        public static SprayClientConfig LoadClientConfig()
        {
            return LoadOrCreateData().settings ?? new SprayClientConfig();
        }

        public static void SaveClientConfig(SprayClientConfig config)
        {
            var data = LoadOrCreateData();
            data.settings = config ?? new SprayClientConfig();
            WriteData(data);
        }

        // ---- spray manifest ----

        /// <summary>
        /// Loads the spray list, then reconciles it: imports any legacy links.txt (once), drops file
        /// entries whose image is gone, and appends new image files found in the optional folder.
        /// </summary>
        public static SprayManifest LoadManifest()
        {
            var data = LoadOrCreateData();
            var manifest = new SprayManifest { sprays = data.sprays ?? new List<SpraySpec>() };

            bool changed = ImportLegacyLinks(manifest);
            changed |= ReconcileFiles(manifest);

            if (changed) SaveManifest(manifest);
            return manifest;
        }

        public static void SaveManifest(SprayManifest manifest)
        {
            if (manifest == null) return;
            var data = LoadOrCreateData();
            data.sprays = manifest.sprays ?? new List<SpraySpec>();
            WriteData(data);
        }

        /// <summary>Reads the shipped preset link list (default_sprays.json next to the DLL).</summary>
        private static SprayManifest ReadDefaultManifest()
        {
            try
            {
                string modDir = SprayUtilities.GetModDirectory();
                if (string.IsNullOrEmpty(modDir)) return null;
                string path = Path.Combine(modDir, "default_sprays.json");
                if (File.Exists(path))
                {
                    Debug.Log($"[SprayMod] Seeding sprays from preset: {path}");
                    return JsonConvert.DeserializeObject<SprayManifest>(File.ReadAllText(path));
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SprayMod] Error reading default_sprays.json: {ex.Message}");
            }
            return null;
        }

        /// <summary>Drops missing file sprays and appends image files not yet in the manifest.</summary>
        private static bool ReconcileFiles(SprayManifest m)
        {
            bool changed = false;
            string imagesFolder = GetSprayImagesFolderPath();

            // Drop file entries whose image no longer exists.
            int removed = m.sprays.RemoveAll(s => !s.IsUrl &&
                (string.IsNullOrEmpty(s.file) || !File.Exists(Path.Combine(imagesFolder, s.file))));
            if (removed > 0) changed = true;

            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in m.sprays) if (!s.IsUrl && !string.IsNullOrEmpty(s.file)) known.Add(s.file);

            if (Directory.Exists(imagesFolder))
            {
                var files = Directory.GetFiles(imagesFolder)
                    .Where(IsSupportedImage)
                    .Select(Path.GetFileName)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

                foreach (var f in files)
                {
                    if (!known.Contains(f))
                    {
                        m.sprays.Add(new SpraySpec { name = Path.GetFileNameWithoutExtension(f), file = f });
                        known.Add(f);
                        changed = true;
                    }
                }
            }
            return changed;
        }

        /// <summary>One-time import of legacy links.txt into the manifest, then renames it.</summary>
        private static bool ImportLegacyLinks(SprayManifest m)
        {
            string imagesFolder = GetSprayImagesFolderPath();
            string path = Path.Combine(imagesFolder, "links.txt");
            if (!File.Exists(path)) return false;

            bool changed = false;
            var knownUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in m.sprays) if (s.IsUrl) knownUrls.Add(s.url);

            foreach (var kv in SprayLibrary.ParseLinksFile(imagesFolder))
            {
                if (!knownUrls.Contains(kv.Value))
                {
                    m.sprays.Add(new SpraySpec { name = kv.Key, url = kv.Value });
                    knownUrls.Add(kv.Value);
                    changed = true;
                }
            }

            try { File.Move(path, path + ".imported"); } catch { }
            return changed;
        }

        private static bool IsSupportedImage(string path)
        {
            foreach (var ext in SprayConfig.SUPPORTED_IMAGE_FORMATS)
                if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                string destFile = Path.Combine(destDir, Path.GetFileName(file));
                if (!File.Exists(destFile)) File.Copy(file, destFile, false);
            }
            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
            }
        }
    }
}
