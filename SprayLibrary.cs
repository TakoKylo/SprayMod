using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace SprayMod
{
    /// <summary>One loaded spray image (static = 1 frame, animated GIF = many frames).</summary>
    public class SprayLibraryEntry
    {
        public string FilePath;
        public string FileName;                               // image filename (file sprays) - used for lookup/reconcile
        public string Name;                                   // friendly display name (from the manifest)
        public string Hash;                                   // 16 hex chars = content reference id used for P2P matching
        public string Url;                                    // non-null for URL-backed sprays
        public int Order;                                     // position in the manifest (wheel order)
        public readonly List<Texture2D> Frames = new List<Texture2D>();
        public readonly List<float> Delays = new List<float>();

        public bool IsUrl => !string.IsNullOrEmpty(Url);
        public Texture2D Thumbnail => Frames.Count > 0 ? Frames[0] : null;
        public bool IsAnimated => Frames.Count > 1;
        public string Label => !string.IsNullOrEmpty(Name) ? Name : FileName;

        public void Dispose()
        {
            foreach (var t in Frames)
                if (t != null) UnityEngine.Object.Destroy(t);
            Frames.Clear();
            Delays.Clear();
        }
    }

    /// <summary>A file to load, in manifest order.</summary>
    public struct SprayFileLoad
    {
        public string Path;
        public string Name;
        public int Order;
    }

    /// <summary>
    /// Holds the spray image library and loads it without hitching the game: file I/O,
    /// hashing and image/GIF decode happen on a background thread; only the cheap GPU
    /// upload (SetPixels32/Apply) runs on the main thread, time-sliced across frames.
    /// Entries are kept sorted by their manifest Order, so URL sprays that finish
    /// downloading later still land in the right place.
    /// </summary>
    public class SprayLibrary
    {
        private const double UploadBudgetMs = 3.0;
        private const int MaxConcurrentDecodes = 3;

        public readonly List<SprayLibraryEntry> Entries = new List<SprayLibraryEntry>();
        private readonly Dictionary<string, SprayLibraryEntry> _byHash = new Dictionary<string, SprayLibraryEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SprayLibraryEntry> _byFileName = new Dictionary<string, SprayLibraryEntry>(StringComparer.OrdinalIgnoreCase);

        public int Count => Entries.Count;
        public bool IsLoading { get; private set; }

        public SprayLibraryEntry Get(int index) => (index >= 0 && index < Entries.Count) ? Entries[index] : null;
        public bool TryGetByHash(string hash, out SprayLibraryEntry entry) { entry = null; return !string.IsNullOrEmpty(hash) && _byHash.TryGetValue(hash, out entry); }
        public bool TryGetByFileName(string name, out SprayLibraryEntry entry) { entry = null; return !string.IsNullOrEmpty(name) && _byFileName.TryGetValue(name, out entry); }
        public int IndexOf(SprayLibraryEntry entry) => Entries.IndexOf(entry);

        public void Clear()
        {
            foreach (var e in Entries) e.Dispose();
            Entries.Clear();
            _byHash.Clear();
            _byFileName.Clear();
        }

        /// <summary>Adds an entry, keeping Entries sorted ascending by Order.</summary>
        public void AddEntry(SprayLibraryEntry entry)
        {
            int i = 0;
            while (i < Entries.Count && Entries[i].Order <= entry.Order) i++;
            Entries.Insert(i, entry);
            if (!string.IsNullOrEmpty(entry.Hash)) _byHash[entry.Hash] = entry;
            if (!string.IsNullOrEmpty(entry.FileName)) _byFileName[entry.FileName] = entry;
        }

        /// <summary>Re-sorts entries by Order (after a live reorder/rename).</summary>
        public void Resort() => Entries.Sort((a, b) => a.Order.CompareTo(b.Order));

        /// <summary>
        /// Loads the given image files (in manifest order). Decode runs off-thread; upload
        /// is time-sliced. URL sprays are loaded separately by SprayManager and added via AddEntry.
        /// </summary>
        public IEnumerator LoadFilesAsync(List<SprayFileLoad> files, int maxSize, Action<bool> onComplete)
        {
            IsLoading = true;
            Clear();

            if (files == null || files.Count == 0)
            {
                IsLoading = false;
                onComplete?.Invoke(true);
                yield break;
            }

            var tasks = new Task<DecodeResult>[files.Count];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int started = 0, consumed = 0;

            while (consumed < files.Count)
            {
                while (started < files.Count && started < consumed + MaxConcurrentDecodes)
                {
                    string path = files[started].Path;
                    int ms = maxSize;
                    tasks[started] = Task.Run(() => DecodeOne(path, ms));
                    started++;
                }

                var task = tasks[consumed];
                if (!task.IsCompleted)
                {
                    yield return null;
                    continue;
                }

                DecodeResult result = null;
                try { result = task.Result; }
                catch (Exception e) { Debug.LogError($"[SprayLibrary] Decode task failed: {e.Message}"); }

                if (result != null)
                {
                    SprayLibraryEntry entry = null;
                    foreach (var step in BuildEntry(result, files[consumed], sw))
                    {
                        if (step is SprayLibraryEntry built) entry = built;
                        else yield return null;
                    }
                    if (entry != null && entry.Frames.Count > 0) AddEntry(entry);
                }

                consumed++;
            }

            IsLoading = false;
            Debug.Log($"[SprayLibrary] Loaded {Entries.Count} file sprays");
            onComplete?.Invoke(true);
        }

        private IEnumerable<object> BuildEntry(DecodeResult result, SprayFileLoad load, System.Diagnostics.Stopwatch sw)
        {
            var entry = new SprayLibraryEntry
            {
                FilePath = result.Path,
                FileName = Path.GetFileName(result.Path),
                Name = load.Name,
                Order = load.Order,
                Hash = result.Hash
            };

            if (result.Image != null && result.Image.Frames.Count > 0)
            {
                var img = result.Image;
                for (int f = 0; f < img.Frames.Count; f++)
                {
                    var tex = new Texture2D(img.Width, img.Height, TextureFormat.RGBA32, false);
                    tex.wrapMode = TextureWrapMode.Clamp;
                    tex.SetPixels32(img.Frames[f]);
                    tex.Apply(false, false);
                    entry.Frames.Add(tex);
                    entry.Delays.Add(f < img.Delays.Count ? img.Delays[f] : 0.1f);

                    if (sw.Elapsed.TotalMilliseconds >= UploadBudgetMs) { yield return null; sw.Restart(); }
                }
            }
            else if (result.Bytes != null && !result.Path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            {
                // Fallback: System.Drawing failed; decode on the main thread (no downscale).
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (tex.LoadImage(result.Bytes))
                {
                    tex.wrapMode = TextureWrapMode.Clamp;
                    entry.Frames.Add(tex);
                    entry.Delays.Add(0.1f);
                }
                else UnityEngine.Object.Destroy(tex);
                if (sw.Elapsed.TotalMilliseconds >= UploadBudgetMs) { yield return null; sw.Restart(); }
            }

            yield return entry;
        }

        // ---- background-thread work (no Unity API) ----

        private class DecodeResult
        {
            public string Path;
            public string Hash;
            public DecodedImage Image;   // null if System.Drawing failed
            public byte[] Bytes;         // kept for main-thread fallback
        }

        private static DecodeResult DecodeOne(string path, int maxSize)
        {
            var res = new DecodeResult { Path = path };
            try
            {
                res.Bytes = File.ReadAllBytes(path);
                res.Hash = ComputeHash(res.Bytes);
                res.Image = GifDecoder.DecodeBytes(res.Bytes, maxSize);
                if (res.Image != null) res.Bytes = null;
            }
            catch (Exception e)
            {
                Debug.LogError($"[SprayLibrary] Failed to read/decode {path}: {e.Message}");
            }
            return res;
        }

        /// <summary>16 hex chars from SHA-256 - the spray's network reference id.</summary>
        public static string ComputeHash(byte[] data)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] h = sha.ComputeHash(data);
                var sb = new System.Text.StringBuilder(16);
                for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Reads a legacy links.txt (one URL per line, optional "Name = url"). Kept only for
        /// one-time migration into sprays.json.
        /// </summary>
        public static List<KeyValuePair<string, string>> ParseLinksFile(string folder)
        {
            var result = new List<KeyValuePair<string, string>>();
            try
            {
                string path = Path.Combine(folder, "links.txt");
                if (!File.Exists(path)) return result;

                foreach (var raw in File.ReadAllLines(path))
                {
                    string line = raw?.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#") || line.StartsWith("//")) continue;

                    string name = null, url = line;
                    int eq = line.IndexOf('=');
                    if (eq > 0)
                    {
                        name = line.Substring(0, eq).Trim();
                        url = line.Substring(eq + 1).Trim();
                    }
                    if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(new KeyValuePair<string, string>(string.IsNullOrEmpty(name) ? url : name, url));
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SprayLibrary] Failed to read links.txt: {e.Message}");
            }
            return result;
        }
    }
}
