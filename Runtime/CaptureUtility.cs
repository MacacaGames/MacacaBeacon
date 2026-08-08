using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace MacacaGames.RuntimeBugReporter
{
    internal static class CaptureUtility
    {
        // CaptureScaledJpegAsync runs serially on the Unity thread. Reuse the
        // readback buffers so Android does not allocate two full RGBA frames
        // for every captured screenshot.
        private static byte[] readbackBuffer;
        private static byte[] rowSwapBuffer;

        public static IEnumerator CapturePng(Action<byte[], Texture2D> completed)
        {
            yield return new WaitForEndOfFrame();

            // Use the same backbuffer capture path as video on Android. The
            // AsTexture path can use the logical orientation/viewport size,
            // which produces an incorrectly framed portrait capture when the
            // display is letterboxed or rotated.
            if (SystemInfo.supportsAsyncGPUReadback)
            {
                var width = CaptureWidth();
                var height = CaptureHeight();
                var renderTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                var request = default(AsyncGPUReadbackRequest);
                Texture2D capturedTexture = null;
                try
                {
                    ScreenCapture.CaptureScreenshotIntoRenderTexture(renderTexture);
                    request = AsyncGPUReadback.Request(renderTexture, 0, TextureFormat.RGBA32);
                    while (!request.done)
                        yield return null;
                    if (!request.hasError)
                    {
                        var raw = request.GetData<byte>();
                        EnsureReadbackBuffers(raw.Length, width * 4);
                        raw.CopyTo(readbackBuffer);
                        // Android's CaptureScreenshotIntoRenderTexture readback
                        // is bottom-up even when the graphics UV origin is top.
                        // Keep the platform correction separate from video's
                        // backend-specific texture orientation.
#if UNITY_ANDROID && !UNITY_EDITOR
                        EnsureRowSwapBuffer(width * 4);
                        FlipRowsInPlace(readbackBuffer, rowSwapBuffer, width, height, 4);
#elif !UNITY_ANDROID || UNITY_EDITOR
                        if (!SystemInfo.graphicsUVStartsAtTop)
                        {
                            EnsureRowSwapBuffer(width * 4);
                            FlipRowsInPlace(readbackBuffer, rowSwapBuffer, width, height, 4);
                        }
#endif
                        capturedTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                        capturedTexture.LoadRawTextureData(readbackBuffer);
                        capturedTexture.Apply(false, false);
                        completed?.Invoke(capturedTexture.EncodeToPNG(), capturedTexture);
                        yield break;
                    }
                }
                finally
                {
                    RenderTexture.ReleaseTemporary(renderTexture);
                }
            }

            var fallbackTexture = ScreenCapture.CaptureScreenshotAsTexture();
            byte[] bytes = null;
            if (fallbackTexture != null)
                bytes = fallbackTexture.EncodeToPNG();
            completed?.Invoke(bytes, fallbackTexture);
        }

        private static int CaptureWidth()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (Display.main != null && Display.main.renderingWidth > 0)
                return Display.main.renderingWidth;
#endif
            return Mathf.Max(2, Screen.width);
        }

        private static int CaptureHeight()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (Display.main != null && Display.main.renderingHeight > 0)
                return Display.main.renderingHeight;
#endif
            return Mathf.Max(2, Screen.height);
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

        public static IEnumerator CaptureScaledJpegAsync(int targetWidth, int quality, Action<byte[]> completed)
        {
            yield return new WaitForEndOfFrame();

            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                completed?.Invoke(CaptureScaledJpeg(targetWidth, quality));
                yield break;
            }

            var width = Mathf.Max(2, Mathf.Min(targetWidth, Screen.width));
            var height = Mathf.Max(2, Mathf.RoundToInt(Screen.height * (width / (float)Mathf.Max(1, Screen.width))));
            width -= width % 2;
            height -= height % 2;

            var renderTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            var request = default(AsyncGPUReadbackRequest);
            Texture2D scaled = null;
            byte[] bytes = null;
            try
            {
                ScreenCapture.CaptureScreenshotIntoRenderTexture(renderTexture);
                request = AsyncGPUReadback.Request(renderTexture, 0, TextureFormat.RGBA32);
                while (!request.done)
                    yield return null;

                if (!request.hasError)
                {
                    var raw = request.GetData<byte>();
                    EnsureReadbackBuffers(raw.Length, width * 4);
                    raw.CopyTo(readbackBuffer);
                    FlipRowsInPlace(readbackBuffer, rowSwapBuffer, width, height, 4);
                    scaled = new Texture2D(width, height, TextureFormat.RGBA32, false);
                    scaled.LoadRawTextureData(readbackBuffer);
                    scaled.Apply(false, false);
                    bytes = scaled.EncodeToJPG(quality);
                }
            }
            finally
            {
                if (scaled != null)
                    UnityEngine.Object.Destroy(scaled);
                RenderTexture.ReleaseTemporary(renderTexture);
            }

            if (request.hasError || bytes == null)
                bytes = CaptureScaledJpeg(targetWidth, quality);
            completed?.Invoke(bytes);
        }

        public static IEnumerator CaptureScaledRgbaAsync(int targetWidth, Action<byte[], int, int> completed)
        {
            yield return new WaitForEndOfFrame();

            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                completed?.Invoke(null, 0, 0);
                yield break;
            }

            Texture2D editorSource = null;
#if UNITY_EDITOR
            // In the Editor, Screen.width/height can describe the host window
            // rather than the Game View backbuffer. Capture the actual Game
            // View first so the recording rectangle and aspect ratio match.
            editorSource = ScreenCapture.CaptureScreenshotAsTexture();
#endif
            var sourceWidth = editorSource == null ? Screen.width : editorSource.width;
            var sourceHeight = editorSource == null ? Screen.height : editorSource.height;
            var width = Mathf.Max(2, Mathf.Min(targetWidth, sourceWidth));
            var height = Mathf.Max(2, Mathf.RoundToInt(sourceHeight * (width / (float)Mathf.Max(1, sourceWidth))));
            width -= width % 2;
            height -= height % 2;

            var renderTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            var request = default(AsyncGPUReadbackRequest);
            byte[] frame = null;
            try
            {
#if UNITY_EDITOR
                if (editorSource == null)
                {
                    completed?.Invoke(null, 0, 0);
                    yield break;
                }
                Graphics.Blit(editorSource, renderTexture);
#else
                ScreenCapture.CaptureScreenshotIntoRenderTexture(renderTexture);
#endif
                request = AsyncGPUReadback.Request(renderTexture, 0, TextureFormat.RGBA32);
                while (!request.done)
                    yield return null;

                if (!request.hasError)
                {
                    var raw = request.GetData<byte>();
                    frame = new byte[raw.Length];
                    raw.CopyTo(frame);
                    EnsureRowSwapBuffer(width * 4);
                    // Use the active graphics backend instead of hard-coding
                    // platform names. Metal, Vulkan, GLES and D3D can expose
                    // different texture origins between Editor and Player.
                    if (!SystemInfo.graphicsUVStartsAtTop)
                    FlipRowsInPlace(frame, rowSwapBuffer, width, height, 4);
                }
            }
            finally
            {
                if (editorSource != null)
                    UnityEngine.Object.Destroy(editorSource);
                RenderTexture.ReleaseTemporary(renderTexture);
            }

            completed?.Invoke(frame, frame == null ? 0 : width, frame == null ? 0 : height);
        }

        private static void EnsureReadbackBuffers(int byteCount, int rowLength)
        {
            if (readbackBuffer == null || readbackBuffer.Length != byteCount)
                readbackBuffer = new byte[byteCount];
            if (rowSwapBuffer == null || rowSwapBuffer.Length != rowLength)
                rowSwapBuffer = new byte[rowLength];
        }

        private static void EnsureRowSwapBuffer(int rowLength)
        {
            if (rowSwapBuffer == null || rowSwapBuffer.Length != rowLength)
                rowSwapBuffer = new byte[rowLength];
        }

        private static void FlipRowsInPlace(byte[] source, byte[] rowBuffer, int width, int height, int bytesPerPixel)
        {
            var rowLength = width * bytesPerPixel;
            var halfHeight = height / 2;
            for (var row = 0; row < height; row++)
            {
                var oppositeRow = height - 1 - row;
                if (row >= halfHeight)
                    break;
                Buffer.BlockCopy(source, row * rowLength, rowBuffer, 0, rowLength);
                Buffer.BlockCopy(source, oppositeRow * rowLength, source, row * rowLength, rowLength);
                Buffer.BlockCopy(rowBuffer, 0, source, oppositeRow * rowLength, rowLength);
            }
        }
    }
}
