using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace MacacaGames.RuntimeBugReporter
{
    internal sealed class MacOsGpuRollingVideoRecorder : IDisposable
    {
        private sealed class Segment
        {
            public string Path;
            public double Start;
            public double End;
        }

        private sealed class PendingSegment
        {
            public MacOsGpuVideoSession Session;
            public string Path;
            public double Start;
            public double End;
            public bool CleanupWhenDone;
        }

        private readonly MonoBehaviour host;
        private readonly BugReporterSettings settings;
        private readonly List<Segment> segments = new List<Segment>();
        private readonly List<PendingSegment> pendingSegments = new List<PendingSegment>();
        private GpuFrameCapture capture;
        private MacOsGpuVideoSession session;
        private Coroutine coroutine;
        private bool requestedEnabled;
        private bool incidentPending;
        private double incidentTime;
        private double incidentEndTime;
        private Action<VideoCaptureResult> incidentCompleted;
        private int width;
        private int height;
        private double segmentStart;
        private string directory;

        public bool IsFinalizing { get; private set; }
        public bool IsEncoding { get; private set; }
        public bool IsEnabled => requestedEnabled;

        public MacOsGpuRollingVideoRecorder(MonoBehaviour host, BugReporterSettings settings)
        {
            this.host = host;
            this.settings = settings;
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
            incidentEndTime = incidentTime + settings.secondsAfter;
            incidentCompleted = completed;
            IsFinalizing = true;
            host.StartCoroutine(FinalizeAfterWindow());
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
                // Do not require a valid frame or an active segment to decide
                // that the post-incident window is complete. Segment creation
                // is asynchronous and must never be able to hold the report UI
                // in Recording forever.
                if (incidentPending && Time.realtimeSinceStartupAsDouble >= incidentEndTime)
                {
                    yield return FinalizeIncident();
                    continue;
                }
                if (!frame.IsValid)
                {
                    Debug.LogWarning("[Macaca Beacon] GPU capture returned an invalid RenderTexture.");
                    continue;
                }

                if (session == null)
                {
                    width = frame.Width;
                    height = frame.Height;
                    yield return BeginSegmentAsync(now);
                    if (session == null)
                        continue;
                }

                if (!session.Submit(frame, now - segmentStart))
                {
                    Debug.LogWarning("[Macaca Beacon] GPU video frame submission failed.");
                    yield return FinalizeIncident();
                    yield break;
                }

                if (now - segmentStart >= 2.0)
                {
                    FinishSegment(now);
                    PruneSegments(now - Math.Max(1, settings.secondsBefore) - 2.0);
                }

            }
        }

        private IEnumerator BeginSegmentAsync(double start)
        {
            if (string.IsNullOrEmpty(directory))
            {
                directory = Path.Combine(Application.temporaryCachePath, "MacacaBeacon", "GpuSegments-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
            }

            segmentStart = start;
            var path = Path.Combine(directory, "segment-" + Guid.NewGuid().ToString("N") + ".mp4");
            var created = default(MacOsGpuVideoSession);
            yield return MacOsGpuVideoSession.TryCreateAsync(
                path,
                width,
                height,
                settings.videoFramesPerSecond,
                settings.videoBitrateKbps,
                value => created = value);
            // Keep a session that was already being created when the incident
            // was marked; it contains the after-window frames we still need.
            // Only discard a late result after FinalizeIncident has claimed the
            // incident and detached the active recording state.
            if (IsFinalizing && !incidentPending)
            {
                created?.Dispose();
                created = null;
            }
            session = created;
            if (session == null)
            {
                TryDelete(path);
                Debug.LogWarning("[Macaca Beacon] Could not create the Apple GPU video session.");
            }
        }

        private void FinishSegment(double end)
        {
            if (session == null)
                return;
            var pending = new PendingSegment
            {
                Session = session,
                Path = session.OutputPath,
                Start = segmentStart,
                End = end
            };
            session = null;
            pendingSegments.Add(pending);
            host.StartCoroutine(FinalizeSegmentAsync(pending));
        }

        private IEnumerator FinalizeSegmentAsync(PendingSegment pending)
        {
            var finished = false;
            yield return pending.Session.FinishAsync(value => finished = value);
            pending.Session.Dispose();
            pendingSegments.Remove(pending);

            if (finished && File.Exists(pending.Path))
                segments.Add(new Segment { Path = pending.Path, Start = pending.Start, End = pending.End });
            else
                TryDelete(pending.Path);

            if (pending.CleanupWhenDone && pendingSegments.Count == 0)
                CleanupSegments();
        }

        private IEnumerator FinalizeIncident()
        {
            if (!incidentPending)
                yield break;
            incidentPending = false;
            FinishSegment(Time.realtimeSinceStartupAsDouble);
            IsEncoding = true;

            while (pendingSegments.Count > 0)
                yield return null;

            var selected = new List<string>();
            var startTime = incidentTime - Math.Max(0, settings.secondsBefore);
            var endTime = incidentEndTime;
            for (var index = 0; index < segments.Count; index++)
            {
                var segment = segments[index];
                if (segment.End > startTime && segment.Start < endTime)
                    selected.Add(segment.Path);
            }

            VideoCaptureResult result = null;
            if (selected.Count > 0)
            {
                var outputDirectory = Path.Combine(Application.temporaryCachePath, "MacacaBeacon", "Captures");
                Directory.CreateDirectory(outputDirectory);
                var outputPath = Path.Combine(outputDirectory, "incident-" + Guid.NewGuid().ToString("N") + ".mp4");
                var mergeTask = Task.Run(() => MacOsGpuVideoBridge.ConcatSegments(outputPath, selected));
                while (!mergeTask.IsCompleted)
                    yield return null;
                if (mergeTask.IsCompletedSuccessfully && mergeTask.Result && File.Exists(outputPath))
                {
                    var duration = Math.Max(1d / Math.Max(1, settings.videoFramesPerSecond), endTime - startTime);
                    result = new VideoCaptureResult(outputPath, ".mp4", "video/mp4", duration,
                        Math.Max(1, Mathf.RoundToInt((float)(duration * settings.videoFramesPerSecond))),
                        "Apple Metal H.264", width, height);
                }
                else
                {
                    TryDelete(outputPath);
                }
            }

            IsFinalizing = false;
            IsEncoding = false;
            incidentCompleted?.Invoke(result);
            incidentCompleted = null;
            CleanupSegments();
            if (!requestedEnabled)
                Stop();
        }

        private IEnumerator FinalizeAfterWindow()
        {
            while (incidentPending && Time.realtimeSinceStartupAsDouble < incidentEndTime)
                yield return null;
            if (incidentPending)
                yield return FinalizeIncident();
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
                try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
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
            for (var index = 0; index < pendingSegments.Count; index++)
                pendingSegments[index].CleanupWhenDone = true;
            if (session != null)
            {
                var pending = new PendingSegment
                {
                    Session = session,
                    Path = session.OutputPath,
                    Start = segmentStart,
                    End = Time.realtimeSinceStartupAsDouble,
                    CleanupWhenDone = true
                };
                session = null;
                pendingSegments.Add(pending);
                host.StartCoroutine(FinalizeSegmentAsync(pending));
            }
            else if (pendingSegments.Count == 0)
            {
                CleanupSegments();
            }
            capture?.Dispose();
            capture = null;
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch { }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
