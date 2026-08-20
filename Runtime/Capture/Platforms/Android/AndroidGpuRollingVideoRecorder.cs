using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace MacacaGames.RuntimeBugReporter
{
    /// <summary>
    /// Android Surface/MediaCodec recorder. The encoder stays active while the
    /// game runs, so frames never make a CPU readback or JPEG round-trip.
    /// </summary>
    internal sealed class AndroidGpuRollingVideoRecorder : IDisposable
    {
        private readonly MonoBehaviour host;
        private readonly BugReporterSettings settings;
        private GpuFrameCapture capture;
        private Coroutine coroutine;
        private long session;
        private string outputPath;
        private double startedAt;
        private bool requestedEnabled;
        private bool incidentPending;
        private double incidentEnd;
        private Action<VideoCaptureResult> incidentCompleted;
        private int frameCount;
        private int width;
        private int height;

        public bool IsFinalizing { get; private set; }
        public bool IsEncoding => IsFinalizing;
        public bool IsEnabled => requestedEnabled;

        public AndroidGpuRollingVideoRecorder(MonoBehaviour host, BugReporterSettings settings)
        {
            this.host = host;
            this.settings = settings;
        }

        public void Start() => SetEnabled(settings.enableRollingVideo);

        public void SetEnabled(bool enabled)
        {
            requestedEnabled = enabled;
            if (enabled && coroutine == null)
            {
                capture = new GpuFrameCapture();
                coroutine = host.StartCoroutine(CaptureLoop());
            }
            else if (!enabled && !IsFinalizing)
            {
                Stop();
            }
        }

        public void MarkIncident(Action<VideoCaptureResult> completed)
        {
            if (!requestedEnabled)
            {
                completed?.Invoke(null);
                return;
            }
            incidentPending = true;
            incidentEnd = Time.realtimeSinceStartupAsDouble + Math.Max(0, settings.secondsAfter);
            incidentCompleted = completed;
            IsFinalizing = true;
            if (settings.secondsAfter <= 0)
                host.StartCoroutine(FinalizeIncident());
        }

        private IEnumerator CaptureLoop()
        {
            var wait = new WaitForEndOfFrame();
            var interval = 1d / Math.Max(1, settings.videoFramesPerSecond);
            var next = 0d;
            while (requestedEnabled)
            {
                yield return wait;
                var now = Time.realtimeSinceStartupAsDouble;
                if (now < next)
                    continue;
                next = now + interval;

                GpuFrameCapture.GpuFrame frame = default(GpuFrameCapture.GpuFrame);
                yield return capture.Capture(settings.videoWidth, value => frame = value);
                if (!frame.IsValid)
                    continue;

                if (session == 0)
                {
                    width = frame.Width;
                    height = frame.Height;
                    var directory = Path.Combine(Application.temporaryCachePath, "MacacaBeacon", "AndroidGpu");
                    Directory.CreateDirectory(directory);
                    outputPath = Path.Combine(directory, "incident-" + Guid.NewGuid().ToString("N") + ".mp4");
                    session = AndroidGpuVideoBridge.CreateSession(outputPath, frame.Width, frame.Height,
                        settings.videoFramesPerSecond, settings.videoBitrateKbps);
                    if (session == 0)
                    {
                        Debug.LogWarning("[Macaca Beacon] GPU video session creation failed, using CPU fallback");
                        requestedEnabled = false;
                        yield break;
                    }
                    startedAt = now;
                    frameCount = 0;
                    Debug.Log("[Macaca Beacon] Android GPU video session started");
                }

                if (!AndroidGpuVideoBridge.Submit(session, frame, now - startedAt))
                {
                    Debug.LogWarning("[Macaca Beacon] GPU frame submission failed");
                    yield return FinalizeIncident();
                    yield break;
                }
                frameCount++;
                if (incidentPending && now >= incidentEnd)
                    yield return FinalizeIncident();
            }
        }

        private IEnumerator FinalizeIncident()
        {
            if (!incidentPending && session == 0)
                yield break;
            incidentPending = false;
            var currentSession = session;
            var currentPath = outputPath;
            session = 0;
            var finished = currentSession != 0 && AndroidGpuVideoBridge.FinishSession(currentSession);
            var error = currentSession != 0 ? AndroidGpuVideoBridge.LastError(currentSession) : null;
            AndroidGpuVideoBridge.DestroySession(currentSession);
            if (!finished && !string.IsNullOrEmpty(error))
                Debug.LogWarning("[Macaca Beacon] Android GPU video finalization failed: " + error);

            var result = finished && File.Exists(currentPath)
                ? new VideoCaptureResult(currentPath, ".mp4", "video/mp4",
                    Math.Max(1d / Math.Max(1, settings.videoFramesPerSecond), Time.realtimeSinceStartupAsDouble - startedAt),
                    frameCount, "Android MediaCodec GPU " + SystemInfo.graphicsDeviceType, width, height)
                : null;
            IsFinalizing = false;
            incidentCompleted?.Invoke(result);
            incidentCompleted = null;
            if (!requestedEnabled)
                Stop();
        }

        private void Stop()
        {
            requestedEnabled = false;
            if (coroutine != null)
            {
                host.StopCoroutine(coroutine);
                coroutine = null;
            }
            if (session != 0)
            {
                AndroidGpuVideoBridge.FinishSession(session);
                AndroidGpuVideoBridge.DestroySession(session);
                session = 0;
            }
            capture?.Dispose();
            capture = null;
        }

        public void Dispose() => Stop();
    }
}
