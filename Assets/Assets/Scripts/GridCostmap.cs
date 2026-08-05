using System;
using UnityEngine;

namespace MotorcycleNavigation
{
    public class GridCostmap
    {
        public const byte FreeSpace = 0;
        public const byte InscribedInflatedObstacle = 253;
        public const byte LethalObstacle = 254;
        public const byte NoInformation = 255;

        public readonly int Width;
        public readonly int Height;
        public readonly float Resolution;
        public readonly float ResolutionZ;
        public readonly Vector2 OriginXZ;
        public readonly byte[] Costs;

        public GridCostmap(int width, int height, float resolution, Vector2 originXZ)
            : this(width, height, resolution, resolution, originXZ)
        {
        }

        public GridCostmap(int width, int height, float resolutionX, float resolutionZ, Vector2 originXZ)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentException("Costmap dimensions must be positive.");
            if (resolutionX <= 0f || resolutionZ <= 0f)
                throw new ArgumentException("Costmap resolution must be positive.");

            Width = width;
            Height = height;
            Resolution = resolutionX;
            ResolutionZ = resolutionZ;
            OriginXZ = originXZ;
            Costs = new byte[width * height];
        }

        public int Index(int x, int y)
        {
            return x + y * Width;
        }

        public bool InBounds(int x, int y)
        {
            return x >= 0 && y >= 0 && x < Width && y < Height;
        }

        public byte GetCost(int x, int y)
        {
            if (!InBounds(x, y))
                return NoInformation;
            return Costs[Index(x, y)];
        }

        public void SetCost(int x, int y, byte cost)
        {
            if (InBounds(x, y))
                Costs[Index(x, y)] = cost;
        }

        public bool WorldToCell(Vector3 world, out int x, out int y)
        {
            x = Mathf.FloorToInt((world.x - OriginXZ.x) / Resolution);
            y = Mathf.FloorToInt((world.z - OriginXZ.y) / ResolutionZ);
            return InBounds(x, y);
        }

        public Vector3 CellToWorld(int x, int y, float worldY = 0f)
        {
            return new Vector3(
                OriginXZ.x + (x + 0.5f) * Resolution,
                worldY,
                OriginXZ.y + (y + 0.5f) * ResolutionZ);
        }

        public GridCostmap Clone()
        {
            GridCostmap clone = new GridCostmap(Width, Height, Resolution, ResolutionZ, OriginXZ);
            Array.Copy(Costs, clone.Costs, Costs.Length);
            return clone;
        }

        public static GridCostmap FromTexture(Texture2D texture, CostmapBuildSettings settings)
        {
            if (texture == null)
                throw new ArgumentNullException("texture");
            if (settings == null)
                throw new ArgumentNullException("settings");

            GridCostmap map = new GridCostmap(
                texture.width,
                texture.height,
                settings.metersPerPixel,
                settings.metersPerPixelZ > 0f ? settings.metersPerPixelZ : settings.metersPerPixel,
                settings.worldOriginXZ);

            Color32[] pixels = GetReadablePixels(texture);
            for (int y = 0; y < map.Height; y++)
            {
                int sourceY = settings.flipVertical ? map.Height - 1 - y : y;
                for (int x = 0; x < map.Width; x++)
                {
                    Color32 c = pixels[x + sourceY * map.Width];
                    float alpha = c.a / 255f;
                    if (alpha <= settings.unknownAlphaThreshold)
                    {
                        map.SetCost(x, y, NoInformation);
                        continue;
                    }

                    float luma = (0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b) / 255f;
                    bool obstacle = settings.darkPixelsAreObstacles
                        ? luma <= settings.obstacleLumaThreshold
                        : luma >= settings.obstacleLumaThreshold;

                    if (obstacle && settings.ignoreSemanticAnnotationColors && IsSemanticAnnotationColor(c, settings))
                        obstacle = false;

                    map.SetCost(x, y, obstacle ? LethalObstacle : FreeSpace);
                }
            }

            return map;
        }

        private static bool IsSemanticAnnotationColor(Color32 c, CostmapBuildSettings settings)
        {
            float r = c.r / 255f;
            float g = c.g / 255f;
            float b = c.b / 255f;
            float max = Mathf.Max(r, Mathf.Max(g, b));
            float min = Mathf.Min(r, Mathf.Min(g, b));
            float saturation = max <= 0.0001f ? 0f : (max - min) / max;
            return max >= settings.semanticValueThreshold && saturation >= settings.semanticSaturationThreshold;
        }

        private static Color32[] GetReadablePixels(Texture2D texture)
        {
            try
            {
                return texture.GetPixels32();
            }
            catch (UnityException)
            {
                RenderTexture previous = RenderTexture.active;
                RenderTexture temporary = RenderTexture.GetTemporary(
                    texture.width,
                    texture.height,
                    0,
                    RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Linear);

                Graphics.Blit(texture, temporary);
                RenderTexture.active = temporary;

                Texture2D readable = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                readable.Apply(false);

                Color32[] pixels = readable.GetPixels32();
                UnityEngine.Object.Destroy(readable);
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(temporary);
                return pixels;
            }
        }
    }
}
