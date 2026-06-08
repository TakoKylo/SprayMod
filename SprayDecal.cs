using System;
using System.Collections.Generic;
using UnityEngine;

namespace SprayMod
{
    /// <summary>
    /// Individual spray decal component that renders on surfaces
    /// Implements a projected decal using Unity's built-in projection
    /// </summary>
    public class SprayDecal : MonoBehaviour
    {
        public ulong SprayId { get; private set; }
        public string OwnerSteamId { get; private set; }
        public float LifeTime { get; private set; }

        // Decal shader is the same for every spray - find it once.
        private static Shader _cachedShader;
        public Texture2D DecalTexture { get; private set; }
        
        private GameObject decalObject;
        private Projector projector;
        private Material decalMaterial;
        private float creationTime;
        
        // Animation support
        private List<Texture2D> animationFrames;
        private List<float> frameDelays;
        private int currentFrame = 0;
        private float frameTimer = 0f;
        private bool isAnimated = false;

        private const float DECAL_SIZE = 2.0f; // Was 2.0 - reduced to normal size
        private const float DECAL_OFFSET = 0.05f; // Larger offset from surface

        /// <summary>
        /// Initialize the spray decal with the given parameters
        /// </summary>
        public void Initialize(SprayData data, Texture2D texture, float lifeTime = 0f, List<Texture2D> frames = null, List<float> delays = null)
        {
            SprayId = data.SprayId;
            OwnerSteamId = data.OwnerSteamId;
            LifeTime = lifeTime;
            DecalTexture = texture;
            creationTime = Time.time;
            
            // Set up animation if frames provided
            // GIFs work on both clients and servers - animation happens on all instances
            if (frames != null && frames.Count > 1)
            {
                animationFrames = frames;
                frameDelays = delays ?? new List<float>();
                isAnimated = true;
            }

            CreateDecalVisuals(data, texture);
        }

        /// <summary>
        /// Creates the visual representation of the spray using a Projector or Quad
        /// </summary>
        private void CreateDecalVisuals(SprayData data, Texture2D texture)
        {
            // Position slightly offset from the surface to avoid z-fighting
            transform.position = data.Position + data.Normal * DECAL_OFFSET;
            transform.rotation = Quaternion.LookRotation(-data.Normal);

            // Projectors don't work well in VR - skip to quad
            // Always use quad-based decal for visibility
            CreateQuadDecal(texture, data.Scale, data.Opacity);
        }

        /// <summary>
        /// Creates a projector-based decal (requires Projector component)
        /// </summary>
        private bool TryCreateProjectorDecal(Texture2D texture, float scale)
        {
            try
            {
                projector = gameObject.AddComponent<Projector>();
                projector.orthographic = true;
                projector.orthographicSize = scale * DECAL_SIZE;
                projector.nearClipPlane = 0.01f;
                projector.farClipPlane = 2f;
                projector.ignoreLayers = LayerMask.GetMask("Player", "Puck", "Stick");

                // Create material with the spray texture
                // Use Sprites/Default shader which should always exist
                Shader shader = Shader.Find("Sprites/Default");
                if (shader == null)
                {
                    shader = Shader.Find("Unlit/Texture");
                }
                if (shader == null)
                {
                    Debug.LogWarning("[SprayDecal] No suitable shader found, using fallback");
                    return false;
                }
                
                decalMaterial = new Material(shader);
                decalMaterial.mainTexture = texture;
                projector.material = decalMaterial;

                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SprayDecal] Could not create projector: {e.Message}");
                if (projector != null)
                {
                    Destroy(projector);
                }
                return false;
            }
        }

        /// <summary>
        /// Creates a quad-based decal (fallback method)
        /// </summary>
        private void CreateQuadDecal(Texture2D texture, float scale, float opacity = 1f)
        {
            decalObject = GameObject.CreatePrimitive(PrimitiveType.Quad);
            decalObject.name = $"SprayQuad_{SprayId}";
            decalObject.transform.SetParent(transform);
            decalObject.transform.localPosition = Vector3.zero;
            decalObject.transform.localRotation = Quaternion.identity;
            decalObject.transform.localScale = Vector3.one * scale * DECAL_SIZE;

            // Remove collider from the quad
            Collider collider = decalObject.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }

            // Get the renderer
            MeshRenderer renderer = decalObject.GetComponent<MeshRenderer>();

            // Resolve the decal shader once and reuse it for every spray.
            Shader transparentShader = GetDecalShader();

            // Create material
            if (transparentShader != null)
            {
                decalMaterial = new Material(transparentShader);
            }
            else
            {
                decalMaterial = new Material(renderer.sharedMaterial);
            }
            
            // Set texture first
            decalMaterial.mainTexture = texture;
            if (decalMaterial.HasProperty("_MainTex"))
            {
                decalMaterial.SetTexture("_MainTex", texture);
            }
            
            // Enable transparency for Standard shader
            if (decalMaterial.shader.name.Contains("Standard"))
            {
                decalMaterial.SetFloat("_Mode", 3); // Transparent
                decalMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                decalMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                decalMaterial.SetInt("_ZWrite", 0);
                decalMaterial.DisableKeyword("_ALPHATEST_ON");
                decalMaterial.EnableKeyword("_ALPHABLEND_ON");
                decalMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                decalMaterial.renderQueue = 3000;
            }
            else
            {
                // For other shaders, just set high render queue
                decalMaterial.renderQueue = 3000;
            }
            
            // Set color with opacity applied
            decalMaterial.color = new Color(1f, 1f, 1f, opacity);
            
            renderer.material = decalMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            
            // Make absolutely sure the GameObject is active
            decalObject.SetActive(true);
            gameObject.SetActive(true);
        }

        /// <summary>
        /// Finds a suitable transparent shader once and caches it. The scene-wide
        /// fallback scan only ever runs on the very first spray if nothing else is found.
        /// </summary>
        private static Shader GetDecalShader()
        {
            if (_cachedShader != null) return _cachedShader;

            _cachedShader = Shader.Find("Unlit/Transparent")
                            ?? Shader.Find("Sprites/Default")
                            ?? Shader.Find("Standard");

            if (_cachedShader == null)
            {
                MeshRenderer[] allRenderers = UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
                foreach (MeshRenderer r in allRenderers)
                {
                    if (r != null && r.sharedMaterial != null && r.sharedMaterial.shader != null &&
                        !r.sharedMaterial.shader.name.Contains("Hidden"))
                    {
                        _cachedShader = r.sharedMaterial.shader;
                        break;
                    }
                }
            }

            return _cachedShader;
        }

        /// <summary>
        /// Live-updates this decal's size and opacity (used when the local display settings
        /// change so already-placed sprays update too).
        /// </summary>
        public void SetDisplay(float scale, float opacity)
        {
            if (decalObject != null)
                decalObject.transform.localScale = Vector3.one * scale * DECAL_SIZE;
            if (decalMaterial != null)
            {
                Color c = decalMaterial.color;
                c.a = opacity;
                decalMaterial.color = c;
            }
        }

        private void Update()
        {
            // Update animation
            if (isAnimated && animationFrames != null && animationFrames.Count > 1 && decalMaterial != null)
            {
                frameTimer += Time.deltaTime;
                
                float currentDelay = currentFrame < frameDelays.Count ? frameDelays[currentFrame] : 0.1f;
                if (frameTimer >= currentDelay)
                {
                    frameTimer = 0f;
                    currentFrame = (currentFrame + 1) % animationFrames.Count;
                    
                    // Update material texture
                    decalMaterial.mainTexture = animationFrames[currentFrame];
                    if (decalMaterial.HasProperty("_MainTex"))
                    {
                        decalMaterial.SetTexture("_MainTex", animationFrames[currentFrame]);
                    }
                }
            }
            
            // Handle lifetime expiration
            if (LifeTime > 0 && Time.time - creationTime >= LifeTime)
            {
                FadeAndDestroy();
            }
        }

        /// <summary>
        /// Fade out and destroy the spray
        /// </summary>
        public void FadeAndDestroy(float fadeTime = 1f)
        {
            if (decalMaterial != null && decalMaterial.HasProperty("_Color"))
            {
                // Simple fade out
                Color color = decalMaterial.color;
                color.a = Mathf.Lerp(color.a, 0f, Time.deltaTime / fadeTime);
                decalMaterial.color = color;

                if (color.a <= 0.01f)
                {
                    Destroy(gameObject);
                }
            }
            else
            {
                Destroy(gameObject);
            }
        }

        private void OnDestroy()
        {
            // Clean up material to prevent memory leaks
            if (decalMaterial != null)
            {
                Destroy(decalMaterial);
            }

            if (decalObject != null)
            {
                Destroy(decalObject);
            }
        }
    }
}
