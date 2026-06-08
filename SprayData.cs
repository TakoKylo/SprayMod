using System;
using UnityEngine;

namespace SprayMod
{
    /// <summary>
    /// Plain placement data for a spray decal. No longer network-serializable -
    /// sprays are shared by reference over chat (see SprayChatSync), not by
    /// serializing this struct.
    /// </summary>
    public struct SprayData
    {
        public string OwnerSteamId;     // SteamID of the player who placed the spray ("" for local/standalone)
        public Vector3 Position;        // World position of the spray
        public Quaternion Rotation;     // Rotation to align with surface
        public Vector3 Normal;          // Surface normal for proper alignment
        public int TextureIndex;        // Index into the placer's spray library
        public float Scale;             // Size of the spray decal
        public float Opacity;           // Opacity of the spray decal (0.0 - 1.0)
        public ulong SprayId;           // Unique local identifier for this spray instance

        public SprayData(string ownerSteamId, Vector3 position, Quaternion rotation, Vector3 normal, int textureIndex, float scale, float opacity = 1f)
        {
            OwnerSteamId = ownerSteamId ?? string.Empty;
            Position = position;
            Rotation = rotation;
            Normal = normal;
            TextureIndex = textureIndex;
            Scale = scale;
            Opacity = opacity;
            SprayId = GenerateSprayId();
        }

        private static ulong GenerateSprayId()
        {
            return (ulong)(DateTime.UtcNow.Ticks ^ UnityEngine.Random.Range(0, int.MaxValue));
        }
    }
}
