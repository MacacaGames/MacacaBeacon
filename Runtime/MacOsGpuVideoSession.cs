using System;
using UnityEngine;

namespace MacacaGames.RuntimeBugReporter
{
    internal sealed class MacOsGpuVideoSession : IDisposable
    {
        private IntPtr nativeSession;

        public bool IsOpen => nativeSession != IntPtr.Zero;
        public string OutputPath { get; }

        private MacOsGpuVideoSession(IntPtr nativeSession, string outputPath)
        {
            this.nativeSession = nativeSession;
            OutputPath = outputPath;
        }

        public static MacOsGpuVideoSession TryCreate(string outputPath, int width, int height, int framesPerSecond, int bitrateKbps)
        {
            var nativeSession = MacOsGpuVideoBridge.CreateSession(outputPath, width, height, framesPerSecond, bitrateKbps);
            return nativeSession == IntPtr.Zero ? null : new MacOsGpuVideoSession(nativeSession, outputPath);
        }

        public bool Submit(GpuFrameCapture.GpuFrame frame, double presentationSeconds)
        {
            return IsOpen && MacOsGpuVideoBridge.Submit(nativeSession, frame, presentationSeconds);
        }

        public bool Finish()
        {
            if (!IsOpen)
                return false;
            var finished = MacOsGpuVideoBridge.FinishSession(nativeSession);
            if (!finished)
                Debug.LogWarning("[Macaca Beacon] GPU MP4 finalization failed: " + MacOsGpuVideoBridge.GetLastError(nativeSession));
            return finished;
        }

        public void Dispose()
        {
            if (nativeSession == IntPtr.Zero)
                return;
            MacOsGpuVideoBridge.DestroySession(nativeSession);
            nativeSession = IntPtr.Zero;
        }
    }
}
