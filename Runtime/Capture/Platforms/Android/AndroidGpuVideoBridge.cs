using System;
using System.Runtime.InteropServices;
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

        public static long CreateSession(string outputPath, int width, int height, int fps, int bitrateKbps)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!IsAvailable)
                return 0;
            using (var bridge = new AndroidJavaClass("com.macacagames.beacon.MacacaBeaconVideo"))
                return bridge.CallStatic<long>("createSurfaceSession", outputPath, width, height, fps, Math.Max(128, bitrateKbps) * 1000);
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

        public static bool FinishSession(long session)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (session == 0)
                return false;
            using (var bridge = new AndroidJavaClass("com.macacagames.beacon.MacacaBeaconVideo"))
                return bridge.CallStatic<int>("finishSurfaceSession", session) != 0;
#else
            return false;
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

        public static void DestroySession(long session)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (session == 0)
                return;
            using (var bridge = new AndroidJavaClass("com.macacagames.beacon.MacacaBeaconVideo"))
                bridge.CallStatic("destroySurfaceSession", session);
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
