using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace SprayMod
{
    /// <summary>
    /// EXPERIMENT: screen-space (depth-buffer) decals. The visible rink meshes aren't readable and
    /// have no usable colliders, and the game's URP has no Decal feature/shader - so the only way to
    /// conform a spray to existing surfaces is to ship our own shader and project via the depth buffer.
    ///
    /// This loads a custom shader from an AssetBundle ("spraymod_decals", built in Unity 6000.3.14f1 +
    /// URP - see decal/README.md) shipped next to the DLL, ensures URP writes the camera depth texture,
    /// and places a unit cube using that shader at the hit. If the bundle is missing/unusable, callers
    /// fall back to the flat quad - so the mod works with or without it.
    /// </summary>
    public static class SprayScreenDecal
    {
        private const string BundleName = "spraymod_decals";
        private const string MaterialName = "SprayDecalMat";
        private const string ShaderName = "SprayMod/ScreenSpaceDecal";

        private static bool _tried;
        private static bool _ready;
        private static Material _template;
        private static Mesh _cubeMesh;

        public static bool Available => Ensure();

        private static bool Ensure()
        {
            if (_tried) return _ready;
            _tried = true;
            try
            {
                string dir = SprayUtilities.GetModDirectory();
                string path = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, BundleName);
                if (path == null || !File.Exists(path))
                {
                    Debug.Log($"[SprayMod] Screen-space decal bundle '{BundleName}' not found next to the DLL; using quad decals. " +
                              $"(Build it in Unity {Application.unityVersion} + URP - see decal/README.md.)");
                    return false;
                }

                var bundle = AssetBundle.LoadFromFile(path);
                if (bundle == null) { Debug.LogWarning("[SprayMod] Could not load decal AssetBundle."); return false; }

                _template = bundle.LoadAsset<Material>(MaterialName);
                if (_template == null)
                {
                    var sh = bundle.LoadAsset<Shader>(ShaderName);
                    if (sh != null) _template = new Material(sh);
                }
                if (_template == null || _template.shader == null || !_template.shader.isSupported)
                {
                    Debug.LogWarning("[SprayMod] Decal bundle loaded but has no usable material/shader.");
                    return false;
                }

                EnableUrpDepthTexture();
                _cubeMesh = BuildUnitCube();
                _ready = true;
                Debug.Log("[SprayMod] Screen-space decals enabled (custom shader loaded).");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SprayMod] Decal bundle load failed: {e.Message}");
                _ready = false;
            }
            return _ready;
        }

        // URP only generates _CameraDepthTexture when the active pipeline asset asks for it.
        private static void EnableUrpDepthTexture()
        {
            try
            {
                var rp = GraphicsSettings.currentRenderPipeline;
                if (rp == null) return;
                var t = rp.GetType();

                var prop = t.GetProperty("supportsCameraDepthTexture", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanWrite) { prop.SetValue(rp, true); return; }

                var f = t.GetField("m_RequireDepthTexture", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?? t.GetField("m_SupportsCameraDepthTexture", BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) { f.SetValue(rp, true); return; }

                Debug.LogWarning("[SprayMod] Couldn't toggle URP depth texture; screen-space decals may not appear if depth is off.");
            }
            catch (Exception e) { Debug.LogWarning($"[SprayMod] Enable depth texture failed: {e.Message}"); }
        }

        /// <summary>
        /// Turns <paramref name="go"/> into a screen-space decal box at the hit. Returns false (and
        /// builds nothing) if the custom shader bundle isn't available. On success <paramref name="material"/>
        /// is the per-instance material (so the caller can animate GIF frames / fade opacity).
        /// </summary>
        public static bool TryCreate(GameObject go, Texture2D tex, Vector3 position, Vector3 normal,
            Vector3 up, float size, float opacity, out Material material)
        {
            material = null;
            if (!Ensure() || tex == null) return false;
            try
            {
                // Local +Z = surface normal (the projection axis); local +Y = the sprayer's up so the
                // spray reads upright from their view (XY spans the decal plane).
                // Keep the projection box THIN and biased BEHIND the surface so objects in FRONT of it
                // (players/sticks/pucks skating by) fall outside the volume and don't get stamped.
                // Box spans from +frontMargin (in front of the hit) to -(depth-frontMargin) (into the wall).
                Vector3 n = normal.sqrMagnitude > 1e-6f ? normal.normalized : Vector3.up;
                Vector3 u = up.sqrMagnitude > 1e-6f ? up : Vector3.up;
                const float depth = 0.5f;        // thin volume (was up to size*0.5)
                const float frontMargin = 0.08f; // small tolerance in front for collider/visual offset
                go.transform.position = position + n * (frontMargin - depth * 0.5f);
                go.transform.rotation = Quaternion.LookRotation(n, u);
                go.transform.localScale = new Vector3(size, size, depth);

                go.AddComponent<MeshFilter>().sharedMesh = _cubeMesh;
                var r = go.AddComponent<MeshRenderer>();
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

                material = new Material(_template);
                material.mainTexture = tex;
                if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", tex);
                if (material.HasProperty("_Color")) material.color = new Color(1f, 1f, 1f, opacity);
                // Un-mirror: projecting along the normal flips the image horizontally when viewed from
                // the front, so flip U back (tiling -1, offset 1).
                material.mainTextureScale = new Vector2(-1f, 1f);
                material.mainTextureOffset = new Vector2(1f, 0f);
                r.sharedMaterial = material;
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SprayMod] Screen-space decal create failed: {e.Message}");
                if (material != null) { UnityEngine.Object.Destroy(material); material = null; }
                return false;
            }
        }

        // A shared 1x1x1 cube (verts at +/-0.5, matching the shader's unit-cube clip).
        private static Mesh BuildUnitCube()
        {
            var temp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var src = temp.GetComponent<MeshFilter>().sharedMesh;
            var copy = UnityEngine.Object.Instantiate(src);
            copy.name = "SprayDecalCube";
            UnityEngine.Object.Destroy(temp);
            return copy;
        }
    }
}
