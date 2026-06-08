using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using UnityEngine;

namespace SprayMod
{
    /// <summary>
    /// Raw decoded image data produced entirely OFF the Unity main thread.
    /// Holds RGBA pixels in Unity orientation (row 0 = bottom) so the main
    /// thread only has to do the cheap GPU upload (Texture2D.SetPixels32/Apply).
    /// This is the core of lag-free spray loading.
    /// </summary>
    public class DecodedImage
    {
        public int Width;
        public int Height;
        public List<Color32[]> Frames = new List<Color32[]>();
        public List<float> Delays = new List<float>();
        public bool IsAnimated => Frames.Count > 1;
    }

    /// <summary>
    /// Decodes PNG / JPG / animated GIF files to raw pixel frames using
    /// System.Drawing (GDI+). All methods here are thread-safe to call from a
    /// background thread because they touch no Unity API.
    /// </summary>
    public static class GifDecoder
    {
        /// <summary>
        /// Decode an image file to raw RGBA frames. Safe to call off-thread.
        /// Returns null on failure (caller should fall back to main-thread decode).
        /// </summary>
        public static DecodedImage Decode(string filePath, int maxSize)
        {
            try
            {
                byte[] fileBytes = File.ReadAllBytes(filePath);
                return DecodeBytes(fileBytes, maxSize);
            }
            catch (Exception e)
            {
                Debug.LogError($"[GifDecoder] Decode failed for {filePath}: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Decode raw image bytes to RGBA frames. Safe to call off-thread.
        /// </summary>
        public static DecodedImage DecodeBytes(byte[] fileBytes, int maxSize)
        {
            // Keep the stream alive for the lifetime of the Image (GDI+ requirement).
            using (var ms = new MemoryStream(fileBytes))
            using (var image = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false))
            {
                var result = new DecodedImage();

                int frameCount = 1;
                FrameDimension dimension = null;
                try
                {
                    if (image.FrameDimensionsList != null && image.FrameDimensionsList.Length > 0)
                    {
                        dimension = new FrameDimension(image.FrameDimensionsList[0]);
                        frameCount = image.GetFrameCount(dimension);
                    }
                }
                catch { frameCount = 1; }

                // Per-frame delays (GIF property 0x5100, hundredths of a second).
                PropertyItem delayItem = null;
                if (frameCount > 1)
                {
                    try { delayItem = image.GetPropertyItem(0x5100); } catch { }
                }

                ComputeTargetSize(image.Width, image.Height, maxSize, out int targetW, out int targetH);
                result.Width = targetW;
                result.Height = targetH;

                for (int i = 0; i < frameCount; i++)
                {
                    if (dimension != null && frameCount > 1)
                    {
                        image.SelectActiveFrame(dimension, i);
                    }

                    float delay = 0.1f;
                    if (delayItem != null && delayItem.Value != null && delayItem.Value.Length >= (i + 1) * 4)
                    {
                        int hundredths = BitConverter.ToInt32(delayItem.Value, i * 4);
                        delay = hundredths / 100f;
                        if (delay < 0.02f) delay = 0.1f; // clamp absurdly fast frames
                    }

                    Color32[] pixels = RenderFrameToRgba(image, targetW, targetH);
                    if (pixels != null)
                    {
                        result.Frames.Add(pixels);
                        result.Delays.Add(delay);
                    }
                }

                return result.Frames.Count > 0 ? result : null;
            }
        }

        /// <summary>
        /// Draws the image's current frame into a fresh 32bpp bitmap at the target
        /// size (compositing + downscaling), then returns Unity-oriented RGBA pixels.
        /// </summary>
        private static Color32[] RenderFrameToRgba(Image image, int targetW, int targetH)
        {
            using (var bmp = new Bitmap(targetW, targetH, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.Clear(System.Drawing.Color.Transparent);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.DrawImage(image, 0, 0, targetW, targetH);
                }

                var rect = new Rectangle(0, 0, targetW, targetH);
                BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    int byteCount = data.Stride * targetH;
                    byte[] raw = new byte[byteCount];
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0, raw, 0, byteCount);

                    var pixels = new Color32[targetW * targetH];
                    // GDI+ rows are top-down; Unity textures are bottom-up. Flip vertically.
                    for (int y = 0; y < targetH; y++)
                    {
                        int srcRow = y * data.Stride;
                        int dstRow = (targetH - 1 - y) * targetW;
                        for (int x = 0; x < targetW; x++)
                        {
                            int s = srcRow + x * 4;          // memory order is B,G,R,A
                            pixels[dstRow + x] = new Color32(raw[s + 2], raw[s + 1], raw[s], raw[s + 3]);
                        }
                    }
                    return pixels;
                }
                finally
                {
                    bmp.UnlockBits(data);
                }
            }
        }

        private static void ComputeTargetSize(int width, int height, int maxSize, out int newWidth, out int newHeight)
        {
            if (maxSize <= 0 || (width <= maxSize && height <= maxSize))
            {
                newWidth = Mathf.Max(1, width);
                newHeight = Mathf.Max(1, height);
                return;
            }

            float aspect = (float)width / height;
            if (width >= height)
            {
                newWidth = maxSize;
                newHeight = Mathf.Max(1, Mathf.RoundToInt(maxSize / aspect));
            }
            else
            {
                newHeight = maxSize;
                newWidth = Mathf.Max(1, Mathf.RoundToInt(maxSize * aspect));
            }
        }
    }
}
