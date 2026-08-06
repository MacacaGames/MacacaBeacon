using System;
using System.Collections;
using UnityEngine;

namespace MacacaGames.RuntimeBugReporter
{
    internal static class CaptureUtility
    {
        public static IEnumerator CapturePng(Action<byte[], Texture2D> completed)
        {
            yield return new WaitForEndOfFrame();
            var texture = ScreenCapture.CaptureScreenshotAsTexture();
            byte[] bytes = null;
            if (texture != null)
                bytes = texture.EncodeToPNG();
            completed?.Invoke(bytes, texture);
        }

        public static byte[] CaptureScaledJpeg(int targetWidth, int quality)
        {
            var source = ScreenCapture.CaptureScreenshotAsTexture();
            if (source == null)
                return null;

            var width = Mathf.Max(2, Mathf.Min(targetWidth, source.width));
            var height = Mathf.Max(2, Mathf.RoundToInt(source.height * (width / (float)source.width)));
            width -= width % 2;
            height -= height % 2;

            var renderTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;
            Texture2D scaled = null;
            try
            {
                Graphics.Blit(source, renderTexture);
                RenderTexture.active = renderTexture;
                scaled = new Texture2D(width, height, TextureFormat.RGB24, false);
                scaled.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                scaled.Apply(false, false);
                return scaled.EncodeToJPG(quality);
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(renderTexture);
                UnityEngine.Object.Destroy(source);
                if (scaled != null)
                    UnityEngine.Object.Destroy(scaled);
            }
        }
    }
}
