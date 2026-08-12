using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace MacacaGames.RuntimeBugReporter
{
    internal sealed class WindowsGpuRollingVideoRecorder : IDisposable
    {
        private sealed class Segment
        {
            public string Path;
            public double Start;
            public double End;
        }

        private readonly MonoBehaviour host;
        private readonly BugReporterSettings settings;
        private Action<string, Action<VideoCaptureResult>> recoveryRequested;
        private readonly List<Segment> segments = new List<Segment>();
        private GpuFrameCapture capture;
        private Coroutine coroutine;
        private IntPtr session;
        private string sessionPath;
        private string directory;
        private double segmentStart;
        private double incidentTime;
        private double incidentEndTime;
        private int width;
        private int height;
        private bool requestedEnabled;
        private bool incidentPending;
        private Action<VideoCaptureResult> incidentCompleted;

        public bool IsFinalizing { get; private set; }
        public bool IsEncoding { get; private set; }
        public bool IsEnabled => requestedEnabled;

        public WindowsGpuRollingVideoRecorder(
            MonoBehaviour host,
            BugReporterSettings settings,
            Action<string, Action<VideoCaptureResult>> recoveryRequested)
        {
            this.host = host;
            this.settings = settings;
            this.recoveryRequested = recoveryRequested;
        }

        public void Start()
        {
            SetEnabled(settings.enableRollingVideo);
        }

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
            incidentTime = Time.realtimeSinceStartupAsDouble;
            incidentEndTime = incidentTime + Math.Max(0, settings.secondsAfter);
            incidentCompleted = completed;
            IsFinalizing = true;
            if (settings.secondsAfter <= 0)
                host.StartCoroutine(FinalizeIncident());
        }

        private IEnumerator CaptureLoop()
        {
            var waitForFrame = new WaitForEndOfFrame();
            var interval = 1d / Math.Max(1, settings.videoFramesPerSecond);
            var nextCapture = 0d;
            while (requestedEnabled)
            {
                yield return waitForFrame;
                var now = Time.realtimeSinceStartupAsDouble;
                if (now < nextCapture)
                    continue;
                nextCapture = now + interval;

                GpuFrameCapture.GpuFrame frame = default(GpuFrameCapture.GpuFrame);
                yield return capture.Capture(settings.videoWidth, value => frame = value);
                if (!frame.IsValid)
                {
                    Debug.LogWarning("[Macaca Beacon] Windows GPU capture returned an invalid RenderTexture.");
                    continue;
                }

                if (session == IntPtr.Zero)
                {
                    width = frame.Width;
                    height = frame.Height;
                    if (!BeginSegment(now, frame, out var createError))
                    {
                        RecoverWithGeneric("Session creation failed: " + createError);
                        yield break;
                    }
                }

                if (!WindowsGpuVideoBridge.Submit(session, frame, now - segmentStart))
                {
                    RecoverWithGeneric("Frame submission failed: " +
                                       (WindowsGpuVideoBridge.GetLastError(session) ?? "unknown native error"));
                    yield break;
                }

                if (now - segmentStart >= 2d)
                {
                    if (!FinishSegment(now, out var segmentError))
                    {
                        RecoverWithGeneric("Segment finalization failed: " + segmentError);
                        yield break;
                    }
                    PruneSegments(now - Math.Max(1, settings.secondsBefore) - 2d);
                }

                if (incidentPending && now >= incidentEndTime)
                    yield return FinalizeIncident();
            }
        }

        private bool BeginSegment(double start, GpuFrameCapture.GpuFrame frame, out string error)
        {
            if (string.IsNullOrEmpty(directory))
            {
                directory = Path.Combine(Application.temporaryCachePath, "MacacaBeacon", "WindowsGpuSegments-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
            }

            segmentStart = start;
            sessionPath = Path.Combine(directory, "segment-" + Guid.NewGuid().ToString("N") + ".mp4");
            return WindowsGpuVideoBridge.TryCreateSession(
                sessionPath,
                frame,
                settings.videoFramesPerSecond,
                settings.videoBitrateKbps,
                out session,
                out error);
        }

        private bool FinishSegment(double end, out string error)
        {
            error = null;
            if (session == IntPtr.Zero)
                return true;

            var currentSession = session;
            var currentPath = sessionPath;
            session = IntPtr.Zero;
            sessionPath = null;
            var finished = WindowsGpuVideoBridge.FinishSession(currentSession);
            error = WindowsGpuVideoBridge.GetLastError(currentSession);
            WindowsGpuVideoBridge.DestroySession(currentSession);
            if (finished && File.Exists(currentPath))
            {
                segments.Add(new Segment { Path = currentPath, Start = segmentStart, End = end });
                return true;
            }
            TryDelete(currentPath);
            error = error ?? "The native encoder returned no completed MP4 segment.";
            return false;
        }

        private IEnumerator FinalizeIncident()
        {
            if (!incidentPending)
                yield break;

            incidentPending = false;
            if (!FinishSegment(Time.realtimeSinceStartupAsDouble, out var finishError))
            {
                RecoverWithGeneric("Incident segment finalization failed: " + finishError);
                yield break;
            }
            IsEncoding = true;

            var selected = new List<string>();
            var startTime = incidentTime - Math.Max(0, settings.secondsBefore);
            for (var index = 0; index < segments.Count; index++)
            {
                var segment = segments[index];
                if (segment.End > startTime && segment.Start < incidentEndTime)
                    selected.Add(segment.Path);
            }

            VideoCaptureResult result = null;
            if (selected.Count > 0)
            {
                var outputDirectory = Path.Combine(Application.temporaryCachePath, "MacacaBeacon", "Captures");
                Directory.CreateDirectory(outputDirectory);
                var outputPath = Path.Combine(outputDirectory, "incident-" + Guid.NewGuid().ToString("N") + ".mp4");
                var mergeTask = Task.Run(() => WindowsGpuVideoBridge.ConcatSegments(outputPath, selected));
                while (!mergeTask.IsCompleted)
                    yield return null;

                if (mergeTask.IsCompletedSuccessfully && mergeTask.Result && File.Exists(outputPath))
                {
                    var duration = Math.Max(1d / Math.Max(1, settings.videoFramesPerSecond), incidentEndTime - startTime);
                    result = new VideoCaptureResult(outputPath, ".mp4", "video/mp4", duration,
                        Math.Max(1, Mathf.RoundToInt((float)(duration * settings.videoFramesPerSecond))),
                        "Windows D3D11 Media Foundation H.264");
                }
                else
                    TryDelete(outputPath);
            }

            if (result == null)
            {
                RecoverWithGeneric(selected.Count == 0
                    ? "No completed GPU video segment covered the incident window."
                    : "Could not merge the completed GPU video segments.");
                yield break;
            }

            IsFinalizing = false;
            IsEncoding = false;
            incidentCompleted?.Invoke(result);
            incidentCompleted = null;
            CleanupSegments();
            if (requestedEnabled)
            {
                segmentStart = 0d;
            }
            else
            {
                Stop();
            }
        }

        private void PruneSegments(double cutoff)
        {
            for (var index = segments.Count - 1; index >= 0; index--)
            {
                if (segments[index].End >= cutoff)
                    continue;
                TryDelete(segments[index].Path);
                segments.RemoveAt(index);
            }
        }

        private void CleanupSegments()
        {
            for (var index = 0; index < segments.Count; index++)
                TryDelete(segments[index].Path);
            segments.Clear();
            if (!string.IsNullOrEmpty(directory))
            {
                try
                {
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, true);
                }
                catch { }
            }
            directory = null;
        }

        private void Stop()
        {
            requestedEnabled = false;
            if (coroutine != null)
            {
                host.StopCoroutine(coroutine);
                coroutine = null;
            }
            FinishSegment(Time.realtimeSinceStartupAsDouble, out _);
            CleanupSegments();
            capture?.Dispose();
            capture = null;
        }

        private void RecoverWithGeneric(string error)
        {
            requestedEnabled = false;
            incidentPending = false;
            IsFinalizing = false;
            IsEncoding = false;
            if (session != IntPtr.Zero)
                FinishSegment(Time.realtimeSinceStartupAsDouble, out _);
            var pendingIncident = incidentCompleted;
            incidentCompleted = null;
            coroutine = null;
            CleanupSegments();
            capture?.Dispose();
            capture = null;
            var recover = recoveryRequested;
            recoveryRequested = null;
            if (recover != null)
                recover(error, pendingIncident);
            else
                pendingIncident?.Invoke(null);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
