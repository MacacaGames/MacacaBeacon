using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace MacacaGames.RuntimeBugReporter
{
    internal static class AndroidGpuVideoBridge
    {
        private static bool loggedBackend;
        public static bool IsAvailable
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.OpenGLES3 &&
                    SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan)
                return false;
                try
                {
                    var available = NativeGetRenderEventFunc() != IntPtr.Zero;
                    if (available && !loggedBackend)
                    {
                        Debug.Log("[Macaca Beacon] Android GPU video backend: " + SystemInfo.graphicsDeviceType);
                        loggedBackend = true;
                    }
                    return available;
                }
                catch (DllNotFoundException) { return false; }
                catch (EntryPointNotFoundException) { return false; }
#else
                return false;
#endif
            }
        }

        public static long CreateSession(int width, int height, int fps, int bitrateKbps, int secondsBefore, int maximumCacheMegabytes)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!IsAvailable)
                return 0;
            using (var bridge = new AndroidJavaClass("com.macacagames.beacon.MacacaBeaconVideo"))
                return bridge.CallStatic<long>("createSurfaceSession", width, height, fps, Math.Max(128, bitrateKbps) * 1000,
                    (double)Math.Max(0, secondsBefore), Math.Max(1L, maximumCacheMegabytes) * 1024L * 1024L);
#else
            return 0;
#endif
        }

        public static bool Submit(long session, GpuFrameCapture.GpuFrame frame, double presentationSeconds)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (session == 0 || !frame.IsValid || !IsAvailable)
                return false;
            var data = NativeAllocateSubmitData(session, frame.NativeTexturePtr, presentationSeconds);
            if (data == IntPtr.Zero)
                return false;
            var commandBuffer = new CommandBuffer { name = "Macaca Beacon Android GPU video submit" };
            commandBuffer.IssuePluginEventAndData(NativeGetRenderEventFunc(), 2, data);
            commandBuffer.IssuePluginEventAndData(NativeGetRenderEventFunc(), 1, data);
            Graphics.ExecuteCommandBuffer(commandBuffer);
            commandBuffer.Release();
            return true;
#else
            return false;
#endif
        }

        public static bool BeginClip(long session, string path, double startSeconds, double endSeconds)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using (var bridge = new AndroidJavaClass("com.macacagames.beacon.MacacaBeaconVideo"))
                return bridge.CallStatic<int>("beginSurfaceClip", session, path, startSeconds, endSeconds) != 0;
#else
            return false;
#endif
        }

        public static int ClipState(long session)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using (var bridge = new AndroidJavaClass("com.macacagames.beacon.MacacaBeaconVideo"))
                return bridge.CallStatic<int>("surfaceClipState", session);
#else
            return -1;
#endif
        }

        public static double[] ClipInfo(long session)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using (var bridge = new AndroidJavaClass("com.macacagames.beacon.MacacaBeaconVideo"))
                return bridge.CallStatic<double[]>("surfaceClipInfo", session);
#else
            return Array.Empty<double>();
#endif
        }

        public static void ReleaseClip(long session, bool cancel)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using (var bridge = new AndroidJavaClass("com.macacagames.beacon.MacacaBeaconVideo"))
                bridge.CallStatic("releaseSurfaceClip", session, cancel);
#endif
        }

        public static string LastCreateError()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using (var bridge = new AndroidJavaClass("com.macacagames.beacon.MacacaBeaconVideo"))
                return bridge.CallStatic<string>("lastCreateError");
#else
            return null;
#endif
        }

        public static string LastError(long session)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using (var bridge = new AndroidJavaClass("com.macacagames.beacon.MacacaBeaconVideo"))
                return bridge.CallStatic<string>("surfaceSessionError", session);
#else
            return null;
#endif
        }

        public static void DestroySession(long session, Action completed)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            var context = SynchronizationContext.Current;
            Task.Run(() =>
            {
                AndroidJNI.AttachCurrentThread();
                try
                {
                    using (var bridge = new AndroidJavaClass("com.macacagames.beacon.MacacaBeaconVideo"))
                        bridge.CallStatic("destroySurfaceSession", session);
                    // RenderTextures are Unity objects. Release them on the
                    // captured main thread only after native submissions finish.
                    context.Post(_ => completed?.Invoke(), null);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("[Macaca Beacon] Android asynchronous cleanup failed: " + exception.Message);
                }
                finally
                {
                    AndroidJNI.DetachCurrentThread();
                }
            });
#else
            completed?.Invoke();
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        [DllImport("MacacaBeaconAndroidVideo", EntryPoint = "MacacaBeaconAndroidVideo_GetRenderEventFunc")]
        private static extern IntPtr NativeGetRenderEventFunc();

        [DllImport("MacacaBeaconAndroidVideo", EntryPoint = "MacacaBeaconAndroidVideo_AllocateSubmitData")]
        private static extern IntPtr NativeAllocateSubmitData(long session, IntPtr nativeTexture, double presentationSeconds);
#endif
    }
}
