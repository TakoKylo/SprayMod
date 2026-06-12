using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

namespace SprayMod
{
    /// <summary>
    /// Peer-to-peer spray sync over the game's chat channel. A placed spray is broadcast
    /// as a chat message; every client receives it in ChatManager.AddChatMessage, where the
    /// receive patch decodes it, hides it from the chat UI, and renders the spray.
    /// No server-side mod is required - vanilla servers relay chat for us.
    ///
    /// The message is INVISIBLE to players without the mod: the wire string is encoded with
    /// Unicode Tag characters (see EncodeInvisible), which survive the network and the server's
    /// trim, but a vanilla client's chat display strips them to an empty string and hides the
    /// whole line. Players WITH the mod read the raw Content before any filtering and decode it.
    ///
    /// Decoded wire format (v2), kept under the server's 128-char cap and never starting with '/':
    ///   "!SX2:" + Base64(placement[10]) + typeChar + payload
    ///     placement = posXYZ(int16 cm) + normalXYZ(sbyte) + scale(byte)   (10 bytes -> 16 b64)
    ///     typeChar  = 'u' (payload is a URL, downloaded by every client) or
    ///                 'h' (payload is a 16-hex content hash, matched against the local library)
    /// URL sprays are visible to everyone running the mod even if they don't own the file; hash
    /// sprays fall back to a placeholder when the receiver doesn't have that image.
    /// See spraymod-b897-architecture.
    /// </summary>
    public class SprayChatSync : MonoBehaviour
    {
        public const string MARKER = "!SX2:";
        private const int PLACEMENT_BYTES = 10;
        private const int B64_LEN = 16; // Base64 length of 10 bytes (incl. padding)

        public static SprayChatSync Instance { get; private set; }

        // Self-echo dedup: our own broadcast comes back to us; remember what we sent so we
        // never double-spawn it, regardless of whether our SteamID is resolvable.
        private readonly Dictionary<string, float> _recentSent = new Dictionary<string, float>();
        private const float RecentSentTtl = 6f;

        public void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public void SendSprayUrl(string url, Vector3 pos, Vector3 normal, float scale, Vector3 up)
        {
            if (string.IsNullOrEmpty(url)) return;
            Send(BuildContent('u', url, pos, normal, scale, up));
        }

        public void SendSprayHash(string hash, Vector3 pos, Vector3 normal, float scale, Vector3 up)
        {
            if (string.IsNullOrEmpty(hash)) return;
            Send(BuildContent('h', hash, pos, normal, scale, up));
        }

        private void Send(string content)
        {
            try
            {
                if (string.IsNullOrEmpty(content)) return;

                // Cloak the wire as invisible Unicode so players WITHOUT the mod never see it
                // (their chat display strips it to nothing and hides the line). Mod clients read
                // the raw Content before that filtering, so they still decode it.
                string wire = EncodeInvisible(content);
                if (wire.Length > 128)
                {
                    SprayUtilities.DebugLog($"Spray sync message too long ({wire.Length} chars); not sent");
                    return;
                }
                if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsConnectedClient) return;
                var cm = ChatManager.Instance;
                if (cm == null) return;

                PurgeRecent();
                _recentSent[wire] = Time.unscaledTime;

                cm.Client_SendChatMessage(wire, false, false);
            }
            catch (Exception e)
            {
                SprayUtilities.DebugLog($"Send failed: {e.Message}");
            }
        }

        /// <summary>Called by the receive patch for a marker message from any client.</summary>
        public static void HandleIncoming(string senderSteamId, string content)
        {
            try
            {
                var inst = Instance;
                if (inst != null && inst._recentSent.Remove(content))
                    return; // our own echo - already spawned locally

                if (!string.IsNullOrEmpty(senderSteamId) && senderSteamId == SprayManager.GetLocalSteamId())
                    return;

                // New clients send the wire invisibly-encoded; tolerate legacy raw-marker messages too.
                string decoded = IsInvisibleSpray(content) ? DecodeInvisible(content) : content;

                if (!TryDecode(decoded, out bool isUrl, out string reference, out Vector3 pos, out Vector3 normal, out float scale, out Vector3 up))
                    return;

                SprayManager.Instance?.RemoteSpray(senderSteamId ?? string.Empty, isUrl, reference, pos, normal, scale, up);
            }
            catch (Exception e)
            {
                SprayUtilities.DebugLog($"HandleIncoming failed: {e.Message}");
            }
        }

        private void PurgeRecent()
        {
            if (_recentSent.Count == 0) return;
            float now = Time.unscaledTime;
            var stale = new List<string>();
            foreach (var kvp in _recentSent)
                if (now - kvp.Value > RecentSentTtl) stale.Add(kvp.Key);
            foreach (var k in stale) _recentSent.Remove(k);
        }

        // ---- encoding ----

        private static string BuildContent(char type, string payload, Vector3 pos, Vector3 normal, float scale, Vector3 up)
        {
            string b64 = Convert.ToBase64String(EncodePlacement(pos, normal, scale, up));
            return MARKER + b64 + type + payload;
        }

        /// <summary>True if a URL spray message would fit within the server's 128-char chat cap.
        /// Each ASCII char of the wire becomes one 2-unit invisible character, so the budget halves.</summary>
        public static bool UrlFitsInChat(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            return (MARKER.Length + B64_LEN + 1 + url.Length) * 2 <= 128;
        }

        // ---- invisible transport ----
        // Each printable-ASCII byte of the wire maps to a Unicode TAG character (U+E0000 + c). Tag
        // characters are Format-category code points: they survive the network (UTF-16) and the
        // server's Trim, but a vanilla client's chat display runs FilterStringSpecialCharacters,
        // which strips Format characters - leaving an empty string, so UIChat hides the whole line
        // (DisplayStyle.None). Mod clients read the raw Content in the receive patch BEFORE any of
        // that filtering, so they decode it normally.
        private const int TagBase = 0xE0000;
        private const char TagHighSurrogate = '\uDB40'; // shared high surrogate of U+E0000..U+E03FF

        /// <summary>Encodes a printable-ASCII wire string as invisible Unicode Tag characters.</summary>
        public static string EncodeInvisible(string ascii)
        {
            if (string.IsNullOrEmpty(ascii)) return ascii;
            var sb = new StringBuilder(ascii.Length * 2);
            foreach (char c in ascii)
                if (c >= 0x20 && c <= 0x7E) sb.Append(char.ConvertFromUtf32(TagBase + c));
            return sb.ToString();
        }

        /// <summary>Inverse of EncodeInvisible: pulls the ASCII back out of the Tag characters.</summary>
        public static string DecodeInvisible(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length / 2);
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    int cp = char.ConvertToUtf32(s[i], s[++i]);
                    if (cp >= TagBase + 0x20 && cp <= TagBase + 0x7E) sb.Append((char)(cp - TagBase));
                }
            }
            return sb.ToString();
        }

        /// <summary>Fast check: does this message carry an invisibly-encoded spray marker?</summary>
        public static bool IsInvisibleSpray(string content)
        {
            if (string.IsNullOrEmpty(content) || content.IndexOf(TagHighSurrogate) < 0) return false;
            return DecodeInvisible(content).IndexOf(MARKER, StringComparison.Ordinal) >= 0;
        }

        public static bool TryDecode(string content, out bool isUrl, out string reference, out Vector3 pos, out Vector3 normal, out float scale, out Vector3 up)
        {
            isUrl = false; reference = null; pos = Vector3.zero; normal = Vector3.up; scale = 1f; up = Vector3.up;
            if (string.IsNullOrEmpty(content)) return false;

            // Locate the marker ANYWHERE in the message - some servers/mods colour-wrap or prefix
            // chat (e.g. "<color=#fff>!SX2:...</color>"), so it isn't always at index 0.
            int markerIdx = content.IndexOf(MARKER, StringComparison.Ordinal);
            if (markerIdx < 0) return false;

            int b64Start = markerIdx + MARKER.Length;
            int typeIndex = b64Start + B64_LEN;
            if (content.Length <= typeIndex) return false;

            byte[] placement;
            try { placement = Convert.FromBase64String(content.Substring(b64Start, B64_LEN)); }
            catch { return false; }
            if (placement.Length < PLACEMENT_BYTES) return false;

            char type = content[typeIndex];
            reference = content.Substring(typeIndex + 1);
            // Drop any trailing markup a server/mod may append (e.g. "</color>"); URLs and hex
            // hashes never contain '<'.
            int lt = reference.IndexOf('<');
            if (lt >= 0) reference = reference.Substring(0, lt);
            reference = reference.Trim();
            if (string.IsNullOrEmpty(reference)) return false;
            isUrl = type == 'u';

            DecodePlacement(placement, out pos, out normal, out scale, out up);
            return true;
        }

        // 11th byte = decal roll around the normal so the spray is oriented the same for everyone (see
        // EncodeRoll). It's optional: older 10-byte messages decode with no roll (up defaults to world up).
        private static byte[] EncodePlacement(Vector3 pos, Vector3 normal, float scale, Vector3 up)
        {
            var b = new byte[PLACEMENT_BYTES + 1];
            WriteShort(b, 0, (short)Mathf.Clamp(Mathf.RoundToInt(pos.x * 100f), short.MinValue, short.MaxValue));
            WriteShort(b, 2, (short)Mathf.Clamp(Mathf.RoundToInt(pos.y * 100f), short.MinValue, short.MaxValue));
            WriteShort(b, 4, (short)Mathf.Clamp(Mathf.RoundToInt(pos.z * 100f), short.MinValue, short.MaxValue));
            b[6] = (byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(normal.x * 127f), -127, 127);
            b[7] = (byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(normal.y * 127f), -127, 127);
            b[8] = (byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(normal.z * 127f), -127, 127);
            b[9] = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp(scale, 0f, 3f) / 3f * 255f), 0, 255);
            b[10] = EncodeRoll(normal, up);
            return b;
        }

        private static void DecodePlacement(byte[] b, out Vector3 pos, out Vector3 normal, out float scale, out Vector3 up)
        {
            pos = new Vector3(ReadShort(b, 0) / 100f, ReadShort(b, 2) / 100f, ReadShort(b, 4) / 100f);
            normal = new Vector3((sbyte)b[6] / 127f, (sbyte)b[7] / 127f, (sbyte)b[8] / 127f);
            if (normal.sqrMagnitude < 1e-6f) normal = Vector3.up;
            normal.Normalize();
            scale = b[9] / 255f * 3f;
            up = b.Length > PLACEMENT_BYTES ? DecodeRoll(normal, b[PLACEMENT_BYTES]) : Vector3.up;
        }

        // ---- decal roll (orientation around the normal), shared by sender and receiver ----

        /// <summary>Deterministic tangent on the surface plane, derived only from the normal.</summary>
        private static Vector3 RefTangent(Vector3 normal)
        {
            Vector3 t = Vector3.Cross(normal, Vector3.up);
            if (t.sqrMagnitude < 1e-4f) t = Vector3.Cross(normal, Vector3.forward);
            return t.normalized;
        }

        /// <summary>Encodes the spray's up (its angle around the normal from the reference tangent) into a byte.</summary>
        private static byte EncodeRoll(Vector3 normal, Vector3 up)
        {
            Vector3 n = normal.normalized;
            Vector3 r = RefTangent(n);
            Vector3 upPerp = up - n * Vector3.Dot(up, n);
            if (upPerp.sqrMagnitude < 1e-6f) upPerp = r;
            float ang = Vector3.SignedAngle(r, upPerp.normalized, n); // [-180, 180]
            return (byte)Mathf.Clamp(Mathf.RoundToInt((ang + 180f) / 360f * 255f), 0, 255);
        }

        /// <summary>Reconstructs the up vector from the roll byte (inverse of EncodeRoll).</summary>
        private static Vector3 DecodeRoll(Vector3 normal, byte roll)
        {
            float ang = roll / 255f * 360f - 180f;
            return (Quaternion.AngleAxis(ang, normal) * RefTangent(normal)).normalized;
        }

        private static void WriteShort(byte[] b, int o, short v) { b[o] = (byte)(v & 0xFF); b[o + 1] = (byte)((v >> 8) & 0xFF); }
        private static short ReadShort(byte[] b, int o) => (short)(b[o] | (b[o + 1] << 8));
    }

    /// <summary>
    /// Receive hook: hides our marker messages from the chat UI and routes them to the
    /// spray system. Patches the universal receive point so every client gets them.
    /// </summary>
    [HarmonyPatch(typeof(ChatManager), nameof(ChatManager.AddChatMessage))]
    public static class SprayChatReceivePatch
    {
        // Run before other mods that patch AddChatMessage (e.g. reskin/chat-color mods) so we
        // read the raw marker before anything can recolor/modify the content.
        [HarmonyPriority(Priority.First)]
        static bool Prefix(ChatMessage chatMessage)
        {
            try
            {
                if (chatMessage == null) return true;
                string content = chatMessage.Content.ToString();
                // Ours if it carries the invisibly-encoded marker (new clients) or the raw marker
                // ANYWHERE (legacy clients / servers that colour-wrap or prefix chat).
                if (string.IsNullOrEmpty(content) ||
                    (!SprayChatSync.IsInvisibleSpray(content) && content.IndexOf(SprayChatSync.MARKER, StringComparison.Ordinal) < 0))
                    return true; // normal message - let it through

                string steamId = chatMessage.SteamID.HasValue ? chatMessage.SteamID.Value.ToString() : string.Empty;
                SprayChatSync.HandleIncoming(steamId, content);
                return false; // swallow: never display or store
            }
            catch
            {
                return true;
            }
        }
    }
}
