using UnityEngine;

namespace MacacaGames.RuntimeBugReporter
{
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

        [Header("Slack")]
        [Tooltip("Slack Incoming Webhook URL. Keep this out of public builds; use a relay for untrusted clients.")]
        public string incomingWebhookUrl = "";
        [Tooltip("Optional Slack bot token with files:write. Required to upload screenshots and video directly to Slack.")]
        public string botToken = "";
        [Tooltip("Channel ID used by the Slack file upload API, for example C0123456789.")]
        public string channelId = "";

        [Header("Capture")]
        public bool includeScreenshot = true;
        public bool includeDiagnostics = true;
        public bool includeRecentLogs = true;
        [Range(20, 100)] public int screenshotJpegQuality = 85;
        [Min(20)] public int maximumLogEntries = 200;

        [Header("Rolling video (optional)")]
        [Tooltip("Continuously keeps low-rate JPEG frames in memory. The report contains seconds before and after F6 as an MJPEG AVI.")]
        public bool enableRollingVideo = false;
        [Range(1, 15)] public int videoFramesPerSecond = 6;
        [Range(1, 10)] public int secondsBefore = 5;
        [Range(0, 10)] public int secondsAfter = 5;
        [Range(320, 1920)] public int videoWidth = 960;
        [Range(20, 90)] public int videoJpegQuality = 65;
        [Range(1, 100)] public int maximumAttachmentMegabytes = 25;

        [Header("Form")]
        public string reportTitle = "MACACA BEACON";
        public string privacyNotice = "This report sends your description, screenshot, recent logs, and device diagnostics to the development team for debugging only.";
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
