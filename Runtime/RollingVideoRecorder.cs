using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MacacaGames.RuntimeBugReporter
{
    internal sealed class RollingVideoRecorder
    {
        private readonly MonoBehaviour host;
        private readonly BugReporterSettings settings;
        private readonly Queue<CapturedFrame> history = new Queue<CapturedFrame>();
        private List<CapturedFrame> incidentFrames;
        private Action<byte[]> incidentCompleted;
        private float captureStartedTime;
        private float incidentClipStartTime;
        private float incidentEndTime;
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
                captureStartedTime = Time.realtimeSinceStartup;
                host.StartCoroutine(CaptureLoop());
            }
        }

        public void MarkIncident(Action<byte[]> completed)
        {
            if (!settings.enableRollingVideo)
            {
                completed?.Invoke(null);
                return;
            }

            var incidentTime = Time.realtimeSinceStartup;
            incidentFrames = new List<CapturedFrame>(history);
            incidentCompleted = completed;
            incidentClipStartTime = Mathf.Max(captureStartedTime, incidentTime - settings.secondsBefore);
            incidentEndTime = incidentTime + settings.secondsAfter;
            IsFinalizing = settings.secondsAfter > 0;
            if (!IsFinalizing)
                FinishIncident();
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

                var capturedFrame = new CapturedFrame(frame, Time.realtimeSinceStartup);
                history.Enqueue(capturedFrame);
                var historyCutoff = capturedFrame.CapturedAt - Mathf.Max(1, settings.secondsBefore);
                while (history.Count > 0 && history.Peek().CapturedAt < historyCutoff)
                    history.Dequeue();

                if (incidentFrames == null)
                    continue;

                incidentFrames.Add(capturedFrame);
                if (capturedFrame.CapturedAt >= incidentEndTime)
                    FinishIncident();
            }
        }

        private void FinishIncident()
        {
            var frames = incidentFrames;
            var completed = incidentCompleted;
            incidentFrames = null;
            incidentCompleted = null;
            IsFinalizing = false;
            byte[] result = null;
            try
            {
                var jpegFrames = new List<byte[]>(frames.Count);
                foreach (var frame in frames)
                    jpegFrames.Add(frame.Data);
                var durationSeconds = Mathf.Max(1f / Mathf.Max(1, settings.videoFramesPerSecond), incidentEndTime - incidentClipStartTime);
                result = MjpegAviEncoder.Encode(jpegFrames, capturedWidth, capturedHeight, settings.videoFramesPerSecond, durationSeconds);
                Debug.Log("[Macaca Beacon] Video finalized: " + jpegFrames.Count + " frames over " + durationSeconds.ToString("0.00") + " seconds.");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
            completed?.Invoke(result);
        }

        private readonly struct CapturedFrame
        {
            public readonly byte[] Data;
            public readonly float CapturedAt;

            public CapturedFrame(byte[] data, float capturedAt)
            {
                Data = data;
                CapturedAt = capturedAt;
            }
        }
    }
}
