using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace MacacaGames.RuntimeBugReporter
{
    /// <summary>
    /// Render-thread bridge for the Apple Metal texture encoder. The texture
    /// is submitted without AsyncGPUReadback or Texture2D conversion.
    /// </summary>
    internal static class MacOsGpuVideoBridge
    {
        public static bool IsAvailable
        {
            get
            {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_IOS
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
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_IOS
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
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_IOS
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

        // Must only be called from a background worker after IsAvailable has
        // already been checked on Unity's main thread. NativeGpuCreate does
        // not access Unity graphics state; calling IsAvailable here would read
        // SystemInfo from a non-main thread and can crash IL2CPP on iOS.
        public static IntPtr CreateSessionOnBackgroundThread(string outputPath, int width, int height, int framesPerSecond, int bitrateKbps)
        {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_IOS
            try
            {
                return NativeGpuCreate(outputPath, width, height, framesPerSecond, Math.Max(128, bitrateKbps) * 1000);
            }
            catch (DllNotFoundException) { return IntPtr.Zero; }
            catch (EntryPointNotFoundException) { return IntPtr.Zero; }
            catch (BadImageFormatException) { return IntPtr.Zero; }
#else
            return IntPtr.Zero;
#endif
        }

        public static bool FinishSession(IntPtr session)
        {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_IOS
            return session != IntPtr.Zero && NativeFinish(session) != 0;
#else
            return false;
#endif
        }

        public static bool BeginFinishSession(IntPtr session)
        {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_IOS
            if (session == IntPtr.Zero)
                return false;
            try
            {
                return NativeBeginFinish(session) != 0;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
            catch (BadImageFormatException) { return false; }
#else
            return false;
#endif
        }

        public static bool IsFinishDone(IntPtr session)
        {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_IOS
            return session != IntPtr.Zero && NativeIsFinishDone(session) != 0;
#else
            return false;
#endif
        }

        public static bool FinishSucceeded(IntPtr session)
        {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_IOS
            return session != IntPtr.Zero && NativeFinishSucceeded(session) != 0;
#else
            return false;
#endif
        }

        public static string GetLastError(IntPtr session)
        {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_IOS
            var pointer = NativeLastError(session);
            return pointer == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(pointer);
#else
            return null;
#endif
        }

        public static void DestroySession(IntPtr session)
        {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_IOS
            if (session != IntPtr.Zero)
                NativeDestroy(session);
#endif
        }

        public static bool ConcatSegments(string outputPath, IReadOnlyList<string> inputPaths)
        {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_IOS
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

#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_IOS
        private static IntPtr AllocateUtf8(string value)
        {
            var bytes = Encoding.UTF8.GetBytes((value ?? string.Empty) + "\0");
            var pointer = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            return pointer;
        }
#endif

#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_IOS
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal", EntryPoint = "MacacaBeaconVideo_Create")]
#else
        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_Create")]
#endif
        private static extern IntPtr NativeCreate([MarshalAs(UnmanagedType.LPUTF8Str)] string outputPath, int width, int height, int framesPerSecond, int bitrate);

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal", EntryPoint = "MacacaBeaconVideo_GpuCreate")]
#else
        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_GpuCreate")]
#endif
        private static extern IntPtr NativeGpuCreate([MarshalAs(UnmanagedType.LPUTF8Str)] string outputPath, int width, int height, int framesPerSecond, int bitrate);

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal", EntryPoint = "MacacaBeaconVideo_Finish")]
#else
        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_Finish")]
#endif
        private static extern int NativeFinish(IntPtr session);

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal", EntryPoint = "MacacaBeaconVideo_BeginFinish")]
#else
        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_BeginFinish")]
#endif
        private static extern int NativeBeginFinish(IntPtr session);

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal", EntryPoint = "MacacaBeaconVideo_IsFinishDone")]
#else
        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_IsFinishDone")]
#endif
        private static extern int NativeIsFinishDone(IntPtr session);

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal", EntryPoint = "MacacaBeaconVideo_FinishSucceeded")]
#else
        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_FinishSucceeded")]
#endif
        private static extern int NativeFinishSucceeded(IntPtr session);

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal", EntryPoint = "MacacaBeaconVideo_LastError")]
#else
        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_LastError")]
#endif
        private static extern IntPtr NativeLastError(IntPtr session);

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal", EntryPoint = "MacacaBeaconVideo_Destroy")]
#else
        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_Destroy")]
#endif
        private static extern void NativeDestroy(IntPtr session);

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal", EntryPoint = "MacacaBeaconVideo_GpuIsAvailable")]
#else
        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_GpuIsAvailable")]
#endif
        private static extern int NativeIsAvailable();

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal", EntryPoint = "MacacaBeaconVideo_GpuGetRenderEventFunc")]
#else
        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_GpuGetRenderEventFunc")]
#endif
        private static extern IntPtr NativeGetRenderEventFunc();

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal", EntryPoint = "MacacaBeaconVideo_GpuAllocateSubmitData")]
#else
        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_GpuAllocateSubmitData")]
#endif
        private static extern IntPtr NativeAllocateSubmitData(IntPtr session, IntPtr nativeTexture, double presentationSeconds);

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal", EntryPoint = "MacacaBeaconVideo_ConcatSegments")]
#else
        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_ConcatSegments")]
#endif
        private static extern int NativeConcatSegments(IntPtr outputPath, IntPtr inputPaths, int inputCount);
#endif
    }
}
