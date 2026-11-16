using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace MacacaGames.RuntimeBugReporter
{
    /// <summary>Continuous GPU encoder with bounded compressed history and asynchronous incident export.</summary>
    internal sealed class AndroidGpuRollingVideoRecorder : IDisposable
    {
        private const double FinalizationTimeoutSeconds = 10d;
        private readonly MonoBehaviour host;
        private readonly BugReporterSettings settings;
        private readonly Action<string> failed;
        private GpuFrameCapture capture;
        private Coroutine coroutine;
        private Coroutine incidentCoroutine;
        private long session;
        private double startedAt;
        private bool requestedEnabled;
        private bool incidentPending;
        private bool clipStarted;
        private double incidentTime;
        private double incidentEnd;
        private string outputPath;
        private Action<VideoCaptureResult> incidentCompleted;
        private int width;
        private int height;

        public bool IsFinalizing { get; private set; }
        public bool IsEncoding { get; private set; }
        public bool IsEnabled => requestedEnabled;

        public AndroidGpuRollingVideoRecorder(MonoBehaviour host, BugReporterSettings settings, Action<string> failed)
        {
            this.host = host;
            this.settings = settings;
            this.failed = failed;
        }

        public void SetEnabled(bool enabled)
        {
            requestedEnabled = enabled;
            if (enabled && coroutine == null)
            {
                capture = new GpuFrameCapture();
                coroutine = host.StartCoroutine(CaptureLoop());
            }
            else if (!enabled)
                Stop();
        }

        public void MarkIncident(Action<VideoCaptureResult> completed)
        {
            if (!requestedEnabled || incidentPending)
            {
                completed?.Invoke(null);
                return;
            }
            incidentTime = Time.realtimeSinceStartupAsDouble;
            incidentEnd = incidentTime + Math.Max(0, settings.secondsAfter);
            incidentPending = true;
            clipStarted = false;
            incidentCompleted = completed;
            IsFinalizing = true;
            // Freeze history in the same call as the incident. Waiting until
            // the next frame could evict the GOP containing its start boundary.
            var error = BeginPendingClip();
            if (error != null)
            {
                Fail(error);
                return;
            }
            incidentCoroutine = host.StartCoroutine(MonitorIncident());
        }

        private IEnumerator CaptureLoop()
        {
            var wait = new WaitForEndOfFrame();
            var interval = 1d / Math.Max(1, settings.videoFramesPerSecond);
            var next = 0d;
            var nextHealthCheck = 0d;
            while (requestedEnabled)
            {
                yield return wait;
                var now = Time.realtimeSinceStartupAsDouble;
                if (now < next)
                    continue;
                next = now + interval;
                GpuFrameCapture.GpuFrame frame = default;
                yield return capture.Capture(settings.videoWidth, value => frame = value);
                if (!frame.IsValid)
                    continue;

                // JNI/native failures must reach the report callback instead of
                // terminating this iterator and leaving Send disabled forever.
                var error = SubmitFrame(frame, now, now >= nextHealthCheck);
                if (!string.IsNullOrEmpty(error))
                {
                    coroutine = null;
                    Fail(error);
                    yield break;
                }
                if (now >= nextHealthCheck)
                    nextHealthCheck = now + 0.5d;
            }
        }

        private string SubmitFrame(GpuFrameCapture.GpuFrame frame, double now, bool checkHealth)
        {
            try
            {
                if (session == 0)
                {
                    width = frame.Width;
                    height = frame.Height;
                    session = AndroidGpuVideoBridge.CreateSession(width, height, settings.videoFramesPerSecond,
                        settings.videoBitrateKbps, settings.secondsBefore, settings.maximumVideoCacheMegabytes);
                    if (session == 0)
                        return "GPU video initialization failed: " + AndroidGpuVideoBridge.LastCreateError();
                    startedAt = now;
                    Debug.Log("[Macaca Beacon] Android compressed rolling video started; renderer=" +
                              SystemInfo.graphicsDeviceType + ", before=" + settings.secondsBefore +
                              "s, cache limit=" + settings.maximumVideoCacheMegabytes + " MB.");
                }
                if (!AndroidGpuVideoBridge.Submit(session, frame, now - startedAt))
                    return "GPU video frame submission failed.";
                return checkHealth ? AndroidGpuVideoBridge.LastError(session) : null;
            }
            catch (Exception exception)
            {
                return exception.Message;
            }
        }

        private string BeginPendingClip()
        {
            if (session == 0 || clipStarted)
                return null;
            try
            {
                var directory = Path.Combine(Application.temporaryCachePath, "MacacaBeacon", "Captures");
                Directory.CreateDirectory(directory);
                outputPath = Path.Combine(directory, "incident-" + Guid.NewGuid().ToString("N") + ".mp4");
                clipStarted = AndroidGpuVideoBridge.BeginClip(session, outputPath,
                    incidentTime - startedAt - Math.Max(0, settings.secondsBefore), incidentEnd - startedAt);
                return clipStarted ? null : "Could not begin incident export: " + AndroidGpuVideoBridge.LastError(session);
            }
            catch (Exception exception)
            {
                return exception.Message;
            }
        }

        // Independent of WaitForEndOfFrame/valid frames so backgrounding or
        // capture failures cannot leave an incident waiting without a deadline.
        private IEnumerator MonitorIncident()
        {
            // StartCoroutine executes through its first yield synchronously.
            // Delay polling so all state, including incidentCoroutine, is installed.
            yield return null;
            while (incidentPending)
            {
                if (Time.realtimeSinceStartupAsDouble >= incidentEnd + FinalizationTimeoutSeconds)
                {
                    incidentCoroutine = null;
                    Fail("Incident collection/export exceeded the 10 second finalization deadline.");
                    yield break;
                }
                string error = null;
                VideoCaptureResult result = null;
                try
                {
                    if (session != 0)
                    {
                        error = BeginPendingClip();
                        if (clipStarted)
                        {
                            var state = AndroidGpuVideoBridge.ClipState(session);
                            IsEncoding = state == 1;
                            if (state < 0)
                                error = "Incident export failed: " + AndroidGpuVideoBridge.LastError(session);
                            else if (state == 2)
                            {
                                var info = AndroidGpuVideoBridge.ClipInfo(session);
                                if (info.Length != 6 || info[1] <= info[0] || info[2] < 1 || !File.Exists(outputPath))
                                    error = "Incident export returned invalid video metadata.";
                                else
                                {
                                    result = new VideoCaptureResult(outputPath, ".mp4", "video/mp4", info[1] - info[0],
                                        (int)info[2], "Android MediaCodec GPU rolling " + SystemInfo.graphicsDeviceType,
                                        (int)info[4], (int)info[5]);
                                    Debug.Log("[Macaca Beacon] Android incident requested=" +
                                              (incidentTime - startedAt - settings.secondsBefore).ToString("F3") + ".." +
                                              (incidentEnd - startedAt).ToString("F3") + "s, actual=" +
                                              info[0].ToString("F3") + ".." + info[1].ToString("F3") + "s, frames=" +
                                              info[2] + ", compressed cache=" + info[3] + " bytes.");
                                    AndroidGpuVideoBridge.ReleaseClip(session, false);
                                }
                            }
                        }
                    }
                }
                catch (Exception exception)
                {
                    error = exception.Message;
                }
                if (error != null)
                {
                    result?.DeleteFile();
                    incidentCoroutine = null;
                    Fail(error);
                    yield break;
                }
                if (result != null)
                {
                    incidentCoroutine = null;
                    outputPath = null;
                    Complete(result);
                    yield break;
                }
                yield return null;
            }
        }

        private void Complete(VideoCaptureResult result)
        {
            var callback = incidentCompleted;
            incidentCompleted = null;
            incidentPending = false;
            clipStarted = false;
            IsFinalizing = false;
            IsEncoding = false;
            callback?.Invoke(result);
        }

        private void Fail(string error)
        {
            Debug.LogWarning("[Macaca Beacon] Android GPU video: " + error);
            Stop();
            failed?.Invoke(error);
        }

        private void Stop()
        {
            requestedEnabled = false;
            if (coroutine != null) host.StopCoroutine(coroutine);
            coroutine = null;
            if (incidentCoroutine != null) host.StopCoroutine(incidentCoroutine);
            incidentCoroutine = null;
            var oldSession = session;
            session = 0;
            var oldCapture = capture;
            capture = null;
            try
            {
                if (oldSession != 0)
                    AndroidGpuVideoBridge.DestroySession(oldSession, () => oldCapture?.Dispose());
                else
                    oldCapture?.Dispose();
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[Macaca Beacon] Android video cleanup: " + exception.Message);
            }
            // Java owns pending-file deletion while its exporter is in flight.
            outputPath = null;
            Complete(null);
        }

        public void Dispose() => Stop();
    }
}
