using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace MacacaGames.RuntimeBugReporter
{
    internal enum WindowsVideoBackendMode
    {
        Auto,
        WindowsGpu,
        WindowsCpu,
        ManagedAvi
    }

    internal sealed class RollingVideoRecorder
    {
        private const string VideoBackendArgument = "-macaca-beacon-video-backend";
        private const int DiagnosticCapacity = 32;
        // The Metal texture-submit path is still experimental. Keep it opt-in
        // until it has been validated against the Editor's native plugin
        // lifetime and graphics-device reset paths; the existing CPU/native
        // MP4 path is the crash-safe default.
        private const bool EnableExperimentalMacOsGpuPath = true;
        private const bool EnableExperimentalAndroidGpuPath = true;
        private const bool EnableExperimentalWindowsGpuPath = true;
        private readonly MonoBehaviour host;
        private readonly BugReporterSettings settings;
        private readonly WindowsVideoBackendMode windowsVideoBackendMode;
        private readonly Queue<VideoCaptureFrame> history = new Queue<VideoCaptureFrame>();
        private readonly Queue<string> diagnosticEntries = new Queue<string>();
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
        private bool backendEnvironmentLogged;
        private VideoCaptureResult lastDiagnosticCapture;
        private readonly MacOsGpuRollingVideoRecorder gpuRecorder;
        private readonly AndroidGpuRollingVideoRecorder androidGpuRecorder;
        private WindowsGpuRollingVideoRecorder windowsGpuRecorder;

        public bool IsFinalizing => gpuRecorder != null ? gpuRecorder.IsFinalizing : androidGpuRecorder != null ? androidGpuRecorder.IsFinalizing : windowsGpuRecorder != null ? windowsGpuRecorder.IsFinalizing : isFinalizing;
        public bool IsEncoding => gpuRecorder != null ? gpuRecorder.IsEncoding : androidGpuRecorder != null ? androidGpuRecorder.IsEncoding : windowsGpuRecorder != null ? windowsGpuRecorder.IsEncoding : isEncoding;
        public bool IsEnabled => gpuRecorder != null ? gpuRecorder.IsEnabled : androidGpuRecorder != null ? androidGpuRecorder.IsEnabled : windowsGpuRecorder != null ? windowsGpuRecorder.IsEnabled : requestedEnabled;

        public RollingVideoRecorder(MonoBehaviour host, BugReporterSettings settings)
        {
            this.host = host;
            this.settings = settings;
            var requestedBackend = ParseWindowsVideoBackend(Environment.GetCommandLineArgs(), out var invalidBackend);
            if (!string.IsNullOrEmpty(invalidBackend))
                RecordDiagnostic("Unknown video backend '" + invalidBackend + "'; using auto.", LogType.Warning);
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            windowsVideoBackendMode = requestedBackend;
#else
            windowsVideoBackendMode = WindowsVideoBackendMode.Auto;
#endif
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_IOS
            if (EnableExperimentalMacOsGpuPath && MacOsGpuVideoBridge.IsAvailable)
                gpuRecorder = new MacOsGpuRollingVideoRecorder(host, settings);
#endif
#if UNITY_ANDROID && !UNITY_EDITOR
            if (EnableExperimentalAndroidGpuPath && AndroidGpuVideoBridge.IsAvailable)
                androidGpuRecorder = new AndroidGpuRollingVideoRecorder(host, settings);
#endif
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (EnableExperimentalWindowsGpuPath &&
                windowsVideoBackendMode != WindowsVideoBackendMode.WindowsCpu &&
                windowsVideoBackendMode != WindowsVideoBackendMode.ManagedAvi &&
                (windowsVideoBackendMode == WindowsVideoBackendMode.WindowsGpu || WindowsGpuVideoBridge.IsAvailable))
                windowsGpuRecorder = new WindowsGpuRollingVideoRecorder(host, settings, RecoverFromWindowsGpuFailure);
#endif
        }

        internal static WindowsVideoBackendMode ParseWindowsVideoBackend(string[] arguments, out string invalidValue)
        {
            invalidValue = null;
            if (arguments == null)
                return WindowsVideoBackendMode.Auto;

            for (var index = 0; index < arguments.Length; index++)
            {
                var argument = arguments[index];
                if (string.IsNullOrEmpty(argument))
                    continue;
                string value = null;
                if (argument.StartsWith(VideoBackendArgument + "=", StringComparison.OrdinalIgnoreCase))
                    value = argument.Substring(VideoBackendArgument.Length + 1);
                else if (string.Equals(argument, VideoBackendArgument, StringComparison.OrdinalIgnoreCase) &&
                         index + 1 < arguments.Length)
                    value = arguments[index + 1];
                else
                    continue;

                switch (value?.Trim().ToLowerInvariant())
                {
                    case "auto": return WindowsVideoBackendMode.Auto;
                    case "windows-gpu": return WindowsVideoBackendMode.WindowsGpu;
                    case "windows-cpu": return WindowsVideoBackendMode.WindowsCpu;
                    case "managed-avi": return WindowsVideoBackendMode.ManagedAvi;
                    default:
                        invalidValue = value ?? string.Empty;
                        return WindowsVideoBackendMode.Auto;
                }
            }
            return WindowsVideoBackendMode.Auto;
        }

        internal static void GetEncoderPolicy(
            WindowsVideoBackendMode mode,
            bool configuredPreferMp4,
            bool configuredAllowAvi,
            out bool preferMp4,
            out bool allowAvi,
            out bool allowCustomEncoder)
        {
            preferMp4 = mode == WindowsVideoBackendMode.WindowsCpu ||
                        (mode != WindowsVideoBackendMode.ManagedAvi && configuredPreferMp4);
            allowAvi = mode == WindowsVideoBackendMode.ManagedAvi ||
                       (mode != WindowsVideoBackendMode.WindowsCpu && configuredAllowAvi);
            allowCustomEncoder = mode == WindowsVideoBackendMode.Auto;
        }

        private static string BackendModeName(WindowsVideoBackendMode mode)
        {
            switch (mode)
            {
                case WindowsVideoBackendMode.WindowsGpu: return "windows-gpu";
                case WindowsVideoBackendMode.WindowsCpu: return "windows-cpu";
                case WindowsVideoBackendMode.ManagedAvi: return "managed-avi";
                default: return "auto";
            }
        }

        public void Start()
        {
            SetEnabled(settings.enableRollingVideo);
        }

        public void SetEnabled(bool enabled)
        {
            requestedEnabled = enabled;
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (enabled && !backendEnvironmentLogged)
            {
                backendEnvironmentLogged = true;
                var selected = windowsGpuRecorder != null
                    ? "windows-gpu"
                    : windowsVideoBackendMode == WindowsVideoBackendMode.WindowsCpu
                        ? "windows-cpu"
                        : windowsVideoBackendMode == WindowsVideoBackendMode.ManagedAvi
                            ? "managed-avi"
                            : "generic-auto";
                RecordDiagnostic("Video backend mode=" + BackendModeName(windowsVideoBackendMode) +
                                 ", selected=" + selected +
                                 ", renderer=" + SystemInfo.graphicsDeviceType +
                                 ", device=" + SystemInfo.graphicsDeviceName +
                                 ", os=" + SystemInfo.operatingSystem +
                                 ", unity=" + Application.unityVersion + ".");
            }
#endif
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
            if (windowsGpuRecorder != null)
            {
                windowsGpuRecorder.SetEnabled(enabled);
                return;
            }
            if (enabled)
                StartGenericCapture();
            else if (!IsFinalizing)
            {
                StopCapture();
            }
        }

        private void StartGenericCapture()
        {
            if (captureCoroutine != null)
                return;
            captureStartedTime = Time.realtimeSinceStartupAsDouble;
            captureCoroutine = host.StartCoroutine(CaptureLoop());
        }

        private void RecoverFromWindowsGpuFailure(string error, Action<VideoCaptureResult> pendingIncident)
        {
            windowsGpuRecorder = null;
            if (windowsVideoBackendMode == WindowsVideoBackendMode.WindowsGpu)
            {
                requestedEnabled = false;
                RecordDiagnostic("Forced Windows GPU video failed on " + SystemInfo.graphicsDeviceType +
                                 ": " + (error ?? "unknown error") + ". Generic fallback is disabled for this diagnostic run.", LogType.Warning);
                pendingIncident?.Invoke(null);
                return;
            }
            RecordDiagnostic("Windows GPU video failed on " + SystemInfo.graphicsDeviceType +
                             ": " + (error ?? "unknown error") +
                             ". Switching to the generic MP4/AVI compatibility recorder with a fresh history.", LogType.Warning);
            if (!requestedEnabled)
            {
                pendingIncident?.Invoke(null);
                return;
            }

            StartGenericCapture();
            if (pendingIncident != null)
                MarkIncident(pendingIncident);
        }

        public void MarkIncident(Action<VideoCaptureResult> completed)
        {
            var diagnosticCompleted = completed;
            if (IsEnabled)
            {
                diagnosticCompleted = result =>
                {
                    RecordCaptureResult(result);
                    completed?.Invoke(result);
                };
            }
            if (gpuRecorder != null)
            {
                gpuRecorder.MarkIncident(diagnosticCompleted);
                return;
            }
            if (androidGpuRecorder != null)
            {
                androidGpuRecorder.MarkIncident(diagnosticCompleted);
                return;
            }
            if (windowsGpuRecorder != null)
            {
                windowsGpuRecorder.MarkIncident(diagnosticCompleted);
                return;
            }
            if (!requestedEnabled)
            {
                completed?.Invoke(null);
                return;
            }

            var incidentTime = Time.realtimeSinceStartupAsDouble;
            incidentFrames = new List<VideoCaptureFrame>(history);
            incidentCompleted = diagnosticCompleted;
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
            GetEncoderPolicy(
                windowsVideoBackendMode,
                settings.preferMp4,
                settings.allowLegacyAviFallback,
                out var preferMp4,
                out var allowLegacyAviFallback,
                out var allowCustomEncoder);
#if UNITY_WEBGL && !UNITY_EDITOR
            if (preferMp4)
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

                if (result == null && allowLegacyAviFallback)
                {
                    result = VideoEncoderBackend.Encode(
                        outputStem,
                        frames,
                        capturedWidth,
                        capturedHeight,
                        settings.videoFramesPerSecond,
                        settings.videoBitrateKbps,
                        durationSeconds,
                        settings.videoJpegQuality,
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
                    settings.videoJpegQuality,
                    preferMp4,
                    allowLegacyAviFallback,
                    out encoderError);
            }
#elif UNITY_ANDROID && !UNITY_EDITOR
            if (preferMp4 && allowCustomEncoder && BugReporter.VideoEncoderOverride == null && AreRawFileFrames(frames))
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
                    settings.videoJpegQuality,
                    preferMp4,
                    allowLegacyAviFallback,
                    out encoderError,
                    allowCustomEncoder);
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
                settings.videoJpegQuality,
                preferMp4,
                allowLegacyAviFallback,
                out encoderError,
                allowCustomEncoder));

            while (!task.IsCompleted)
                yield return null;

            if (task.IsFaulted)
            {
                Debug.LogException(task.Exception);
                encoderError = task.Exception?.GetBaseException().Message;
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
                RecordDiagnostic("Video finalization failed: " + (encoderError ?? "unknown encoder error"), LogType.Warning);
            }
            else
            {
                if (string.Equals(result.Extension, ".avi", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(encoderError))
                    RecordDiagnostic("Preferred MP4 encoder failed before managed AVI fallback succeeded: " + encoderError, LogType.Warning);
                RecordCaptureResult(result);
            }
            completed?.Invoke(result);
            DeleteFrameCache();
            history.Clear();
            historyBytes = 0;
            captureStartedTime = Time.realtimeSinceStartupAsDouble;
            if (!requestedEnabled)
                StopCapture();
        }

        internal void RecordCaptureResult(VideoCaptureResult result)
        {
            if (result == null || ReferenceEquals(result, lastDiagnosticCapture))
                return;
            lastDiagnosticCapture = result;
            var dimensions = result.Width > 0 && result.Height > 0
                ? ", output=" + result.Width + "x" + result.Height
                : string.Empty;
            var effectiveFramesPerSecond = result.DurationSeconds > 0d
                ? result.FrameCount / result.DurationSeconds
                : 0d;
            RecordDiagnostic("Video finalized by " + result.EncoderName +
                             ": frames=" + result.FrameCount +
                             ", duration=" + result.DurationSeconds.ToString("0.00", CultureInfo.InvariantCulture) + "s" +
                             ", effectiveFps=" + effectiveFramesPerSecond.ToString("0.00", CultureInfo.InvariantCulture) +
                             dimensions +
                             ", file=" + result.FilePath + ".");
        }

        internal string BuildDiagnostics()
        {
            lock (diagnosticEntries)
            {
                var builder = new StringBuilder();
                foreach (var entry in diagnosticEntries)
                    builder.AppendLine(entry);
                return builder.ToString();
            }
        }

        private void RecordDiagnostic(string message, LogType type = LogType.Log)
        {
            var fullMessage = "[Macaca Beacon] " + message;
            var entry = "[" + DateTime.UtcNow.ToString("O") + "] [" + type + "] " + fullMessage;
            lock (diagnosticEntries)
            {
                diagnosticEntries.Enqueue(entry);
                while (diagnosticEntries.Count > DiagnosticCapacity)
                    diagnosticEntries.Dequeue();
            }
            if (type == LogType.Warning)
                Debug.LogWarning(fullMessage);
            else
                Debug.Log(fullMessage);
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
            androidGpuRecorder?.Dispose();
            windowsGpuRecorder?.Dispose();
            if (gpuRecorder == null && androidGpuRecorder == null && windowsGpuRecorder == null)
                StopCapture();
        }

    }
}
