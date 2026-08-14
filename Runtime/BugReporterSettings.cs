using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace MacacaGames.RuntimeBugReporter
{
    public enum EntryButtonCorner
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    [Obsolete("Use EntryButtonCorner instead.")]
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

#if MACACA_BEACON_PRODUCTION || PRODUCTION
        // Keep the asset type and runtime API available in Production, but do
        // not compile or serialize any project-specific Beacon configuration.
        // These properties are intentionally non-serialized shell values.
        public bool enableInBuild => false;
        public KeyCode shortcut => KeyCode.None;
        public bool allowEscapeToClose => false;
        public bool fullscreen => false;
        public float backdropOpacity => 0f;
        public float interfaceScale => 1f;
        public float desktopWidthRatio => 0.64f;
        public bool showEntryButton => false;
        public float desktopEntryButtonSize => 44f;
        public float mobileEntryButtonSize => 68f;
        public float entryButtonOpacity => 0f;
        public EntryButtonCorner entryButtonCorner => EntryButtonCorner.TopRight;
        public bool enableThreeFingerGesture => false;
        public float threeFingerGestureHoldSeconds => 0f;
        public string botToken => string.Empty;
        public string channelId => string.Empty;
        public bool includeScreenshot => false;
        public bool includeDiagnostics => false;
        public bool includeRecentLogs => false;
        public int screenshotJpegQuality => 85;
        public int maximumLogEntries => 0;
        public bool enableRollingVideo => false;
        public bool preferMp4 => false;
        public bool allowLegacyAviFallback => false;
        public int videoFramesPerSecond => 1;
        public int secondsBefore => 0;
        public int secondsAfter => 0;
        public int videoWidth => 320;
        public int videoJpegQuality => 65;
        public int videoBitrateKbps => 128;
        public int maximumVideoCacheMegabytes => 0;
        public int maximumAttachmentMegabytes => 1;
        public bool saveFailedReportsLocally => false;
        public int maximumRetainedLocalReports => 0;
        public string reportTitle => string.Empty;
        public string privacyNotice => string.Empty;
        public string[] categories => new[] { "Other" };
#else
        [Header("Activation")]
        [FormerlySerializedAs("enabledInBuild")]
        [FormerlySerializedAs("enableBugReporter")]
        [Tooltip("Enable Macaca Beacon in Player builds. Editor Play Mode remains enabled for testing.")]
        public bool enableInBuild = true;
        public KeyCode shortcut = KeyCode.F6;
        public bool allowEscapeToClose = true;

        [Header("Appearance")]
        [Tooltip("Use the entire Game View for the report form. Disable this to use a centered desktop window.")]
        public bool fullscreen = true;
        [Range(0f, 0.75f)] public float backdropOpacity = 0.42f;
        [Range(0.8f, 1.5f)] public float interfaceScale = 1.25f;
        [Range(0.45f, 0.9f)] public float desktopWidthRatio = 0.64f;

        [Header("Entry Button")]
        [Tooltip("Show a corner button that opens the reporter on desktop and mobile. It only consumes input inside its own rectangle.")]
        [FormerlySerializedAs("mobileEntryButton")]
        public bool showEntryButton = false;
        [Range(32f, 80f)] public float desktopEntryButtonSize = 44f;
        [FormerlySerializedAs("mobileEntrySize")]
        [Range(48f, 112f)] public float mobileEntryButtonSize = 68f;
        [FormerlySerializedAs("mobileEntryOpacity")]
        [Range(0.15f, 1f)] public float entryButtonOpacity = 0.72f;
        [FormerlySerializedAs("mobileEntryCorner")]
        public EntryButtonCorner entryButtonCorner = EntryButtonCorner.TopRight;

        [Header("Mobile Gesture")]
        [Tooltip("Open the reporter after holding three fingers on the screen. This gesture does not reserve a visible UI area.")]
        [FormerlySerializedAs("mobileThreeFingerGesture")]
        public bool enableThreeFingerGesture = true;
        [FormerlySerializedAs("mobileGestureHoldSeconds")]
        [Range(0.3f, 2f)] public float threeFingerGestureHoldSeconds = 0.75f;

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
        [Tooltip("Continuously caches raw rolling frames. Windows/Proton uses a bounded preallocated native RAM ring; other generic fallbacks may use temporary disk files.")]
        public bool enableRollingVideo = true;
        [Tooltip("Prefer a Slack-friendly H.264 MP4 when a runtime encoder backend is available.")]
        public bool preferMp4 = true;
        [Tooltip("Use managed MJPEG AVI when this platform has no MP4 backend or MP4 encoding fails.")]
        public bool allowLegacyAviFallback = true;
        [Range(1, 60)] public int videoFramesPerSecond = 30;
        [Range(1, 10)] public int secondsBefore = 5;
        [Range(0, 5)] public int secondsAfter = 1;
        [Range(320, 1920)] public int videoWidth = 960;
        [Range(20, 90)] public int videoJpegQuality = 65;
        [Range(128, 8000)] public int videoBitrateKbps = 1500;
        [Tooltip("Maximum raw rolling-frame cache. Windows/Proton preallocates up to this RAM budget and automatically lowers capture FPS when the requested duration would exceed it; other generic paths use it as a temporary disk limit.")]
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
#endif

#if MACACA_BEACON_PRODUCTION
        [Obsolete("Use enableInBuild instead.")]
        public bool enabledInBuild => enableInBuild;
        [Obsolete("Use enableInBuild instead.")]
        public bool enableBugReporter => enableInBuild;
        [Obsolete("Use showEntryButton instead.")]
        public bool mobileEntryButton => showEntryButton;
        [Obsolete("Use mobileEntryButtonSize instead.")]
        public float mobileEntrySize => mobileEntryButtonSize;
        [Obsolete("Use entryButtonOpacity instead.")]
        public float mobileEntryOpacity => entryButtonOpacity;
        [Obsolete("Use enableThreeFingerGesture instead.")]
        public bool mobileThreeFingerGesture => enableThreeFingerGesture;
        [Obsolete("Use threeFingerGestureHoldSeconds instead.")]
        public float mobileGestureHoldSeconds => threeFingerGestureHoldSeconds;
#pragma warning disable 0618
        [Obsolete("Use entryButtonCorner instead.")]
        public MobileEntryCorner mobileEntryCorner => (MobileEntryCorner)entryButtonCorner;
#pragma warning restore 0618
#else
        [Obsolete("Use enableInBuild instead.")]
        public bool enabledInBuild { get => enableInBuild; set => enableInBuild = value; }
        [Obsolete("Use enableInBuild instead.")]
        public bool enableBugReporter { get => enableInBuild; set => enableInBuild = value; }
        [Obsolete("Use showEntryButton instead.")]
        public bool mobileEntryButton { get => showEntryButton; set => showEntryButton = value; }
        [Obsolete("Use mobileEntryButtonSize instead.")]
        public float mobileEntrySize { get => mobileEntryButtonSize; set => mobileEntryButtonSize = value; }
        [Obsolete("Use entryButtonOpacity instead.")]
        public float mobileEntryOpacity { get => entryButtonOpacity; set => entryButtonOpacity = value; }
        [Obsolete("Use enableThreeFingerGesture instead.")]
        public bool mobileThreeFingerGesture { get => enableThreeFingerGesture; set => enableThreeFingerGesture = value; }
        [Obsolete("Use threeFingerGestureHoldSeconds instead.")]
        public float mobileGestureHoldSeconds { get => threeFingerGestureHoldSeconds; set => threeFingerGestureHoldSeconds = value; }
#pragma warning disable 0618
        [Obsolete("Use entryButtonCorner instead.")]
        public MobileEntryCorner mobileEntryCorner
        {
            get => (MobileEntryCorner)entryButtonCorner;
            set => entryButtonCorner = (EntryButtonCorner)value;
        }
#pragma warning restore 0618
#endif

        public static BugReporterSettings LoadOrDefault()
        {
#if MACACA_BEACON_PRODUCTION || PRODUCTION
            return null;
#else
            var configured = Resources.Load<BugReporterSettings>(ResourceName);
            if (configured != null)
                return configured;

            var defaults = CreateInstance<BugReporterSettings>();
            defaults.hideFlags = HideFlags.HideAndDontSave;
            return defaults;
#endif
        }
    }
}
