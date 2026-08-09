using System;
using System.Collections;
using System.Threading.Tasks;
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

        public static IEnumerator TryCreateAsync(
            string outputPath,
            int width,
            int height,
            int framesPerSecond,
            int bitrateKbps,
            Action<MacOsGpuVideoSession> completed)
        {
            var task = Task.Run(() =>
            {
                var nativeSession = MacOsGpuVideoBridge.CreateSessionOnBackgroundThread(
                    outputPath, width, height, framesPerSecond, bitrateKbps);
                return nativeSession == IntPtr.Zero
                    ? null
                    : new MacOsGpuVideoSession(nativeSession, outputPath);
            });
            while (!task.IsCompleted)
                yield return null;

            if (task.IsFaulted)
            {
                Debug.LogException(task.Exception);
                completed?.Invoke(null);
                yield break;
            }

            completed?.Invoke(task.Result);
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

        public IEnumerator FinishAsync(Action<bool> completed)
        {
            if (!IsOpen)
            {
                completed?.Invoke(false);
                yield break;
            }

            // Keep older macOS bundles usable until their native plugin is rebuilt
            // with the async exports. iOS always compiles the current source into
            // the generated Xcode project.
            if (!MacOsGpuVideoBridge.BeginFinishSession(nativeSession))
            {
                completed?.Invoke(Finish());
                yield break;
            }

            while (!MacOsGpuVideoBridge.IsFinishDone(nativeSession))
                yield return null;

            var finished = MacOsGpuVideoBridge.FinishSucceeded(nativeSession);
            if (!finished)
                Debug.LogWarning("[Macaca Beacon] GPU MP4 background finalization failed: " + MacOsGpuVideoBridge.GetLastError(nativeSession));
            completed?.Invoke(finished);
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
