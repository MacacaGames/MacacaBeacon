using System.Collections.Generic;
using UnityEngine;

namespace MacacaGames.RuntimeBugReporter
{
    internal sealed class ScreenshotAnnotator
    {
        private readonly Texture2D texture;
        private readonly Color32[] originalPixels;
        private readonly Color32[] workingPixels;
        private readonly List<Stroke> strokes = new List<Stroke>();
        private List<Stroke> clearedStrokes;
        private Stroke activeStroke;

        public bool HasAnnotations => strokes.Count > 0;
        public bool CanUndo => strokes.Count > 0 || clearedStrokes != null;
        public int Width => texture.width;
        public int Height => texture.height;

        public ScreenshotAnnotator(Texture2D texture)
        {
            this.texture = texture;
            originalPixels = texture.GetPixels32();
            workingPixels = new Color32[originalPixels.Length];
            originalPixels.CopyTo(workingPixels, 0);
        }

        public void BeginStroke(Vector2 normalizedPoint, Color32 color, int radius)
        {
            clearedStrokes = null;
            activeStroke = new Stroke(color, Mathf.Max(1, radius));
            strokes.Add(activeStroke);
            AddPoint(normalizedPoint);
        }

        public void AddPoint(Vector2 normalizedPoint)
        {
            if (activeStroke == null)
                return;
            var point = ToPixel(normalizedPoint);
            if (activeStroke.Points.Count > 0 && activeStroke.Points[activeStroke.Points.Count - 1] == point)
                return;
            if (activeStroke.Points.Count == 0)
                DrawCircle(point, activeStroke.Radius, activeStroke.Color);
            else
                DrawSegment(activeStroke.Points[activeStroke.Points.Count - 1], point, activeStroke.Radius, activeStroke.Color);
            activeStroke.Points.Add(point);
            Apply();
        }

        public void EndStroke() => activeStroke = null;

        public void Undo()
        {
            activeStroke = null;
            if (strokes.Count > 0)
                strokes.RemoveAt(strokes.Count - 1);
            else if (clearedStrokes != null)
            {
                strokes.AddRange(clearedStrokes);
                clearedStrokes = null;
            }
            Rebuild();
        }

        public void Clear()
        {
            activeStroke = null;
            if (strokes.Count == 0)
                return;
            clearedStrokes = new List<Stroke>(strokes);
            strokes.Clear();
            Rebuild();
        }

        public byte[] EncodePng() => texture.EncodeToPNG();

        private Vector2Int ToPixel(Vector2 normalizedPoint)
        {
            return new Vector2Int(
                Mathf.Clamp(Mathf.RoundToInt(normalizedPoint.x * (texture.width - 1)), 0, texture.width - 1),
                Mathf.Clamp(Mathf.RoundToInt((1f - normalizedPoint.y) * (texture.height - 1)), 0, texture.height - 1));
        }

        private void Rebuild()
        {
            originalPixels.CopyTo(workingPixels, 0);
            foreach (var stroke in strokes)
            {
                if (stroke.Points.Count == 1)
                    DrawCircle(stroke.Points[0], stroke.Radius, stroke.Color);
                for (var index = 1; index < stroke.Points.Count; index++)
                    DrawSegment(stroke.Points[index - 1], stroke.Points[index], stroke.Radius, stroke.Color);
            }
            Apply();
        }

        private void DrawSegment(Vector2Int from, Vector2Int to, int radius, Color32 color)
        {
            var distance = Vector2.Distance(from, to);
            var steps = Mathf.Max(1, Mathf.CeilToInt(distance / Mathf.Max(1f, radius * 0.45f)));
            for (var step = 0; step <= steps; step++)
            {
                var amount = step / (float)steps;
                DrawCircle(new Vector2Int(Mathf.RoundToInt(Mathf.Lerp(from.x, to.x, amount)), Mathf.RoundToInt(Mathf.Lerp(from.y, to.y, amount))), radius, color);
            }
        }

        private void DrawCircle(Vector2Int center, int radius, Color32 color)
        {
            var radiusSquared = radius * radius;
            var minimumX = Mathf.Max(0, center.x - radius);
            var maximumX = Mathf.Min(texture.width - 1, center.x + radius);
            var minimumY = Mathf.Max(0, center.y - radius);
            var maximumY = Mathf.Min(texture.height - 1, center.y + radius);
            for (var y = minimumY; y <= maximumY; y++)
            {
                var deltaY = y - center.y;
                for (var x = minimumX; x <= maximumX; x++)
                {
                    var deltaX = x - center.x;
                    if (deltaX * deltaX + deltaY * deltaY <= radiusSquared)
                        workingPixels[y * texture.width + x] = color;
                }
            }
        }

        private void Apply()
        {
            texture.SetPixels32(workingPixels);
            texture.Apply(false, false);
        }

        private sealed class Stroke
        {
            public readonly Color32 Color;
            public readonly int Radius;
            public readonly List<Vector2Int> Points = new List<Vector2Int>();

            public Stroke(Color32 color, int radius)
            {
                Color = color;
                Radius = radius;
            }
        }
    }
}
