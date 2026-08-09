using System;
using System.Collections;
using System.Threading.Tasks;
using UnityEngine;

namespace MacacaGames.RuntimeBugReporter
{
    internal sealed class MacOsGpuVideoSession : IDisposable
    {
        private const double CreateTimeoutSeconds = 10.0;
        private const double FinishTimeoutSeconds = 35.0;
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
#if UNITY_EDITOR_OSX
            // AVAssetWriter session creation through the macOS bundle returns
            // null when the P/Invoke originates from a Task.Run worker in the
            // Unity Editor. Keep Editor creation on Unity's main thread. The
            // iOS player still uses the background path below, preserving its
            // asynchronous segment rollover.
            completed?.Invoke(TryCreate(outputPath, width, height, framesPerSecond, bitrateKbps));
            yield break;
#else
            var task = Task.Run(() =>
            {
                var nativeSession = MacOsGpuVideoBridge.CreateSessionOnBackgroundThread(
                    outputPath, width, height, framesPerSecond, bitrateKbps);
                return nativeSession == IntPtr.Zero
                    ? null
                    : new MacOsGpuVideoSession(nativeSession, outputPath);
            });
            var startedAt = Time.realtimeSinceStartupAsDouble;
            while (!task.IsCompleted)
            {
                if (Time.realtimeSinceStartupAsDouble - startedAt > CreateTimeoutSeconds)
                {
                    Debug.LogWarning("[Macaca Beacon] GPU MP4 session creation timed out; skipping this segment.");
                    completed?.Invoke(null);
                    yield break;
                }
                yield return null;
            }

            if (task.IsFaulted)
            {
                Debug.LogException(task.Exception);
                completed?.Invoke(null);
                yield break;
            }

            completed?.Invoke(task.Result);
#endif
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

            var startedAt = Time.realtimeSinceStartupAsDouble;
            while (!MacOsGpuVideoBridge.IsFinishDone(nativeSession))
            {
                if (Time.realtimeSinceStartupAsDouble - startedAt > FinishTimeoutSeconds)
                {
                    Debug.LogWarning("[Macaca Beacon] GPU MP4 finalization timed out; continuing without this segment.");
                    // Do not dispose the native session here: the native worker
                    // may still be draining Metal/AVAssetWriter callbacks.
                    completed?.Invoke(false);
                    yield break;
                }
                yield return null;
            }

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
