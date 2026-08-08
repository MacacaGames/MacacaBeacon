using System;
using System.Collections;
using System.Runtime.InteropServices;
using UnityEngine;

namespace MacacaGames.RuntimeBugReporter
{
    /// <summary>
    /// Captures the final Unity game view into persistent GPU textures. This
    /// intentionally does not perform an AsyncGPUReadback; the native video
    /// bridge can consume NativeTexturePtr on the render thread.
    /// </summary>
    internal sealed class GpuFrameCapture : IDisposable
    {
        private const int BufferCount = 2;
        private readonly RenderTexture[] buffers = new RenderTexture[BufferCount];
        private readonly RenderTexture[] sourceBuffers = new RenderTexture[BufferCount];
        private int nextBuffer;
        private int width;
        private int height;

        public bool IsSupported => SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;

        public IEnumerator Capture(int targetWidth, Action<GpuFrame> completed)
        {
            yield return new WaitForEndOfFrame();

            if (!IsSupported)
            {
                completed?.Invoke(default(GpuFrame));
                yield break;
            }

            var sourceWidth = Mathf.Max(1, Screen.width);
            var sourceHeight = Mathf.Max(1, Screen.height);
            var captureWidth = Mathf.Max(2, Mathf.Min(targetWidth, sourceWidth));
            var captureHeight = Mathf.Max(2, Mathf.RoundToInt(sourceHeight * (captureWidth / (float)sourceWidth)));
            captureWidth -= captureWidth % 2;
            captureHeight -= captureHeight % 2;
            EnsureBuffers(sourceWidth, sourceHeight, captureWidth, captureHeight);

            var bufferIndex = nextBuffer;
            nextBuffer = (nextBuffer + 1) % BufferCount;
            var target = buffers[bufferIndex];
            var source = sourceBuffers[bufferIndex];
            ScreenCapture.CaptureScreenshotIntoRenderTexture(source);
            // Keep the full-size capture separate from the encoder target.
            // CaptureScreenshotIntoRenderTexture is not a reliable scaler on
            // every Editor backend; Blit performs the explicit GPU resize.
            Graphics.Blit(source, target);

            // CaptureScreenshotIntoRenderTexture queues GPU work. Yield once
            // before exposing the texture so callers can issue their native
            // plugin event after the capture command has been submitted.
            yield return null;
            completed?.Invoke(new GpuFrame(target, target.GetNativeTexturePtr(), width, height));
        }

        private void EnsureBuffers(int requestedSourceWidth, int requestedSourceHeight, int requestedWidth, int requestedHeight)
        {
            if (width == requestedWidth && height == requestedHeight &&
                buffers[0] != null && buffers[1] != null &&
                sourceBuffers[0] != null && sourceBuffers[1] != null &&
                sourceBuffers[0].width == requestedSourceWidth && sourceBuffers[0].height == requestedSourceHeight)
                return;

            ReleaseBuffers();
            width = requestedWidth;
            height = requestedHeight;
            for (var index = 0; index < buffers.Length; index++)
            {
                var target = CreateCaptureTexture(width, height);
                target.name = "MacacaBeacon.GpuCapture." + index;
                target.filterMode = FilterMode.Bilinear;
                target.wrapMode = TextureWrapMode.Clamp;
                target.useMipMap = false;
                target.autoGenerateMips = false;
                target.antiAliasing = 1;
                target.Create();
                buffers[index] = target;

                var source = CreateCaptureTexture(requestedSourceWidth, requestedSourceHeight);
                source.name = "MacacaBeacon.GpuCapture.Source." + index;
                source.filterMode = FilterMode.Bilinear;
                source.wrapMode = TextureWrapMode.Clamp;
                source.useMipMap = false;
                source.autoGenerateMips = false;
                source.antiAliasing = 1;
                source.Create();
                sourceBuffers[index] = source;
            }
            nextBuffer = 0;
        }

        private static RenderTexture CreateCaptureTexture(int textureWidth, int textureHeight)
        {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            // CVPixelBuffer and the Metal encoder bridge both consume BGRA bytes.
            // Make the native Metal texture layout explicit instead of relying on
            // the platform-dependent native mapping of RenderTextureFormat.ARGB32.
            return new RenderTexture(textureWidth, textureHeight, 0, RenderTextureFormat.BGRA32, RenderTextureReadWrite.sRGB);
#else
            return new RenderTexture(textureWidth, textureHeight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
#endif
        }

        private void ReleaseBuffers()
        {
            for (var index = 0; index < buffers.Length; index++)
            {
                if (buffers[index] == null)
                    continue;
                buffers[index].Release();
                UnityEngine.Object.Destroy(buffers[index]);
                buffers[index] = null;
                if (sourceBuffers[index] != null)
                {
                    sourceBuffers[index].Release();
                    UnityEngine.Object.Destroy(sourceBuffers[index]);
                    sourceBuffers[index] = null;
                }
            }
        }

        public void Dispose()
        {
            ReleaseBuffers();
        }

        internal readonly struct GpuFrame
        {
            public readonly RenderTexture Texture;
            public readonly IntPtr NativeTexturePtr;
            public readonly int Width;
            public readonly int Height;

            public bool IsValid => Texture != null && Texture.IsCreated() && NativeTexturePtr != IntPtr.Zero;

            public GpuFrame(RenderTexture texture, IntPtr nativeTexturePtr, int width, int height)
            {
                Texture = texture;
                NativeTexturePtr = nativeTexturePtr;
                Width = width;
                Height = height;
            }
        }
    }
}
