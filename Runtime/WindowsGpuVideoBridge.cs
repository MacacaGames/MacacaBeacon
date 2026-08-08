using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace MacacaGames.RuntimeBugReporter
{
    internal static class WindowsGpuVideoBridge
    {
        public static bool IsAvailable
        {
            get
            {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
                if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
                    return false;
                try { return NativeIsAvailable() != 0; }
                catch (DllNotFoundException) { return false; }
                catch (EntryPointNotFoundException) { return false; }
                catch (BadImageFormatException) { return false; }
#else
                return false;
#endif
            }
        }

        public static IntPtr CreateSession(string outputPath, int width, int height, int framesPerSecond, int bitrateKbps)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (!IsAvailable)
                return IntPtr.Zero;
            return NativeCreate(outputPath, width, height, framesPerSecond, Math.Max(128, bitrateKbps) * 1000);
#else
            return IntPtr.Zero;
#endif
        }

        public static bool Submit(IntPtr session, GpuFrameCapture.GpuFrame frame, double presentationSeconds)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (session == IntPtr.Zero || !frame.IsValid || !IsAvailable)
                return false;
            var data = NativeAllocateSubmitData(session, frame.NativeTexturePtr, presentationSeconds);
            if (data == IntPtr.Zero)
                return false;
            var commandBuffer = new CommandBuffer { name = "Macaca Beacon GPU video submit" };
            commandBuffer.IssuePluginEventAndData(NativeGetRenderEventFunc(), 1, data);
            Graphics.ExecuteCommandBuffer(commandBuffer);
            commandBuffer.Release();
            return true;
#else
            return false;
#endif
        }

        public static bool FinishSession(IntPtr session)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            return session != IntPtr.Zero && NativeFinish(session) != 0;
#else
            return false;
#endif
        }

        public static string GetLastError(IntPtr session)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            var pointer = NativeLastError(session);
            return pointer == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(pointer);
#else
            return null;
#endif
        }

        public static void DestroySession(IntPtr session)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (session != IntPtr.Zero)
                NativeDestroy(session);
#endif
        }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        [DllImport("MacacaBeaconVideoWindows", EntryPoint = "MacacaBeaconWindowsVideo_IsAvailable", CallingConvention = CallingConvention.Cdecl)]
        private static extern int NativeIsAvailable();
        [DllImport("MacacaBeaconVideoWindows", EntryPoint = "MacacaBeaconWindowsVideo_Create", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NativeCreate([MarshalAs(UnmanagedType.LPUTF8Str)] string outputPath, int width, int height, int framesPerSecond, int bitrate);
        [DllImport("MacacaBeaconVideoWindows", EntryPoint = "MacacaBeaconWindowsVideo_GpuGetRenderEventFunc", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NativeGetRenderEventFunc();
        [DllImport("MacacaBeaconVideoWindows", EntryPoint = "MacacaBeaconWindowsVideo_GpuAllocateSubmitData", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NativeAllocateSubmitData(IntPtr session, IntPtr nativeTexture, double presentationSeconds);
        [DllImport("MacacaBeaconVideoWindows", EntryPoint = "MacacaBeaconWindowsVideo_Finish", CallingConvention = CallingConvention.Cdecl)]
        private static extern int NativeFinish(IntPtr session);
        [DllImport("MacacaBeaconVideoWindows", EntryPoint = "MacacaBeaconWindowsVideo_LastError", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NativeLastError(IntPtr session);
        [DllImport("MacacaBeaconVideoWindows", EntryPoint = "MacacaBeaconWindowsVideo_Destroy", CallingConvention = CallingConvention.Cdecl)]
        private static extern void NativeDestroy(IntPtr session);
#endif
    }
}
