using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace MacacaGames.RuntimeBugReporter
{
    /// <summary>
    /// Render-thread bridge for the macOS Metal texture encoder. The texture
    /// is submitted without AsyncGPUReadback or Texture2D conversion.
    /// </summary>
    internal static class MacOsGpuVideoBridge
    {
        public static bool IsAvailable
        {
            get
            {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
                if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Metal)
                    return false;
                try { return NativeIsAvailable() != 0; }
                catch (DllNotFoundException) { return false; }
                catch (EntryPointNotFoundException) { return false; }
#else
                return false;
#endif
            }
        }

        public static bool Submit(IntPtr session, GpuFrameCapture.GpuFrame frame, double presentationSeconds)
        {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            if (session == IntPtr.Zero || !frame.IsValid || !IsAvailable)
                return false;

            var submitData = NativeAllocateSubmitData(session, frame.NativeTexturePtr, presentationSeconds);
            if (submitData == IntPtr.Zero)
                return false;

            var commandBuffer = new CommandBuffer { name = "Macaca Beacon GPU video submit" };
            commandBuffer.IssuePluginEventAndData(NativeGetRenderEventFunc(), 1, submitData);
            Graphics.ExecuteCommandBuffer(commandBuffer);
            commandBuffer.Release();
            return true;
#else
            return false;
#endif
        }

        public static IntPtr CreateSession(string outputPath, int width, int height, int framesPerSecond, int bitrateKbps)
        {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            if (!IsAvailable)
                return IntPtr.Zero;
            try
            {
                return NativeGpuCreate(outputPath, width, height, framesPerSecond, Math.Max(128, bitrateKbps) * 1000);
            }
            catch (DllNotFoundException) { return IntPtr.Zero; }
            catch (EntryPointNotFoundException) { return IntPtr.Zero; }
#else
            return IntPtr.Zero;
#endif
        }

        public static bool FinishSession(IntPtr session)
        {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            return session != IntPtr.Zero && NativeFinish(session) != 0;
#else
            return false;
#endif
        }

        public static string GetLastError(IntPtr session)
        {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            var pointer = NativeLastError(session);
            return pointer == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(pointer);
#else
            return null;
#endif
        }

        public static void DestroySession(IntPtr session)
        {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            if (session != IntPtr.Zero)
                NativeDestroy(session);
#endif
        }

        public static bool ConcatSegments(string outputPath, IReadOnlyList<string> inputPaths)
        {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
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

#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
        private static IntPtr AllocateUtf8(string value)
        {
            var bytes = Encoding.UTF8.GetBytes((value ?? string.Empty) + "\0");
            var pointer = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            return pointer;
        }
#endif

#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_Create")]
        private static extern IntPtr NativeCreate([MarshalAs(UnmanagedType.LPUTF8Str)] string outputPath, int width, int height, int framesPerSecond, int bitrate);

        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_GpuCreate")]
        private static extern IntPtr NativeGpuCreate([MarshalAs(UnmanagedType.LPUTF8Str)] string outputPath, int width, int height, int framesPerSecond, int bitrate);

        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_Finish")]
        private static extern int NativeFinish(IntPtr session);

        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_LastError")]
        private static extern IntPtr NativeLastError(IntPtr session);

        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_Destroy")]
        private static extern void NativeDestroy(IntPtr session);

        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_GpuIsAvailable")]
        private static extern int NativeIsAvailable();

        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_GpuGetRenderEventFunc")]
        private static extern IntPtr NativeGetRenderEventFunc();

        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_GpuAllocateSubmitData")]
        private static extern IntPtr NativeAllocateSubmitData(IntPtr session, IntPtr nativeTexture, double presentationSeconds);

        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_ConcatSegments")]
        private static extern int NativeConcatSegments(IntPtr outputPath, IntPtr inputPaths, int inputCount);
#endif
    }
}
