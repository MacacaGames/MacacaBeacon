using System;
using System.Collections;
using Unity.Collections;
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

#if UNITY_WEBGL && !UNITY_EDITOR
            // WebGL AsyncGPUReadback can fence a READ buffer while the next
            // frame reuses it. Use the stable CPU path for Beacon captures;
            // WebCodecs still handles the final video compression.
            var webglTexture = ScreenCapture.CaptureScreenshotAsTexture();
            var webglBytes = webglTexture == null ? null : webglTexture.EncodeToPNG();
            completed?.Invoke(webglBytes, webglTexture);
            yield break;
#endif

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if (WindowsOpenH264Mp4Encoder.IsWine)
            {
                // Gamescope/Proton can update Screen.width/height one frame
                // before the actual backbuffer viewport changes. Allocating a
                // RenderTexture from that transient size leaves the captured
                // game image in only part of a larger canvas. Let Unity return
                // the exact current backbuffer texture for this one-shot UI
                // screenshot instead of predicting its dimensions.
                var protonTexture = ScreenCapture.CaptureScreenshotAsTexture();
                if (protonTexture != null && protonTexture.width > 0 && protonTexture.height > 0)
                {
                    completed?.Invoke(protonTexture.EncodeToPNG(), protonTexture);
                    yield break;
                }
            }
#endif

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
                        // iOS Metal's screenshot readback is bottom-left based even
                        // when graphicsUVStartsAtTop reports the render target origin.
                        // Keep the final PNG in the same top-left orientation as the
                        // Game View and the annotation UI.
#if UNITY_EDITOR_OSX || (UNITY_IOS && !UNITY_EDITOR)
                        const bool flipScreenshotRows = true;
#else
                        const bool flipScreenshotRows = false;
#endif
                        var vulkanScreenshotNeedsFlip =
                            Application.platform == RuntimePlatform.Android &&
                            SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan;
                        var windowsScreenshotNeedsFlip =
                            Application.platform == RuntimePlatform.WindowsEditor ||
                            Application.platform == RuntimePlatform.WindowsPlayer;
                        var nonAndroidOriginNeedsFlip =
                            Application.platform != RuntimePlatform.Android &&
                            !SystemInfo.graphicsUVStartsAtTop;
                        if (flipScreenshotRows || vulkanScreenshotNeedsFlip || windowsScreenshotNeedsFlip || nonAndroidOriginNeedsFlip)
                        {
                            EnsureRowSwapBuffer(width * 4);
                            FlipRowsInPlace(readbackBuffer, rowSwapBuffer, width, height, 4);
                        }
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

        public static IEnumerator CaptureScaledJpegAsync(
            int targetWidth,
            int quality,
            Action<byte[]> completed)
        {
            yield return new WaitForEndOfFrame();

#if UNITY_WEBGL && !UNITY_EDITOR
            completed?.Invoke(CaptureScaledJpeg(targetWidth, quality));
            yield break;
#endif

            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                completed?.Invoke(CaptureScaledJpeg(targetWidth, quality));
                yield break;
            }

            CalculateScaledSize(targetWidth, Screen.width, Screen.height, out var width, out var height);

            var renderTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            RenderTexture sourceTexture = null;
            var request = default(AsyncGPUReadbackRequest);
            Texture2D scaled = null;
            byte[] bytes = null;
            try
            {
                CaptureIntoRenderTexture(
                    renderTexture,
                    Screen.width,
                    Screen.height,
                    out sourceTexture);
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
                if (sourceTexture != null)
                    RenderTexture.ReleaseTemporary(sourceTexture);
                RenderTexture.ReleaseTemporary(renderTexture);
            }

            if (request.hasError || bytes == null)
                bytes = CaptureScaledJpeg(targetWidth, quality);
            completed?.Invoke(bytes);
        }

        public static IEnumerator CaptureScaledRgbaAsync(
            int targetWidth,
            Action<byte[], int, int> completed)
        {
            yield return new WaitForEndOfFrame();

#if UNITY_WEBGL && !UNITY_EDITOR
            // Fall through to CaptureScaledJpegAsync in RollingVideoRecorder.
            // Keeping AsyncGPUReadback out of WebGL avoids READ-buffer fence
            // churn and a potentially unbounded request.done wait.
            completed?.Invoke(null, 0, 0);
            yield break;
#endif

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
            CalculateScaledSize(targetWidth, sourceWidth, sourceHeight, out var width, out var height);

            var renderTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            RenderTexture sourceTexture = null;
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
                CaptureIntoRenderTexture(
                    renderTexture,
                    sourceWidth,
                    sourceHeight,
                    out sourceTexture);
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
#if UNITY_EDITOR_OSX || (UNITY_IOS && !UNITY_EDITOR)
                    const bool flipVideoRows = true;
#else
                    const bool flipVideoRows = false;
#endif
                    var windowsVideoNeedsFlip =
                        Application.platform == RuntimePlatform.WindowsEditor ||
                        Application.platform == RuntimePlatform.WindowsPlayer;
                    if (flipVideoRows || windowsVideoNeedsFlip || !SystemInfo.graphicsUVStartsAtTop)
                        FlipRowsInPlace(frame, rowSwapBuffer, width, height, 4);
                }
            }
            finally
            {
                if (editorSource != null)
                    UnityEngine.Object.Destroy(editorSource);
                if (sourceTexture != null)
                    RenderTexture.ReleaseTemporary(sourceTexture);
                RenderTexture.ReleaseTemporary(renderTexture);
            }

            completed?.Invoke(frame, frame == null ? 0 : width, frame == null ? 0 : height);
        }

        public static IEnumerator CaptureScaledRgbaIntoNativeArrayAsync(
            NativeVideoFrame destination,
            Action<bool, bool> completed)
        {
            yield return new WaitForEndOfFrame();

#if UNITY_WEBGL && !UNITY_EDITOR
            destination?.CompleteRequest(false);
            completed?.Invoke(false, false);
            yield break;
#else
            if (destination == null || !SystemInfo.supportsAsyncGPUReadback)
            {
                destination?.CompleteRequest(false);
                completed?.Invoke(false, false);
                yield break;
            }

            var renderTexture = RenderTexture.GetTemporary(
                destination.Width,
                destination.Height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB);
            RenderTexture sourceTexture = null;
            var request = default(AsyncGPUReadbackRequest);
            var succeeded = false;
            try
            {
                CaptureIntoRenderTexture(
                    renderTexture,
                    Screen.width,
                    Screen.height,
                    out sourceTexture);
                request = AsyncGPUReadback.RequestIntoNativeArray(
                    ref destination.Data,
                    renderTexture,
                    0,
                    TextureFormat.RGBA32,
                    null);
                while (!request.done)
                    yield return null;
                succeeded = !request.hasError;
            }
            finally
            {
                destination.CompleteRequest(succeeded);
                if (sourceTexture != null)
                    RenderTexture.ReleaseTemporary(sourceTexture);
                RenderTexture.ReleaseTemporary(renderTexture);
            }

            // Keep orientation as metadata. Deferred encoders can read rows in
            // reverse order without spending rolling-capture CPU time swapping
            // a multi-megabyte frame in place.
            // Unity's Windows/D3D readback normally needs the historical row
            // flip, but DXVK/VKD3D under Proton already returns this capture
            // in display order. Applying both transforms produced an upside-
            // down MP4 on Steam Deck.
            var rowsAreBottomUp = !WindowsOpenH264Mp4Encoder.IsWine &&
                (Application.platform == RuntimePlatform.WindowsEditor ||
                 Application.platform == RuntimePlatform.WindowsPlayer ||
                 !SystemInfo.graphicsUVStartsAtTop);
            completed?.Invoke(succeeded, rowsAreBottomUp);
#endif
        }

        internal static void CalculateScaledSize(
            int targetWidth,
            int sourceWidth,
            int sourceHeight,
            out int width,
            out int height)
        {
            width = Mathf.Max(2, Mathf.Min(targetWidth, sourceWidth));
            height = Mathf.Max(2, Mathf.RoundToInt(sourceHeight * (width / (float)Mathf.Max(1, sourceWidth))));
            width -= width % 2;
            height -= height % 2;
        }

        private static void CaptureIntoRenderTexture(
            RenderTexture target,
            int sourceWidth,
            int sourceHeight,
            out RenderTexture source)
        {
            source = null;
            if (target.width == sourceWidth && target.height == sourceHeight)
            {
                ScreenCapture.CaptureScreenshotIntoRenderTexture(target);
                return;
            }

            source = RenderTexture.GetTemporary(
                Mathf.Max(2, sourceWidth),
                Mathf.Max(2, sourceHeight),
                0,
                RenderTextureFormat.ARGB32);
            ScreenCapture.CaptureScreenshotIntoRenderTexture(source);
            Graphics.Blit(source, target);
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
