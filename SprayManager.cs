using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Networking;

namespace SprayMod
{
    /// <summary>
    /// Client-side spray manager. Owns the spray image library, spawns decals,
    /// and bridges to the chat-based peer sync (SprayChatSync). There is no
    /// server-side component anymore: each client renders sprays from its OWN
    /// library, matched by content hash (see spraymod-b897-architecture).
    /// </summary>
    public class SprayManager : MonoBehaviour
    {
        public static SprayManager Instance { get; private set; }

        private const int MAX_TEXTURE_SIZE = SprayConfig.MAX_TEXTURE_SIZE;

        private SprayClientConfig clientConfig;
        public SprayClientConfig Config => clientConfig;

        public readonly SprayLibrary Library = new SprayLibrary();

        // Active decals keyed by owner SteamID ("" = local/standalone).
        private readonly Dictionary<string, List<SprayDecal>> playerSprays = new Dictionary<string, List<SprayDecal>>();
        private float lastLocalSprayTime = -999f;

        private Transform sprayContainer;
        private AudioSource sprayAudioSource;
        private AudioClip spraySound;
        private Texture2D placeholderTexture;

        // URL spray cache (downloaded images shared by link). Keyed by URL.
        private readonly Dictionary<string, SprayLibraryEntry> urlCache = new Dictionary<string, SprayLibraryEntry>();
        private readonly HashSet<string> pendingUrls = new HashSet<string>();
        private const int MAX_URL_CACHE = 64;
        private const int MAX_DOWNLOAD_BYTES = 8 * 1024 * 1024; // 8 MB safety cap

        public bool IsLibraryLoading => Library.IsLoading;
        public int LibraryCount => Library.Count;

        public void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            clientConfig = SprayConfigManager.LoadClientConfig();
            SprayUtilities.SetConfig(clientConfig);

            var container = new GameObject("SprayContainer");
            sprayContainer = container.transform;
            DontDestroyOnLoad(container);

            sprayAudioSource = container.AddComponent<AudioSource>();
            sprayAudioSource.playOnAwake = false;
            sprayAudioSource.spatialBlend = 0f;
            sprayAudioSource.volume = 0.5f;

            LoadSpraySound();
            placeholderTexture = CreatePlaceholderTexture();

            SprayUtilities.DebugLog("SprayManager initialized");
        }

        private void OnDestroy()
        {
            ClearAllSprays();
            Library.Clear();
            ClearUrlCache();
        }

        private void ClearUrlCache()
        {
            foreach (var e in urlCache.Values) e.Dispose();
            urlCache.Clear();
            pendingUrls.Clear();
        }

        /// <summary>Reloads the client config (called when settings change mid-game).</summary>
        public void ReloadClientConfig()
        {
            clientConfig = SprayConfigManager.LoadClientConfig();
            SprayUtilities.SetConfig(clientConfig);
            ApplyDisplaySettingsToAll(); // pick up size/opacity changes on already-placed sprays
        }

        private Coroutine loadRoutine;
        private bool reloadQueued;
        private Action<bool> queuedCallback;
        private int loadGeneration;

        /// <summary>
        /// Loads (or reloads) the whole spray library without hitching the game. Re-entrant safe:
        /// if a load is already running (e.g. rapid edits in the settings UI), the new request is
        /// coalesced and run once after the current one finishes.
        /// </summary>
        public void LoadLibrary(Action<bool> onComplete = null)
        {
            if (loadRoutine != null)
            {
                reloadQueued = true;
                queuedCallback = onComplete;
                return;
            }
            loadRoutine = StartCoroutine(LoadLibraryRoutine(onComplete));
        }

        private IEnumerator LoadLibraryRoutine(Action<bool> onComplete)
        {
            int gen = ++loadGeneration;

            // Placed decals reference library/url textures; reloading disposes those textures,
            // so clear the decals first to avoid dangling (blank) sprays on the map.
            ClearAllSprays();
            ClearUrlCache();
            var manifest = SprayConfigManager.LoadManifest();
            string imagesFolder = SprayConfigManager.GetSprayImagesFolderPath();

            // Split the manifest into file sprays (loaded here, in order) and URL sprays
            // (downloaded below). Each carries its manifest index as Order so URL sprays that
            // finish later still slot into the correct wheel position.
            var fileLoads = new List<SprayFileLoad>();
            var urlSpecs = new List<KeyValuePair<int, SpraySpec>>();
            for (int i = 0; i < manifest.sprays.Count; i++)
            {
                var spec = manifest.sprays[i];
                if (spec.IsUrl)
                {
                    urlSpecs.Add(new KeyValuePair<int, SpraySpec>(i, spec));
                }
                else if (!string.IsNullOrEmpty(spec.file))
                {
                    fileLoads.Add(new SprayFileLoad
                    {
                        Path = Path.Combine(imagesFolder, spec.file),
                        Name = spec.DisplayName,
                        Order = i
                    });
                }
            }

            bool ok = false;
            yield return Library.LoadFilesAsync(fileLoads, MAX_TEXTURE_SIZE, s => ok = s);

            foreach (var kv in urlSpecs)
            {
                int order = kv.Key;
                string nm = kv.Value.DisplayName;
                string url = kv.Value.url;
                EnsureUrlSpray(url, nm, entry =>
                {
                    if (gen != loadGeneration) return; // a newer reload superseded this one
                    if (entry != null && Library.IndexOf(entry) < 0)
                    {
                        entry.Name = nm;
                        entry.Order = order;
                        Library.AddEntry(entry);
                    }
                });
            }

            loadRoutine = null;
            onComplete?.Invoke(ok);

            if (reloadQueued)
            {
                reloadQueued = false;
                var cb = queuedCallback;
                queuedCallback = null;
                LoadLibrary(cb);
            }
        }

        public SprayLibraryEntry GetEntry(int index) => Library.Get(index);

        /// <summary>
        /// Index of a spray in the loaded library by its stable source (file name or URL).
        /// Used for quick binds so they survive renames and reordering.
        /// </summary>
        public int IndexOfSource(string source)
        {
            if (string.IsNullOrEmpty(source)) return -1;
            var entries = Library.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                if (string.Equals(entries[i].Url, source, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entries[i].FileName, source, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Applies renamed/reordered manifest metadata to already-loaded entries without a full
        /// reload (so reorder/rename don't re-download URL sprays). Matches entries by source.
        /// </summary>
        public void SyncMetadata(SprayManifest manifest)
        {
            if (manifest == null) return;
            foreach (var e in Library.Entries)
            {
                for (int i = 0; i < manifest.sprays.Count; i++)
                {
                    var s = manifest.sprays[i];
                    bool match = s.IsUrl
                        ? string.Equals(s.url, e.Url, StringComparison.OrdinalIgnoreCase)
                        : string.Equals(s.file, e.FileName, StringComparison.OrdinalIgnoreCase);
                    if (match)
                    {
                        e.Name = s.DisplayName;
                        e.Order = i;
                        break;
                    }
                }
            }
            Library.Resort();
        }

        /// <summary>Best-effort thumbnail for a manifest spec (placeholder if not loaded yet).</summary>
        public Texture2D FindThumbnailForSpec(SpraySpec spec)
        {
            if (spec != null)
            {
                if (spec.IsUrl)
                {
                    if (urlCache.TryGetValue(spec.url, out var e) && e.Thumbnail != null) return e.Thumbnail;
                }
                else if (!string.IsNullOrEmpty(spec.file) && Library.TryGetByFileName(spec.file, out var fe) && fe.Thumbnail != null)
                {
                    return fe.Thumbnail;
                }
            }
            return placeholderTexture;
        }

        // ---- placing sprays ----

        /// <summary>Local player requests to place spray <paramref name="index"/> from their library.</summary>
        public void RequestSpray(Vector3 origin, Vector3 direction, int index)
        {
            float cooldown = clientConfig?.SprayCooldown ?? 1f;
            if (Time.time - lastLocalSprayTime < cooldown)
            {
                SprayUtilities.DebugLog("Spray on cooldown");
                return;
            }

            SprayLibraryEntry entry = Library.Get(index);
            if (entry == null)
            {
                SprayUtilities.DebugLog($"No spray at index {index}");
                return;
            }

            if (!Raycast(origin, direction, out RaycastHit hit)) return;

            lastLocalSprayTime = Time.time;

            Vector3 normal = hit.normal;
            float scale = clientConfig?.SpraySize ?? 1f; // sent over the wire (informational; receivers use their own size)
            string steamId = GetLocalSteamId();

            SpawnDecal(steamId, entry, hit.point, normal);

            // Share placement if enabled and connected to a server.
            bool connected = NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient;
            if (connected && (clientConfig == null || clientConfig.ShareSprays))
            {
                Vector3 p = hit.point, n = normal;
                float s = scale;
                bool autoUpload = clientConfig == null || clientConfig.AutoUpload;

                if (entry.IsUrl && SprayChatSync.UrlFitsInChat(entry.Url))
                {
                    // Short link: broadcast it directly.
                    SprayChatSync.Instance?.SendSprayUrl(entry.Url, p, n, s);
                }
                else if (autoUpload)
                {
                    // File sprays AND over-long links (e.g. Discord CDN) -> resolve a short hosted
                    // URL (upload the file, or download+re-upload the long link), then broadcast it.
                    GetSprayUrl(entry, url =>
                    {
                        if (!string.IsNullOrEmpty(url) && SprayChatSync.UrlFitsInChat(url))
                            SprayChatSync.Instance?.SendSprayUrl(url, p, n, s);
                        else if (!entry.IsUrl)
                            SprayChatSync.Instance?.SendSprayHash(entry.Hash, p, n, s); // file fallback
                    });
                }
                else if (entry.IsUrl)
                {
                    // Auto-upload off: try the link directly (dropped if too long for chat).
                    SprayChatSync.Instance?.SendSprayUrl(entry.Url, p, n, s);
                }
                else
                {
                    // Auto-upload off: reference-only - only players with the same file see it.
                    SprayChatSync.Instance?.SendSprayHash(entry.Hash, p, n, s);
                }
            }
        }

        // ---- auto-upload (so file sprays are visible to everyone) ----

        private const int MAX_UPLOAD_BYTES = 5 * 1024 * 1024; // 5 MB
        private const string LITTERBOX_API = "https://litterbox.catbox.moe/resources/internals/api.php";
        private const string LITTERBOX_TIME = "72h"; // temporary host; sprays are ephemeral anyway

        private readonly Dictionary<string, string> uploadCache = new Dictionary<string, string>();           // content hash -> uploaded URL
        private readonly Dictionary<string, List<Action<string>>> uploadWaiters = new Dictionary<string, List<Action<string>>>();
        private readonly HashSet<string> uploadingHashes = new HashSet<string>();

        /// <summary>
        /// Resolves a short, shareable URL for a spray and calls back with it (or null if it can't
        /// be shared):
        ///  - URL spray that fits in chat -> the URL itself
        ///  - file spray -> upload the file to litterbox
        ///  - over-long URL spray -> download the image then re-upload to litterbox (short URL)
        /// Results are cached by content hash so each image is uploaded at most once per session.
        /// </summary>
        private void GetSprayUrl(SprayLibraryEntry entry, Action<string> onReady)
        {
            if (entry == null) { onReady?.Invoke(null); return; }
            if (entry.IsUrl && SprayChatSync.UrlFitsInChat(entry.Url)) { onReady?.Invoke(entry.Url); return; }

            string h = entry.Hash;
            string filePath = null, sourceUrl = null;

            if (entry.IsUrl)
            {
                sourceUrl = entry.Url; // too long -> re-host
            }
            else if (!string.IsNullOrEmpty(entry.FilePath) && File.Exists(entry.FilePath))
            {
                filePath = entry.FilePath;
            }
            else
            {
                onReady?.Invoke(null);
                return;
            }

            if (string.IsNullOrEmpty(h)) { onReady?.Invoke(null); return; }
            if (uploadCache.TryGetValue(h, out var cached)) { onReady?.Invoke(cached); return; }

            if (!uploadWaiters.TryGetValue(h, out var list))
            {
                list = new List<Action<string>>();
                uploadWaiters[h] = list;
            }
            list.Add(onReady);

            if (!uploadingHashes.Contains(h))
            {
                uploadingHashes.Add(h);
                StartCoroutine(UploadCoroutine(h, filePath, sourceUrl, entry.FileName));
            }
        }

        private IEnumerator UploadCoroutine(string hash, string filePath, string sourceUrl, string fileName)
        {
            string resultUrl = null;
            byte[] bytes = null;

            if (!string.IsNullOrEmpty(filePath))
            {
                var readTask = System.Threading.Tasks.Task.Run(() =>
                {
                    try { return File.ReadAllBytes(filePath); } catch { return null; }
                });
                while (!readTask.IsCompleted) yield return null;
                try { bytes = readTask.Result; } catch { }
            }
            else if (!string.IsNullOrEmpty(sourceUrl))
            {
                using (var dl = UnityWebRequest.Get(sourceUrl))
                {
                    dl.timeout = 25;
                    yield return dl.SendWebRequest();
                    if (dl.result == UnityWebRequest.Result.Success) bytes = dl.downloadHandler.data;
                    else SprayUtilities.DebugLog($"Re-host download failed: {dl.error} ({sourceUrl})");
                }
            }

            if (bytes != null && bytes.Length > 0 && bytes.Length <= MAX_UPLOAD_BYTES)
            {
                var form = new List<IMultipartFormSection>
                {
                    new MultipartFormDataSection("reqtype", "fileupload"),
                    new MultipartFormDataSection("time", LITTERBOX_TIME),
                    new MultipartFormFileSection("fileToUpload", bytes, string.IsNullOrEmpty(fileName) ? "spray.png" : fileName, "application/octet-stream")
                };
                using (var req = UnityWebRequest.Post(LITTERBOX_API, form))
                {
                    req.timeout = 30;
                    yield return req.SendWebRequest();
                    if (req.result == UnityWebRequest.Result.Success)
                    {
                        string text = req.downloadHandler.text?.Trim();
                        if (!string.IsNullOrEmpty(text) && text.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                            resultUrl = text;
                        else
                            SprayUtilities.DebugLog($"Upload returned unexpected response: {text}");
                    }
                    else SprayUtilities.DebugLog($"Spray upload failed: {req.error}");
                }
            }
            else SprayUtilities.DebugLog($"Spray too large or unreadable to upload: {filePath ?? sourceUrl}");

            uploadingHashes.Remove(hash);
            if (!string.IsNullOrEmpty(resultUrl)) uploadCache[hash] = resultUrl;

            if (uploadWaiters.TryGetValue(hash, out var waiters))
            {
                uploadWaiters.Remove(hash);
                foreach (var cb in waiters)
                {
                    try { cb?.Invoke(resultUrl); } catch (Exception e) { SprayUtilities.DebugLog($"upload waiter failed: {e.Message}"); }
                }
            }
        }

        /// <summary>
        /// A spray placed by a remote player, received over chat.
        /// URL sprays are downloaded (and cached); hash sprays are matched against the
        /// local library, falling back to a placeholder when we don't have the image.
        /// </summary>
        public void RemoteSpray(string steamId, bool isUrl, string reference, Vector3 position, Vector3 normal, float scale)
        {
            if (clientConfig != null && !clientConfig.ShowOtherPlayerSprays) return;
            // Friends-only filter: ignore sprays from non-friends unless the player opted into "everyone".
            if (clientConfig != null && clientConfig.FriendsOnly && !IsSteamFriend(steamId)) return;

            if (isUrl)
            {
                if (clientConfig != null && !clientConfig.DownloadSharedUrls) return;
                EnsureUrlSpray(reference, reference, entry =>
                {
                    if (entry != null) SpawnDecal(steamId, entry, position, normal);
                });
            }
            else if (Library.TryGetByHash(reference, out SprayLibraryEntry entry))
            {
                SpawnDecal(steamId, entry, position, normal);
            }
            else
            {
                SpawnDecalTexture(steamId, placeholderTexture, null, null, position, normal);
            }
        }

        /// <summary>True if the given SteamID is one of the local player's Steam friends.</summary>
        public static bool IsSteamFriend(string steamId)
        {
            if (string.IsNullOrEmpty(steamId)) return false;
            if (steamId == GetLocalSteamId()) return true; // our own sprays always pass
            if (!ulong.TryParse(steamId, out ulong id)) return false;
            try
            {
                return Steamworks.SteamFriends.GetFriendRelationship(new Steamworks.CSteamID(id))
                       == Steamworks.EFriendRelationship.k_EFriendRelationshipFriend;
            }
            catch
            {
                return false;
            }
        }

        // ---- live display settings (size/opacity apply to ALL placed sprays) ----

        /// <summary>Updates size/opacity in the live config and re-applies them to every placed spray.</summary>
        public void ApplyDisplaySettings(float size, float opacity)
        {
            if (clientConfig != null)
            {
                clientConfig.SpraySize = size;
                clientConfig.SprayOpacity = opacity;
            }
            foreach (var kvp in playerSprays)
                foreach (var s in kvp.Value)
                    if (s != null) s.SetDisplay(size, opacity);
        }

        private void ApplyDisplaySettingsToAll()
        {
            ApplyDisplaySettings(clientConfig?.SpraySize ?? 1f, clientConfig?.SprayOpacity ?? 1f);
        }

        // ---- URL spray download pipeline ----

        /// <summary>
        /// Ensures a URL spray is downloaded and cached, then invokes <paramref name="onReady"/>
        /// with the entry (or null on failure / when capacity is reached). Lag-free: image/GIF
        /// decode happens off-thread, GPU upload is time-sliced.
        /// </summary>
        public void EnsureUrlSpray(string url, string name, Action<SprayLibraryEntry> onReady)
        {
            if (string.IsNullOrEmpty(url)) { onReady?.Invoke(null); return; }
            // Only allow web URLs (never file://, etc. - these can arrive from other players' chat).
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                onReady?.Invoke(null);
                return;
            }
            if (urlCache.TryGetValue(url, out var cached)) { onReady?.Invoke(cached); return; }
            if (pendingUrls.Contains(url)) { onReady?.Invoke(null); return; }
            if (urlCache.Count >= MAX_URL_CACHE) { onReady?.Invoke(null); return; }
            StartCoroutine(DownloadSprayCoroutine(url, name, onReady));
        }

        private IEnumerator DownloadSprayCoroutine(string url, string name, Action<SprayLibraryEntry> onReady)
        {
            pendingUrls.Add(url);
            SprayLibraryEntry entry = null;
            string urlHash = SprayLibrary.ComputeHash(System.Text.Encoding.UTF8.GetBytes(url));
            string cachePath = GetUrlCacheFilePath(urlHash);

            byte[] data = null;
            bool fromCache = false;

            // 1) Disk cache first - instant, and works offline once an image has been fetched.
            if (File.Exists(cachePath))
            {
                var readTask = System.Threading.Tasks.Task.Run(() =>
                {
                    try { return File.ReadAllBytes(cachePath); } catch { return null; }
                });
                while (!readTask.IsCompleted) yield return null;
                try { data = readTask.Result; } catch { }
                fromCache = data != null && data.Length > 0;
            }

            // 2) Otherwise fetch from the web. We only cache the bytes AFTER a successful decode
            // (below) so a non-image response - e.g. an HTML page from a gallery/page link - never
            // poisons the disk cache.
            if (data == null || data.Length == 0)
            {
                using (var req = UnityWebRequest.Get(url))
                {
                    req.timeout = 20;
                    yield return req.SendWebRequest();
                    if (req.result == UnityWebRequest.Result.Success)
                        data = req.downloadHandler.data;
                    else
                        Debug.LogWarning($"[SprayMod] Spray link download failed: {req.error} ({url})");
                }
            }

            // 3) Decode (off-thread, downscaled) and upload time-sliced - same path as file sprays.
            if (data != null && data.Length > 0 && data.Length <= MAX_DOWNLOAD_BYTES)
            {
                var task = System.Threading.Tasks.Task.Run(() => GifDecoder.DecodeBytes(data, MAX_TEXTURE_SIZE));
                while (!task.IsCompleted) yield return null;
                DecodedImage img = null;
                try { img = task.Result; } catch { img = null; }

                if (img != null && img.Frames.Count > 0)
                {
                    entry = new SprayLibraryEntry { Url = url, FileName = name, Name = name, Hash = urlHash };
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    for (int f = 0; f < img.Frames.Count; f++)
                    {
                        var tex = new Texture2D(img.Width, img.Height, TextureFormat.RGBA32, false);
                        tex.wrapMode = TextureWrapMode.Clamp;
                        tex.SetPixels32(img.Frames[f]);
                        tex.Apply(false, false);
                        entry.Frames.Add(tex);
                        entry.Delays.Add(f < img.Delays.Count ? img.Delays[f] : 0.1f);
                        if (sw.Elapsed.TotalMilliseconds >= 3.0) { yield return null; sw.Restart(); }
                    }
                }
                else
                {
                    // Fallback for formats System.Drawing can't read (e.g. WEBP).
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (tex.LoadImage(data))
                    {
                        tex.wrapMode = TextureWrapMode.Clamp;
                        entry = new SprayLibraryEntry { Url = url, FileName = name, Name = name, Hash = urlHash };
                        entry.Frames.Add(tex);
                        entry.Delays.Add(0.1f);
                    }
                    else { UnityEngine.Object.Destroy(tex); Debug.LogWarning($"[SprayMod] Could not decode spray link as an image - is it a DIRECT image URL (ending in .png/.jpg/.gif), not a gallery/web page? {url}"); }
                }
            }
            else SprayUtilities.DebugLog($"URL image unavailable or too large: {url}");

            if (entry != null)
            {
                // Cache the raw bytes for instant/offline loads next time - only ever good images.
                if (!fromCache && data != null && data.Length > 0 && data.Length <= MAX_DOWNLOAD_BYTES)
                    WriteUrlCache(cachePath, data);
            }
            else
            {
                // Nothing decoded: don't keep an undecodable/garbage response (e.g. HTML) on disk.
                try { if (File.Exists(cachePath)) File.Delete(cachePath); } catch { }
            }

            pendingUrls.Remove(url);
            if (entry != null) urlCache[url] = entry;
            onReady?.Invoke(entry);
        }

        // ---- transparent on-disk cache for downloaded link images (persists across sessions) ----

        private static string GetUrlCacheFilePath(string urlHash)
        {
            return Path.Combine(SprayConfigManager.GetSprayFolderPath(), ".cache", urlHash);
        }

        private static void WriteUrlCache(string path, byte[] data)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, data);
            }
            catch (Exception e)
            {
                SprayUtilities.DebugLog($"Cache write failed: {e.Message}");
            }
        }

        private bool Raycast(Vector3 origin, Vector3 direction, out RaycastHit hit)
        {
            int layerMask = ~LayerMask.GetMask(SprayConfig.IGNORED_LAYERS);
            return Physics.Raycast(origin, direction, out hit, SprayConfig.MAX_SPRAY_DISTANCE, layerMask);
        }

        private void SpawnDecal(string steamId, SprayLibraryEntry entry, Vector3 position, Vector3 normal)
        {
            List<Texture2D> frames = entry.IsAnimated ? entry.Frames : null;
            List<float> delays = entry.IsAnimated ? entry.Delays : null;
            SpawnDecalTexture(steamId, entry.Thumbnail, frames, delays, position, normal);
        }

        private void SpawnDecalTexture(string steamId, Texture2D texture, List<Texture2D> frames, List<float> delays,
            Vector3 position, Vector3 normal)
        {
            if (texture == null) texture = placeholderTexture;

            // Size & opacity are LOCAL display settings, applied uniformly to every spray (yours
            // and others') on this client - not the placing player's values.
            float opacity = clientConfig?.SprayOpacity ?? 1f;
            float displayScale = clientConfig?.SpraySize ?? 1f;
            Quaternion rotation = Quaternion.LookRotation(-normal);
            var data = new SprayData(steamId, position, rotation, normal, 0, displayScale, opacity);

            var sprayObj = new GameObject($"Spray_{data.SprayId}");
            sprayObj.transform.SetParent(sprayContainer);

            var spray = sprayObj.AddComponent<SprayDecal>();
            float lifetime = clientConfig?.SprayLifetime ?? 0f;

            if (frames != null && frames.Count > 1)
                spray.Initialize(data, texture, lifetime, frames, delays);
            else
                spray.Initialize(data, texture, lifetime);

            if ((clientConfig == null || clientConfig.EnableSound) && spraySound != null && sprayAudioSource != null)
                sprayAudioSource.PlayOneShot(spraySound);

            string key = steamId ?? string.Empty;
            if (!playerSprays.TryGetValue(key, out var list))
            {
                list = new List<SprayDecal>();
                playerSprays[key] = list;
            }
            list.Add(spray);
            EnforceSprayLimit(key);
        }

        private void EnforceSprayLimit(string steamId)
        {
            if (!playerSprays.TryGetValue(steamId, out var sprays)) return;
            sprays.RemoveAll(s => s == null);

            int max = clientConfig?.MaxSpraysPerPlayer ?? SprayConfig.MAX_SPRAYS_PER_PLAYER;
            if (max <= 0) return; // unlimited

            while (sprays.Count > max)
            {
                var oldest = sprays[0];
                sprays.RemoveAt(0);
                if (oldest != null) Destroy(oldest.gameObject);
            }
        }

        // ---- identity ----

        public static string GetLocalSteamId()
        {
            try
            {
                Player local = GetLocalPlayer();
                if (local != null && local.SteamId != null)
                {
                    string id = local.SteamId.Value.ToString();
                    if (!string.IsNullOrEmpty(id)) return id;
                }
            }
            catch { }
            return string.Empty;
        }

        public static Player GetLocalPlayer()
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsClient) return null;
            ulong localId = NetworkManager.Singleton.LocalClientId;
            foreach (var p in UnityEngine.Object.FindObjectsByType<Player>(FindObjectsSortMode.None))
            {
                if (p.OwnerClientId == localId) return p;
            }
            return null;
        }

        // ---- clearing ----

        public void ClearLocalSprays()
        {
            ClearPlayerSprays(GetLocalSteamId());
        }

        public void ClearPlayerSprays(string steamId)
        {
            string key = steamId ?? string.Empty;
            if (playerSprays.TryGetValue(key, out var sprays))
            {
                foreach (var s in sprays)
                    if (s != null) Destroy(s.gameObject);
                sprays.Clear();
            }
        }

        public void ClearAllSprays()
        {
            foreach (var kvp in playerSprays)
                foreach (var s in kvp.Value)
                    if (s != null) Destroy(s.gameObject);
            playerSprays.Clear();
        }

        // ---- placeholder texture (shown when a referenced spray isn't in our library) ----

        private static Texture2D CreatePlaceholderTexture()
        {
            int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color32[size * size];
            Color32 magenta = new Color32(220, 40, 220, 200);
            Color32 dark = new Color32(40, 40, 40, 200);
            int cell = size / 8;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    px[y * size + x] = (((x / cell) + (y / cell)) % 2 == 0) ? magenta : dark;
            tex.SetPixels32(px);
            tex.Apply(false, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            return tex;
        }

        // ---- spray sound ----

        private void LoadSpraySound()
        {
            try
            {
                string wavPath = SprayConfigManager.GetSpraySoundPath();
                if (File.Exists(wavPath))
                {
                    StartCoroutine(LoadWavAudio(wavPath));
                    return;
                }
                CreateBeepSound();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SprayManager] Error loading spray sound: {e.Message}");
                CreateBeepSound();
            }
        }

        private IEnumerator LoadWavAudio(string path)
        {
            string url = "file:///" + path.Replace("\\", "/");
            using (var www = UnityEngine.Networking.UnityWebRequestMultimedia.GetAudioClip(url, UnityEngine.AudioType.WAV))
            {
                ((UnityEngine.Networking.DownloadHandlerAudioClip)www.downloadHandler).streamAudio = false;
                yield return www.SendWebRequest();

                if (www.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    AudioClip clip = UnityEngine.Networking.DownloadHandlerAudioClip.GetContent(www);
                    if (clip != null && clip.length > 0.01f) spraySound = clip;
                    else CreateBeepSound();
                }
                else
                {
                    CreateBeepSound();
                }
            }
        }

        private void CreateBeepSound()
        {
            int sampleRate = 44100;
            int sampleCount = (int)(sampleRate * 0.3f);
            var samples = new float[sampleCount];
            var random = new System.Random();
            for (int i = 0; i < sampleCount; i++)
            {
                float t = (float)i / sampleCount;
                float noise = (float)random.NextDouble() * 2f - 1f;
                float envelope = 1f;
                if (t < 0.05f) envelope = t / 0.05f;
                else if (t > 0.8f) envelope = (1f - t) / 0.2f;
                samples[i] = noise * envelope * 0.2f;
            }
            spraySound = AudioClip.Create("SpraySound", sampleCount, 1, sampleRate, false);
            spraySound.SetData(samples, 0);
        }
    }
}
