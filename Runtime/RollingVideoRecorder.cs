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
        private readonly Queue<byte[]> history = new Queue<byte[]>();
        private List<byte[]> incidentFrames;
        private Action<byte[]> incidentCompleted;
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
                host.StartCoroutine(CaptureLoop());
        }

        public void MarkIncident(Action<byte[]> completed)
        {
            if (!settings.enableRollingVideo)
            {
                completed?.Invoke(null);
                return;
            }

            incidentFrames = new List<byte[]>(history);
            incidentCompleted = completed;
            incidentEndTime = Time.realtimeSinceStartup + settings.secondsAfter;
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

                history.Enqueue(frame);
                var historyCapacity = Mathf.Max(1, settings.secondsBefore * settings.videoFramesPerSecond);
                while (history.Count > historyCapacity)
                    history.Dequeue();

                if (incidentFrames == null)
                    continue;

                incidentFrames.Add(frame);
                if (Time.realtimeSinceStartup >= incidentEndTime)
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
                result = MjpegAviEncoder.Encode(frames, capturedWidth, capturedHeight, settings.videoFramesPerSecond);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
            completed?.Invoke(result);
        }
    }
}
