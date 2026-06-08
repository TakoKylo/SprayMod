using System;
using System.Collections.Generic;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

namespace SprayMod
{
    /// <summary>
    /// Peer-to-peer spray sync over the game's chat channel. A placed spray is broadcast
    /// as a tiny hidden chat message; every client receives it in ChatManager.AddChatMessage,
    /// where the receive patch decodes it, hides it from the chat UI, and renders the spray.
    /// No server-side mod is required - vanilla servers relay chat for us.
    ///
    /// Wire format (v2), kept under the server's 128-char cap and never starting with '/':
    ///   "!SX2:" + Base64(placement[10]) + typeChar + payload
    ///     placement = posXYZ(int16 cm) + normalXYZ(sbyte) + scale(byte)   (10 bytes -> 16 b64)
    ///     typeChar  = 'u' (payload is a URL, downloaded by every client) or
    ///                 'h' (payload is a 16-hex content hash, matched against the local library)
    /// URL sprays are visible to everyone even if they don't own the file; hash sprays fall
    /// back to a placeholder when the receiver doesn't have that image.
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

        public void SendSprayUrl(string url, Vector3 pos, Vector3 normal, float scale)
        {
            if (string.IsNullOrEmpty(url)) return;
            Send(BuildContent('u', url, pos, normal, scale));
        }

        public void SendSprayHash(string hash, Vector3 pos, Vector3 normal, float scale)
        {
            if (string.IsNullOrEmpty(hash)) return;
            Send(BuildContent('h', hash, pos, normal, scale));
        }

        private void Send(string content)
        {
            try
            {
                if (string.IsNullOrEmpty(content)) return;
                if (content.Length > 128)
                {
                    SprayUtilities.DebugLog($"Spray sync message too long ({content.Length} chars); not sent");
                    return;
                }
                if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsConnectedClient) return;
                var cm = ChatManager.Instance;
                if (cm == null) return;

                PurgeRecent();
                _recentSent[content] = Time.unscaledTime;

                cm.Client_SendChatMessage(content, false, false);
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

                if (!TryDecode(content, out bool isUrl, out string reference, out Vector3 pos, out Vector3 normal, out float scale))
                    return;

                SprayManager.Instance?.RemoteSpray(senderSteamId ?? string.Empty, isUrl, reference, pos, normal, scale);
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

        private static string BuildContent(char type, string payload, Vector3 pos, Vector3 normal, float scale)
        {
            string b64 = Convert.ToBase64String(EncodePlacement(pos, normal, scale));
            return MARKER + b64 + type + payload;
        }

        /// <summary>True if a URL spray message would fit within the server's 128-char chat cap.</summary>
        public static bool UrlFitsInChat(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            return MARKER.Length + B64_LEN + 1 + url.Length <= 128;
        }

        public static bool TryDecode(string content, out bool isUrl, out string reference, out Vector3 pos, out Vector3 normal, out float scale)
        {
            isUrl = false; reference = null; pos = Vector3.zero; normal = Vector3.up; scale = 1f;
            if (string.IsNullOrEmpty(content) || !content.StartsWith(MARKER, StringComparison.Ordinal)) return false;

            int b64Start = MARKER.Length;
            int typeIndex = b64Start + B64_LEN;
            if (content.Length <= typeIndex) return false;

            byte[] placement;
            try { placement = Convert.FromBase64String(content.Substring(b64Start, B64_LEN)); }
            catch { return false; }
            if (placement.Length < PLACEMENT_BYTES) return false;

            char type = content[typeIndex];
            reference = content.Substring(typeIndex + 1);
            if (string.IsNullOrEmpty(reference)) return false;
            isUrl = type == 'u';

            DecodePlacement(placement, out pos, out normal, out scale);
            return true;
        }

        private static byte[] EncodePlacement(Vector3 pos, Vector3 normal, float scale)
        {
            var b = new byte[PLACEMENT_BYTES];
            WriteShort(b, 0, (short)Mathf.Clamp(Mathf.RoundToInt(pos.x * 100f), short.MinValue, short.MaxValue));
            WriteShort(b, 2, (short)Mathf.Clamp(Mathf.RoundToInt(pos.y * 100f), short.MinValue, short.MaxValue));
            WriteShort(b, 4, (short)Mathf.Clamp(Mathf.RoundToInt(pos.z * 100f), short.MinValue, short.MaxValue));
            b[6] = (byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(normal.x * 127f), -127, 127);
            b[7] = (byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(normal.y * 127f), -127, 127);
            b[8] = (byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(normal.z * 127f), -127, 127);
            b[9] = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp(scale, 0f, 3f) / 3f * 255f), 0, 255);
            return b;
        }

        private static void DecodePlacement(byte[] b, out Vector3 pos, out Vector3 normal, out float scale)
        {
            pos = new Vector3(ReadShort(b, 0) / 100f, ReadShort(b, 2) / 100f, ReadShort(b, 4) / 100f);
            normal = new Vector3((sbyte)b[6] / 127f, (sbyte)b[7] / 127f, (sbyte)b[8] / 127f);
            if (normal.sqrMagnitude < 1e-6f) normal = Vector3.up;
            normal.Normalize();
            scale = b[9] / 255f * 3f;
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
                if (content == null || !content.StartsWith(SprayChatSync.MARKER, StringComparison.Ordinal))
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
