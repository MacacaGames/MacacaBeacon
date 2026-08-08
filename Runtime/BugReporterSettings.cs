using UnityEngine;

namespace MacacaGames.RuntimeBugReporter
{
    public enum MobileEntryCorner
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    [CreateAssetMenu(fileName = ResourceName, menuName = "Macaca Beacon/Settings")]
    public sealed class BugReporterSettings : ScriptableObject
    {
        public const string ResourceName = "BugReporterSettings";

        [Header("Activation")]
        public bool enabledInBuild = true;
        public KeyCode shortcut = KeyCode.F6;
        public bool allowEscapeToClose = true;

        [Header("Appearance")]
        [Tooltip("Use the entire Game View for the report form. Disable this to use a centered desktop window.")]
        public bool fullscreen = true;
        [Range(0f, 0.75f)] public float backdropOpacity = 0.42f;
        [Range(0.8f, 1.5f)] public float interfaceScale = 1.25f;
        [Range(0.45f, 0.9f)] public float desktopWidthRatio = 0.64f;

        [Header("Mobile entry")]
        [Tooltip("Show a small edge button on iOS and Android. It only consumes touches inside its own rectangle.")]
        public bool mobileEntryButton = true;
        [Tooltip("Open the reporter after holding three fingers on the screen. This gesture does not reserve a visible UI area.")]
        public bool mobileThreeFingerGesture = true;
        [Range(48f, 112f)] public float mobileEntrySize = 68f;
        [Range(0.15f, 1f)] public float mobileEntryOpacity = 0.72f;
        [Range(0.3f, 2f)] public float mobileGestureHoldSeconds = 0.75f;
        public MobileEntryCorner mobileEntryCorner = MobileEntryCorner.TopRight;

        [Header("Slack")]
        [Tooltip("Slack bot token with chat:write and files:write. Used for both the report message and attachments.")]
        public string botToken = "";
        [Tooltip("Channel ID where the bot posts reports, for example C0123456789.")]
        public string channelId = "";

        [Header("Capture")]
        public bool includeScreenshot = true;
        public bool includeDiagnostics = true;
        public bool includeRecentLogs = true;
        [Range(20, 100)] public int screenshotJpegQuality = 85;
        [Min(20)] public int maximumLogEntries = 200;

        [Header("Rolling video (optional)")]
        [Tooltip("Continuously caches low-rate raw frames on disk. Supported platforms finalize them as hardware H.264 MP4; JPEG/AVI is compatibility-only.")]
        public bool enableRollingVideo = true;
        [Tooltip("Prefer a Slack-friendly H.264 MP4 when a runtime encoder backend is available.")]
        public bool preferMp4 = true;
        [Tooltip("Use managed MJPEG AVI when this platform has no MP4 backend or MP4 encoding fails.")]
        public bool allowLegacyAviFallback = true;
        [Range(1, 24)] public int videoFramesPerSecond = 15;
        [Range(1, 10)] public int secondsBefore = 15;
        [Range(0, 5)] public int secondsAfter = 1;
        [Range(320, 1920)] public int videoWidth = 960;
        [Range(20, 90)] public int videoJpegQuality = 65;
        [Range(128, 8000)] public int videoBitrateKbps = 1500;
        [Tooltip("Maximum temporary disk space for raw rolling frames. Use at least 512 MB for 960 px wide portrait video at 6 FPS and an 8 second history.")]
        [Range(32, 2048)] public int maximumVideoCacheMegabytes = 512;
        [Range(1, 100)] public int maximumAttachmentMegabytes = 25;

        [Header("Local fallback")]
        [Tooltip("Stage every report locally before upload. Successful reports are removed; failed reports remain for manual upload.")]
        public bool saveFailedReportsLocally = true;
        [Range(1, 100)] public int maximumRetainedLocalReports = 20;

        [Header("Form")]
        public string reportTitle = "MACACA BEACON";
        public string privacyNotice = "This report sends your description, screenshot, recent logs, and device diagnostics to the development team for debugging only. A local copy is retained if upload fails.";
        public string[] categories = { "Gameplay", "UI", "Visual", "Audio", "Performance", "Other" };

        public static BugReporterSettings LoadOrDefault()
        {
            var configured = Resources.Load<BugReporterSettings>(ResourceName);
            if (configured != null)
                return configured;

            var defaults = CreateInstance<BugReporterSettings>();
            defaults.hideFlags = HideFlags.HideAndDontSave;
            return defaults;
        }
    }
}
