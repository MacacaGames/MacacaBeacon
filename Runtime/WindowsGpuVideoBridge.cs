using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
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
                try { return NativeIsAvailable() != 0 && NativeGpuIsAvailable() != 0; }
                catch (DllNotFoundException) { return false; }
                catch (EntryPointNotFoundException) { return false; }
                catch (BadImageFormatException) { return false; }
#else
                return false;
#endif
            }
        }

        public static IntPtr CreateSession(string outputPath, GpuFrameCapture.GpuFrame frame, int framesPerSecond, int bitrateKbps)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (!IsAvailable || !frame.IsValid)
                return IntPtr.Zero;
            try
            {
                return NativeGpuCreate(outputPath, frame.Width, frame.Height, framesPerSecond,
                    Math.Max(128, bitrateKbps) * 1000, frame.NativeTexturePtr);
            }
            catch (DllNotFoundException) { return IntPtr.Zero; }
            catch (EntryPointNotFoundException) { return IntPtr.Zero; }
            catch (BadImageFormatException) { return IntPtr.Zero; }
#else
            return IntPtr.Zero;
#endif
        }

        public static bool Submit(IntPtr session, GpuFrameCapture.GpuFrame frame, double presentationSeconds)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (session == IntPtr.Zero || !frame.IsValid || !IsAvailable)
                return false;
            try
            {
                var data = NativeAllocateSubmitData(session, frame.NativeTexturePtr, presentationSeconds);
                if (data == IntPtr.Zero)
                    return false;
                var commandBuffer = new CommandBuffer { name = "Macaca Beacon GPU video submit" };
                commandBuffer.IssuePluginEventAndData(NativeGetRenderEventFunc(), 1, data);
                Graphics.ExecuteCommandBuffer(commandBuffer);
                commandBuffer.Release();
                return true;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
            catch (BadImageFormatException) { return false; }
#else
            return false;
#endif
        }

        public static bool FinishSession(IntPtr session)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (session == IntPtr.Zero)
                return false;
            try { return NativeFinish(session) != 0; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
            catch (BadImageFormatException) { return false; }
#else
            return false;
#endif
        }

        public static string GetLastError(IntPtr session)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (session == IntPtr.Zero)
                return null;
            try
            {
                var pointer = NativeLastError(session);
                return pointer == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(pointer);
            }
            catch (DllNotFoundException) { return null; }
            catch (EntryPointNotFoundException) { return null; }
            catch (BadImageFormatException) { return null; }
#else
            return null;
#endif
        }

        public static void DestroySession(IntPtr session)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (session != IntPtr.Zero)
            {
                try { NativeDestroy(session); }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
                catch (BadImageFormatException) { }
            }
#endif
        }

        public static bool ConcatSegments(string outputPath, IReadOnlyList<string> inputPaths)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (string.IsNullOrEmpty(outputPath) || inputPaths == null || inputPaths.Count == 0)
                return false;

            var outputPointer = AllocateUtf8(outputPath);
            var pathPointers = new IntPtr[inputPaths.Count];
            var arrayPointer = IntPtr.Zero;
            try
            {
                for (var index = 0; index < inputPaths.Count; index++)
                    pathPointers[index] = AllocateUtf8(inputPaths[index]);
                arrayPointer = Marshal.AllocHGlobal(IntPtr.Size * pathPointers.Length);
                for (var index = 0; index < pathPointers.Length; index++)
                    Marshal.WriteIntPtr(arrayPointer, index * IntPtr.Size, pathPointers[index]);
                return NativeConcatSegments(outputPointer, arrayPointer, pathPointers.Length) != 0;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
            finally
            {
                if (arrayPointer != IntPtr.Zero)
                    Marshal.FreeHGlobal(arrayPointer);
                for (var index = 0; index < pathPointers.Length; index++)
                {
                    if (pathPointers[index] != IntPtr.Zero)
                        Marshal.FreeHGlobal(pathPointers[index]);
                }
                if (outputPointer != IntPtr.Zero)
                    Marshal.FreeHGlobal(outputPointer);
            }
#else
            return false;
#endif
        }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        private static IntPtr AllocateUtf8(string value)
        {
            var bytes = Encoding.UTF8.GetBytes((value ?? string.Empty) + "\0");
            var pointer = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            return pointer;
        }

        [DllImport("MacacaBeaconVideoWindows", EntryPoint = "MacacaBeaconWindowsVideo_IsAvailable", CallingConvention = CallingConvention.Cdecl)]
        private static extern int NativeIsAvailable();
        [DllImport("MacacaBeaconVideoWindows", EntryPoint = "MacacaBeaconWindowsVideo_GpuIsAvailable", CallingConvention = CallingConvention.Cdecl)]
        private static extern int NativeGpuIsAvailable();
        [DllImport("MacacaBeaconVideoWindows", EntryPoint = "MacacaBeaconWindowsVideo_GpuCreate", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NativeGpuCreate([MarshalAs(UnmanagedType.LPUTF8Str)] string outputPath, int width, int height, int framesPerSecond, int bitrate, IntPtr nativeTexture);
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
        [DllImport("MacacaBeaconVideoWindows", EntryPoint = "MacacaBeaconWindowsVideo_ConcatSegments", CallingConvention = CallingConvention.Cdecl)]
        private static extern int NativeConcatSegments(IntPtr outputPath, IntPtr inputPaths, int inputCount);
#endif
    }
}
