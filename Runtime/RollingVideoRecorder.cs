using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace MacacaGames.RuntimeBugReporter
{
    internal sealed class RollingVideoRecorder
    {
        private readonly MonoBehaviour host;
        private readonly BugReporterSettings settings;
        private readonly Queue<VideoCaptureFrame> history = new Queue<VideoCaptureFrame>();
        private List<VideoCaptureFrame> incidentFrames;
        private Action<VideoCaptureResult> incidentCompleted;
        private double captureStartedTime;
        private double incidentClipStartTime;
        private double incidentEndTime;
        private int capturedWidth;
        private int capturedHeight;

        public bool IsFinalizing { get; private set; }

        public RollingVideoRecorder(MonoBehaviour host, BugReporterSettings settings)
        {
            this.host = host;
            this.settings = settings;
        }

        public void Start()
        {
            if (settings.enableRollingVideo)
            {
                captureStartedTime = Time.realtimeSinceStartupAsDouble;
                host.StartCoroutine(CaptureLoop());
            }
        }

        public void MarkIncident(Action<VideoCaptureResult> completed)
        {
            if (!settings.enableRollingVideo)
            {
                completed?.Invoke(null);
                return;
            }

            var incidentTime = Time.realtimeSinceStartupAsDouble;
            incidentFrames = new List<VideoCaptureFrame>(history);
            incidentCompleted = completed;
            incidentClipStartTime = Math.Max(captureStartedTime, incidentTime - settings.secondsBefore);
            incidentEndTime = incidentTime + settings.secondsAfter;
            IsFinalizing = true;
            if (settings.secondsAfter <= 0)
                BeginFinishIncident();
        }

        private IEnumerator CaptureLoop()
        {
            var waitForFrame = new WaitForEndOfFrame();
            var interval = 1f / Mathf.Max(1, settings.videoFramesPerSecond);
            var nextCapture = 0f;
            while (true)
            {
                yield return waitForFrame;
                if (Time.realtimeSinceStartup < nextCapture)
                    continue;
                nextCapture = Time.realtimeSinceStartup + interval;

                var frame = CaptureUtility.CaptureScaledJpeg(settings.videoWidth, settings.videoJpegQuality);
                if (frame == null)
                    continue;

                capturedWidth = Mathf.Max(2, Mathf.Min(settings.videoWidth, Screen.width));
                capturedWidth -= capturedWidth % 2;
                capturedHeight = Mathf.RoundToInt(Screen.height * (capturedWidth / (float)Mathf.Max(1, Screen.width)));
                capturedHeight -= capturedHeight % 2;

                var capturedFrame = new VideoCaptureFrame(frame, Time.realtimeSinceStartupAsDouble);
                history.Enqueue(capturedFrame);
                var historyCutoff = capturedFrame.CapturedAt - Mathf.Max(1, settings.secondsBefore);
                while (history.Count > 0 && history.Peek().CapturedAt < historyCutoff)
                    history.Dequeue();

                if (incidentFrames == null)
                    continue;

                incidentFrames.Add(capturedFrame);
                if (capturedFrame.CapturedAt >= incidentEndTime)
                    BeginFinishIncident();
            }
        }

        private void BeginFinishIncident()
        {
            if (incidentFrames == null)
                return;
            var frames = incidentFrames;
            var completed = incidentCompleted;
            incidentFrames = null;
            incidentCompleted = null;
            host.StartCoroutine(FinishIncident(frames, completed));
        }

        private IEnumerator FinishIncident(List<VideoCaptureFrame> frames, Action<VideoCaptureResult> completed)
        {
            var durationSeconds = Math.Max(1d / Math.Max(1, settings.videoFramesPerSecond), incidentEndTime - incidentClipStartTime);
            var captureDirectory = Path.Combine(Application.temporaryCachePath, "MacacaBeacon", "Captures");
            Directory.CreateDirectory(captureDirectory);
            var outputStem = Path.Combine(captureDirectory, "incident-" + Guid.NewGuid().ToString("N"));
            string encoderError = null;
            var task = Task.Run(() => VideoEncoderBackend.Encode(
                outputStem,
                frames,
                capturedWidth,
                capturedHeight,
                settings.videoFramesPerSecond,
                settings.videoBitrateKbps,
                durationSeconds,
                settings.preferMp4,
                settings.allowLegacyAviFallback,
                out encoderError));

            while (!task.IsCompleted)
                yield return null;

            VideoCaptureResult result = null;
            if (task.IsFaulted)
            {
                Debug.LogException(task.Exception);
            }
            else
            {
                result = task.Result;
            }

            IsFinalizing = false;
            if (result == null)
            {
                Debug.LogWarning("[Macaca Beacon] Video finalization failed: " + (encoderError ?? "unknown encoder error"));
            }
            else
            {
                Debug.Log("[Macaca Beacon] Video finalized by " + result.EncoderName + ": " + result.FrameCount + " frames over " + result.DurationSeconds.ToString("0.00") + " seconds at " + result.FilePath);
            }
            completed?.Invoke(result);
        }

    }
}
