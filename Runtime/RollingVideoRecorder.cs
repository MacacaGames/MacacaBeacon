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
        // The Metal texture-submit path is still experimental. Keep it opt-in
        // until it has been validated against the Editor's native plugin
        // lifetime and graphics-device reset paths; the existing CPU/native
        // MP4 path is the crash-safe default.
        private const bool EnableExperimentalMacOsGpuPath = true;
        private const bool EnableExperimentalAndroidGpuPath = true;
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
        private Coroutine captureCoroutine;
        private bool requestedEnabled;
        private bool isFinalizing;
        private bool isEncoding;
        private string frameCacheDirectory;
        private long historyBytes;
        private readonly MacOsGpuRollingVideoRecorder gpuRecorder;
        private readonly AndroidGpuRollingVideoRecorder androidGpuRecorder;

        public bool IsFinalizing => gpuRecorder != null ? gpuRecorder.IsFinalizing : androidGpuRecorder != null ? androidGpuRecorder.IsFinalizing : isFinalizing;
        public bool IsEncoding => gpuRecorder != null ? gpuRecorder.IsEncoding : androidGpuRecorder != null ? androidGpuRecorder.IsEncoding : isEncoding;
        public bool IsEnabled => gpuRecorder != null ? gpuRecorder.IsEnabled : androidGpuRecorder != null ? androidGpuRecorder.IsEnabled : requestedEnabled;

        public RollingVideoRecorder(MonoBehaviour host, BugReporterSettings settings)
        {
            this.host = host;
            this.settings = settings;
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            if (EnableExperimentalMacOsGpuPath && MacOsGpuVideoBridge.IsAvailable)
                gpuRecorder = new MacOsGpuRollingVideoRecorder(host, settings);
#endif
#if UNITY_ANDROID && !UNITY_EDITOR
            if (EnableExperimentalAndroidGpuPath && AndroidGpuVideoBridge.IsAvailable)
                androidGpuRecorder = new AndroidGpuRollingVideoRecorder(host, settings);
#endif
        }

        public void Start()
        {
            if (gpuRecorder != null)
            {
                gpuRecorder.Start();
                return;
            }
            if (androidGpuRecorder != null)
            {
                androidGpuRecorder.Start();
                return;
            }
            SetEnabled(settings.enableRollingVideo);
        }

        public void SetEnabled(bool enabled)
        {
            if (gpuRecorder != null)
            {
                gpuRecorder.SetEnabled(enabled);
                return;
            }
            if (androidGpuRecorder != null)
            {
                androidGpuRecorder.SetEnabled(enabled);
                return;
            }
            requestedEnabled = enabled;
            if (enabled)
            {
                if (captureCoroutine == null)
                {
                    captureStartedTime = Time.realtimeSinceStartupAsDouble;
                    captureCoroutine = host.StartCoroutine(CaptureLoop());
                }
            }
            else if (!IsFinalizing)
            {
                StopCapture();
            }
        }

        public void MarkIncident(Action<VideoCaptureResult> completed)
        {
            if (gpuRecorder != null)
            {
                gpuRecorder.MarkIncident(completed);
                return;
            }
            if (androidGpuRecorder != null)
            {
                androidGpuRecorder.MarkIncident(completed);
                return;
            }
            if (!requestedEnabled)
            {
                completed?.Invoke(null);
                return;
            }

            var incidentTime = Time.realtimeSinceStartupAsDouble;
            incidentFrames = new List<VideoCaptureFrame>(history);
            incidentCompleted = completed;
            incidentClipStartTime = Math.Max(captureStartedTime, incidentTime - settings.secondsBefore);
            incidentEndTime = incidentTime + settings.secondsAfter;
            var availableSeconds = history.Count == 0
                ? 0d
                : Math.Max(0d, incidentTime - history.Peek().CapturedAt);
            Debug.Log("[Macaca Beacon] Incident video window: requested before " + settings.secondsBefore.ToString("0.0") +
                      "s, available before " + availableSeconds.ToString("0.0") + "s, frames " + history.Count +
                      ", cache " + settings.maximumVideoCacheMegabytes + " MB.");
            isFinalizing = true;
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
                // Finalization must not depend on successfully capturing one more frame.
                // WebGL can remain below the capture budget after the report UI opens;
                // the frame-skip guard below would otherwise leave incidentFrames alive
                // forever and keep the UI stuck on "Video recording".
                if (incidentFrames != null && Time.realtimeSinceStartupAsDouble >= incidentEndTime)
                {
                    Debug.Log("[Macaca Beacon] Incident after-window elapsed; starting video finalization with " + incidentFrames.Count + " frames.");
                    BeginFinishIncident();
                    continue;
                }
                // Once the incident clip has collected its after-window, stop
                // taking new screenshots while the encoder finalizes. This
                // avoids competing with gameplay for GPU readback and JPEG
                // compression time.
                if (isFinalizing && incidentFrames == null)
                    continue;
                if (Time.realtimeSinceStartup < nextCapture)
                    continue;
                nextCapture = Time.realtimeSinceStartup + interval;

                // JPEG encoding is intentionally kept on Unity's thread for
                // platform safety. Do not start another readback when the
                // previous game frame already missed the real-time budget;
                // otherwise the recorder can keep the game at 30 FPS.
                if (Application.targetFrameRate >= 50 && Time.unscaledDeltaTime > 0.025f)
                    continue;

                byte[] frame = null;
                var frameWidth = 0;
                var frameHeight = 0;
                var frameFormat = VideoCaptureFrameFormat.Rgba32;
                yield return CaptureUtility.CaptureScaledRgbaAsync(settings.videoWidth, (value, width, height) =>
                {
                    frame = value;
                    frameWidth = width;
                    frameHeight = height;
                });
                if (frame == null)
                {
                    frameFormat = VideoCaptureFrameFormat.Jpeg;
                    yield return CaptureUtility.CaptureScaledJpegAsync(settings.videoWidth, settings.videoJpegQuality, value => frame = value);
                    frameWidth = Mathf.Max(2, Mathf.Min(settings.videoWidth, Screen.width));
                    frameWidth -= frameWidth % 2;
                    frameHeight = Mathf.RoundToInt(Screen.height * (frameWidth / (float)Mathf.Max(1, Screen.width)));
                    frameHeight -= frameHeight % 2;
                }
                if (frame == null)
                    continue;

                capturedWidth = frameWidth;
                capturedHeight = frameHeight;

                VideoCaptureFrame capturedFrame;
                if (frameFormat == VideoCaptureFrameFormat.Rgba32)
                {
#if UNITY_WEBGL && !UNITY_EDITOR
                    // Emscripten's filesystem is backed by the same WebAssembly
                    // memory. Keeping the short rolling window in memory avoids a
                    // blocking write/read round-trip for every raw frame.
                    capturedFrame = new VideoCaptureFrame(
                        frame,
                        frameFormat,
                        frameWidth,
                        frameHeight,
                        Time.realtimeSinceStartupAsDouble);
#else
                    if (string.IsNullOrEmpty(frameCacheDirectory))
                    {
                        frameCacheDirectory = Path.Combine(Application.temporaryCachePath, "MacacaBeacon", "RollingFrames-" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(frameCacheDirectory);
                    }

                    var framePath = Path.Combine(frameCacheDirectory, Guid.NewGuid().ToString("N") + ".rgba");
                    var writeTask = Task.Run(() => File.WriteAllBytes(framePath, frame));
                    while (!writeTask.IsCompleted)
                        yield return null;
                    if (writeTask.IsFaulted)
                    {
                        Debug.LogWarning("[Macaca Beacon] Could not cache a rolling video frame: " + writeTask.Exception?.GetBaseException().Message);
                        continue;
                    }
                    capturedFrame = new VideoCaptureFrame(framePath, frameFormat, frameWidth, frameHeight, frame.Length, Time.realtimeSinceStartupAsDouble);
#endif
                }
                else
                {
                    capturedFrame = new VideoCaptureFrame(frame, Time.realtimeSinceStartupAsDouble);
                }

                history.Enqueue(capturedFrame);
                historyBytes += capturedFrame.ByteCount;
                var historyCutoff = capturedFrame.CapturedAt - Mathf.Max(1, settings.secondsBefore);
                var configuredCacheMegabytes = settings.maximumVideoCacheMegabytes > 0
                    ? settings.maximumVideoCacheMegabytes
                    : 512;
                var maximumCacheBytes = Math.Max(32L, configuredCacheMegabytes) * 1024L * 1024L;
                while (history.Count > 0 &&
                       (history.Peek().CapturedAt < historyCutoff || historyBytes > maximumCacheBytes))
                {
                    var expired = history.Dequeue();
                    historyBytes -= expired.ByteCount;
                    if (!isFinalizing)
                        expired.DeleteDataFile();
                }

                if (incidentFrames == null)
                    continue;

                incidentFrames.Add(capturedFrame);
                if (capturedFrame.CapturedAt >= incidentEndTime)
                    BeginFinishIncident();
            }
        }

        private void StopCapture()
        {
            if (captureCoroutine != null)
            {
                host.StopCoroutine(captureCoroutine);
                captureCoroutine = null;
            }
            history.Clear();
            historyBytes = 0;
            DeleteFrameCache();
        }

        private void BeginFinishIncident()
        {
            if (incidentFrames == null)
                return;
            var frames = incidentFrames;
            var completed = incidentCompleted;
            incidentFrames = null;
            incidentCompleted = null;
            isEncoding = true;
            host.StartCoroutine(FinishIncident(frames, completed));
        }

        private IEnumerator FinishIncident(List<VideoCaptureFrame> frames, Action<VideoCaptureResult> completed)
        {
            var durationSeconds = Math.Max(1d / Math.Max(1, settings.videoFramesPerSecond), incidentEndTime - incidentClipStartTime);
            var captureDirectory = Path.Combine(Application.temporaryCachePath, "MacacaBeacon", "Captures");
            Directory.CreateDirectory(captureDirectory);
            var outputStem = Path.Combine(captureDirectory, "incident-" + Guid.NewGuid().ToString("N"));
            string encoderError = null;
            VideoCaptureResult result = null;
#if UNITY_WEBGL && !UNITY_EDITOR
            if (settings.preferMp4)
            {
                yield return WebGlWebCodecsMp4Encoder.TryEncodeAsync(
                    outputStem + WebGlWebCodecsMp4Encoder.Extension,
                    frames,
                    capturedWidth,
                    capturedHeight,
                    settings.videoFramesPerSecond,
                    settings.videoBitrateKbps,
                    durationSeconds,
                    (value, error) =>
                    {
                        result = value;
                        encoderError = error;
                    });

                if (result == null && settings.allowLegacyAviFallback)
                {
                    result = VideoEncoderBackend.Encode(
                        outputStem,
                        frames,
                        capturedWidth,
                        capturedHeight,
                        settings.videoFramesPerSecond,
                        settings.videoBitrateKbps,
                        durationSeconds,
                        false,
                        true,
                        out encoderError);
                }
            }
            else
            {
                result = VideoEncoderBackend.Encode(
                    outputStem,
                    frames,
                    capturedWidth,
                    capturedHeight,
                    settings.videoFramesPerSecond,
                    settings.videoBitrateKbps,
                    durationSeconds,
                    settings.preferMp4,
                    settings.allowLegacyAviFallback,
                    out encoderError);
            }
#elif UNITY_ANDROID && !UNITY_EDITOR
            if (settings.preferMp4 && BugReporter.VideoEncoderOverride == null && AreRawFileFrames(frames))
            {
                var androidEncoder = new AndroidMediaCodecMp4Encoder();
                yield return androidEncoder.TryEncodeRawFilesAsync(
                    outputStem + androidEncoder.Extension,
                    frames,
                    capturedWidth,
                    capturedHeight,
                    settings.videoFramesPerSecond,
                    settings.videoBitrateKbps,
                    durationSeconds,
                    (value, error) =>
                    {
                        result = value;
                        encoderError = error;
                    });
            }
            else
            {
                // Compatibility/custom encoders keep the synchronous contract.
                // The normal RGBA Android path above runs wholly in a Java worker.
                result = VideoEncoderBackend.Encode(
                    outputStem,
                    frames,
                    capturedWidth,
                    capturedHeight,
                    settings.videoFramesPerSecond,
                    settings.videoBitrateKbps,
                    durationSeconds,
                    settings.preferMp4,
                    settings.allowLegacyAviFallback,
                    out encoderError);
            }
#else
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

            if (task.IsFaulted)
            {
                Debug.LogException(task.Exception);
            }
            else
            {
                result = task.Result;
            }
#endif

            // Keep this method an iterator on Android too, where encoding is
            // intentionally performed synchronously on Unity's main thread
            // to keep Android JNI calls safe.
            yield return null;

            isFinalizing = false;
            isEncoding = false;
            if (result == null)
            {
                Debug.LogWarning("[Macaca Beacon] Video finalization failed: " + (encoderError ?? "unknown encoder error"));
            }
            else
            {
                Debug.Log("[Macaca Beacon] Video finalized by " + result.EncoderName + ": " + result.FrameCount + " frames over " + result.DurationSeconds.ToString("0.00") + " seconds at " + result.FilePath);
            }
            completed?.Invoke(result);
            DeleteFrameCache();
            history.Clear();
            historyBytes = 0;
            captureStartedTime = Time.realtimeSinceStartupAsDouble;
            if (!requestedEnabled)
                StopCapture();
        }

        private void DeleteFrameCache()
        {
            if (string.IsNullOrEmpty(frameCacheDirectory))
                return;
            try
            {
                if (Directory.Exists(frameCacheDirectory))
                    Directory.Delete(frameCacheDirectory, true);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[Macaca Beacon] Could not clean rolling frame cache: " + exception.Message);
            }
            frameCacheDirectory = null;
        }

        private static bool AreRawFileFrames(IReadOnlyList<VideoCaptureFrame> frames)
        {
            if (frames == null || frames.Count == 0)
                return false;
            for (var index = 0; index < frames.Count; index++)
            {
                if (frames[index].Format != VideoCaptureFrameFormat.Rgba32 ||
                    string.IsNullOrEmpty(frames[index].DataFilePath))
                    return false;
            }
            return true;
        }

        public void Dispose()
        {
            gpuRecorder?.Dispose();
            if (gpuRecorder == null)
                StopCapture();
        }

    }
}
