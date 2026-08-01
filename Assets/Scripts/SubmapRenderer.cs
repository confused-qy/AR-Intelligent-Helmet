using System;
using System.Collections.Generic;
using UnityEngine;

namespace MotorcycleNavigation
{
    public static class SubmapRenderer
    {
        public static Texture2D Render(
            GridCostmap map,
            IList<NavPose> path,
            Vector3 center,
            int closestPathIndex,
            float windowMeters,
            int pixels,
            float headingYawDegrees = 0f)
        {
            Texture2D texture = new Texture2D(pixels, pixels, TextureFormat.RGB24, false);
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            Color32[] colors = new Color32[pixels * pixels];
            float metersPerPixel = windowMeters / pixels;
            float half = windowMeters * 0.5f;
            GetHeadingAxes(headingYawDegrees, out Vector2 right, out Vector2 forward);

            for (int py = 0; py < pixels; py++)
            {
                for (int px = 0; px < pixels; px++)
                {
                    float localX = -half + (px + 0.5f) * metersPerPixel;
                    float localForward = -half + (py + 0.5f) * metersPerPixel;
                    float worldX = center.x + right.x * localX + forward.x * localForward;
                    float worldZ = center.z + right.y * localX + forward.y * localForward;
                    int cx = Mathf.FloorToInt((worldX - map.OriginXZ.x) / map.Resolution);
                    int cy = Mathf.FloorToInt((worldZ - map.OriginXZ.y) / map.ResolutionZ);
                    byte cost = map.GetCost(cx, cy);
                    colors[px + py * pixels] = CostToColor(cost);
                }
            }

            if (path != null && path.Count > 1)
            {
                int start = Mathf.Clamp(closestPathIndex - 1, 0, path.Count - 1);
                int end = Mathf.Min(path.Count - 1, start + 24);
                for (int i = start; i < end; i++)
                {
                    int x0;
                    int y0;
                    int x1;
                    int y1;
                    if (TryClipWorldLineToPixels(
                        path[i].position,
                        path[i + 1].position,
                        center,
                        windowMeters,
                        pixels,
                        headingYawDegrees,
                        out x0,
                        out y0,
                        out x1,
                        out y1))
                    {
                        DrawAntialiasedLine(colors, pixels, pixels, x0, y0, x1, y1, new Color32(0, 110, 255, 255), 3f);
                    }
                }
            }

            int rx;
            int ry;
            if (WorldToPixel(center, center, windowMeters, pixels, out rx, out ry))
            {
                DrawCircle(colors, pixels, pixels, rx, ry, 5, new Color32(0, 210, 90, 255));
            }

            texture.SetPixels32(colors);
            texture.Apply(false);
            return texture;
        }

        public static Texture2D RenderOnSourceTexture(
            Texture2D source,
            GridCostmap map,
            IList<NavPose> path,
            Vector3 center,
            int closestPathIndex,
            float windowMeters,
            int pixels,
            bool flipVertical,
            float headingYawDegrees = 0f)
        {
            if (source == null)
                return Render(map, path, center, closestPathIndex, windowMeters, pixels, headingYawDegrees);

            Texture2D texture = new Texture2D(pixels, pixels, TextureFormat.RGB24, false);
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            Color32[] colors = new Color32[pixels * pixels];
            Color32[] sourcePixels = GetReadablePixels(source);
            float metersPerPixel = windowMeters / pixels;
            float half = windowMeters * 0.5f;
            GetHeadingAxes(headingYawDegrees, out Vector2 right, out Vector2 forward);

            for (int py = 0; py < pixels; py++)
            {
                for (int px = 0; px < pixels; px++)
                {
                    float localX = -half + (px + 0.5f) * metersPerPixel;
                    float localForward = -half + (py + 0.5f) * metersPerPixel;
                    float worldX = center.x + right.x * localX + forward.x * localForward;
                    float worldZ = center.z + right.y * localX + forward.y * localForward;
                    int mapX = Mathf.FloorToInt((worldX - map.OriginXZ.x) / map.Resolution);
                    int mapY = Mathf.FloorToInt((worldZ - map.OriginXZ.y) / map.ResolutionZ);
                    if (!map.InBounds(mapX, mapY))
                    {
                        colors[px + py * pixels] = new Color32(90, 90, 90, 255);
                        continue;
                    }

                    float sourceU = (worldX - map.OriginXZ.x) / (map.Width * map.Resolution);
                    float sourceV = (worldZ - map.OriginXZ.y) / (map.Height * map.ResolutionZ);
                    int sx = Mathf.Clamp(Mathf.FloorToInt(sourceU * source.width), 0, source.width - 1);
                    int syMap = Mathf.Clamp(Mathf.FloorToInt(sourceV * source.height), 0, source.height - 1);
                    int sy = flipVertical ? source.height - 1 - syMap : syMap;
                    colors[px + py * pixels] = sourcePixels[sx + sy * source.width];
                }
            }

            if (path != null && path.Count > 1)
            {
                int start = Mathf.Clamp(closestPathIndex - 1, 0, path.Count - 1);
                int end = path.Count - 1;
                for (int i = start; i < end; i++)
                {
                    int x0;
                    int y0;
                    int x1;
                    int y1;
                    if (TryClipWorldLineToPixels(
                        path[i].position,
                        path[i + 1].position,
                        center,
                        windowMeters,
                        pixels,
                        headingYawDegrees,
                        out x0,
                        out y0,
                        out x1,
                        out y1))
                    {
                        DrawAntialiasedLine(colors, pixels, pixels, x0, y0, x1, y1, new Color32(15, 35, 45, 255), 4.5f);
                        DrawAntialiasedLine(colors, pixels, pixels, x0, y0, x1, y1, new Color32(0, 180, 255, 255), 3f);
                    }
                }
            }

            int rx;
            int ry;
            if (WorldToPixel(center, center, windowMeters, pixels, out rx, out ry))
            {
                DrawCircle(colors, pixels, pixels, rx, ry, 6, new Color32(0, 220, 90, 255));
                DrawCircle(colors, pixels, pixels, rx, ry, 3, new Color32(255, 255, 255, 255));
            }

            texture.SetPixels32(colors);
            texture.Apply(false);
            return texture;
        }

        public static string EncodeJpegDataUri(Texture2D texture, int quality)
        {
            byte[] jpg = texture.EncodeToJPG(Mathf.Clamp(quality, 1, 100));
            return "data:image/jpeg;base64," + Convert.ToBase64String(jpg);
        }

        private static Color32 CostToColor(byte cost)
        {
            if (cost == GridCostmap.NoInformation)
                return new Color32(120, 120, 120, 255);
            if (cost >= GridCostmap.LethalObstacle)
                return new Color32(20, 20, 20, 255);
            if (cost >= GridCostmap.InscribedInflatedObstacle)
                return new Color32(140, 30, 30, 255);
            if (cost > GridCostmap.FreeSpace)
            {
                byte red = (byte)Mathf.Clamp(120 + cost / 2, 0, 255);
                byte green = (byte)Mathf.Clamp(220 - cost / 2, 80, 220);
                return new Color32(red, green, 90, 255);
            }
            return new Color32(235, 235, 235, 255);
        }

        private static bool WorldToPixel(Vector3 world, Vector3 center, float windowMeters, int pixels, out int x, out int y)
        {
            float half = windowMeters * 0.5f;
            x = Mathf.RoundToInt(((world.x - center.x + half) / windowMeters) * (pixels - 1));
            y = Mathf.RoundToInt(((world.z - center.z + half) / windowMeters) * (pixels - 1));
            return x >= 0 && y >= 0 && x < pixels && y < pixels;
        }

        private static bool TryClipWorldLineToPixels(
            Vector3 start,
            Vector3 end,
            Vector3 center,
            float windowMeters,
            int pixels,
            float headingYawDegrees,
            out int x0,
            out int y0,
            out int x1,
            out int y1)
        {
            float half = windowMeters * 0.5f;
            float maxPixel = pixels - 1f;
            GetHeadingAxes(headingYawDegrees, out Vector2 right, out Vector2 forward);
            Vector2 startOffset = new Vector2(start.x - center.x, start.z - center.z);
            Vector2 endOffset = new Vector2(end.x - center.x, end.z - center.z);
            float startX = ((Vector2.Dot(startOffset, right) + half) / windowMeters) * maxPixel;
            float startY = ((Vector2.Dot(startOffset, forward) + half) / windowMeters) * maxPixel;
            float endX = ((Vector2.Dot(endOffset, right) + half) / windowMeters) * maxPixel;
            float endY = ((Vector2.Dot(endOffset, forward) + half) / windowMeters) * maxPixel;
            float dx = endX - startX;
            float dy = endY - startY;
            float enter = 0f;
            float exit = 1f;

            if (!ClipLineBoundary(-dx, startX, ref enter, ref exit) ||
                !ClipLineBoundary(dx, maxPixel - startX, ref enter, ref exit) ||
                !ClipLineBoundary(-dy, startY, ref enter, ref exit) ||
                !ClipLineBoundary(dy, maxPixel - startY, ref enter, ref exit))
            {
                x0 = y0 = x1 = y1 = 0;
                return false;
            }

            x0 = Mathf.Clamp(Mathf.RoundToInt(startX + enter * dx), 0, pixels - 1);
            y0 = Mathf.Clamp(Mathf.RoundToInt(startY + enter * dy), 0, pixels - 1);
            x1 = Mathf.Clamp(Mathf.RoundToInt(startX + exit * dx), 0, pixels - 1);
            y1 = Mathf.Clamp(Mathf.RoundToInt(startY + exit * dy), 0, pixels - 1);
            return true;
        }

        private static void GetHeadingAxes(float yawDegrees, out Vector2 right, out Vector2 forward)
        {
            float radians = yawDegrees * Mathf.Deg2Rad;
            float sin = Mathf.Sin(radians);
            float cos = Mathf.Cos(radians);
            right = new Vector2(cos, -sin);
            forward = new Vector2(sin, cos);
        }

        private static bool ClipLineBoundary(float direction, float distance, ref float enter, ref float exit)
        {
            if (Mathf.Approximately(direction, 0f))
                return distance >= 0f;

            float ratio = distance / direction;
            if (direction < 0f)
            {
                if (ratio > exit)
                    return false;
                if (ratio > enter)
                    enter = ratio;
            }
            else
            {
                if (ratio < enter)
                    return false;
                if (ratio < exit)
                    exit = ratio;
            }

            return true;
        }

        private static bool WorldToTopDownPixel(Vector3 world, Vector3 center, float windowMeters, int pixels, out int x, out int y)
        {
            float half = windowMeters * 0.5f;
            x = Mathf.RoundToInt(((world.x - center.x + half) / windowMeters) * (pixels - 1));
            y = Mathf.RoundToInt(((center.z + half - world.z) / windowMeters) * (pixels - 1));
            return x >= 0 && y >= 0 && x < pixels && y < pixels;
        }

        private static void DrawAntialiasedLine(
            Color32[] colors,
            int width,
            int height,
            float x0,
            float y0,
            float x1,
            float y1,
            Color32 color,
            float radius)
        {
            float minX = Mathf.Min(x0, x1) - radius - 1f;
            float maxX = Mathf.Max(x0, x1) + radius + 1f;
            float minY = Mathf.Min(y0, y1) - radius - 1f;
            float maxY = Mathf.Max(y0, y1) + radius + 1f;
            int firstX = Mathf.Max(0, Mathf.FloorToInt(minX));
            int lastX = Mathf.Min(width - 1, Mathf.CeilToInt(maxX));
            int firstY = Mathf.Max(0, Mathf.FloorToInt(minY));
            int lastY = Mathf.Min(height - 1, Mathf.CeilToInt(maxY));
            Vector2 start = new Vector2(x0, y0);
            Vector2 delta = new Vector2(x1 - x0, y1 - y0);
            float lengthSquared = delta.sqrMagnitude;

            for (int y = firstY; y <= lastY; y++)
            {
                for (int x = firstX; x <= lastX; x++)
                {
                    Vector2 pixel = new Vector2(x + 0.5f, y + 0.5f);
                    float t = lengthSquared > 0.0001f
                        ? Mathf.Clamp01(Vector2.Dot(pixel - start, delta) / lengthSquared)
                        : 0f;
                    float distance = Vector2.Distance(pixel, start + t * delta);
                    float coverage = Mathf.Clamp01(radius + 0.5f - distance);
                    if (coverage <= 0f)
                        continue;

                    int index = x + y * width;
                    colors[index] = Blend(colors[index], color, coverage);
                }
            }
        }

        private static Color32 Blend(Color32 background, Color32 foreground, float amount)
        {
            float alpha = Mathf.Clamp01(amount * foreground.a / 255f);
            return new Color32(
                (byte)Mathf.RoundToInt(Mathf.Lerp(background.r, foreground.r, alpha)),
                (byte)Mathf.RoundToInt(Mathf.Lerp(background.g, foreground.g, alpha)),
                (byte)Mathf.RoundToInt(Mathf.Lerp(background.b, foreground.b, alpha)),
                255);
        }

        private static void DrawCircle(Color32[] colors, int width, int height, int cx, int cy, int radius, Color32 color)
        {
            int r2 = radius * radius;
            for (int y = cy - radius; y <= cy + radius; y++)
            {
                if (y < 0 || y >= height)
                    continue;
                for (int x = cx - radius; x <= cx + radius; x++)
                {
                    if (x < 0 || x >= width)
                        continue;
                    int dx = x - cx;
                    int dy = y - cy;
                    if (dx * dx + dy * dy <= r2)
                        colors[x + y * width] = color;
                }
            }
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
