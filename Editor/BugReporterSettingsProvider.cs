using System.IO;
using UnityEditor;
using UnityEngine;

namespace MacacaGames.RuntimeBugReporter.Editor
{
    internal static class BugReporterSettingsProvider
    {
        private const string AssetPath = "Assets/Resources/BugReporterSettings.asset";
        private static BugReporterSettings settings;
        private static SerializedObject serializedSettings;

        [SettingsProvider]
        public static SettingsProvider CreateProvider()
        {
            return new SettingsProvider("Project/Macaca Beacon", SettingsScope.Project)
            {
                label = "Macaca Beacon",
                guiHandler = _ => DrawSettings(),
                keywords = new[] { "bug", "report", "slack", "bot", "screenshot", "video", "F6" }
            };
        }

        [MenuItem("Tools/Macaca Beacon/Open Settings")]
        private static void OpenSettings() => SettingsService.OpenProjectSettings("Project/Macaca Beacon");

        [MenuItem("Tools/Macaca Beacon/Create Settings Asset")]
        private static void CreateSettingsMenu()
        {
            EnsureAsset();
            Selection.activeObject = settings;
            EditorGUIUtility.PingObject(settings);
        }

        private static void DrawSettings()
        {
            EnsureAsset();
            if (serializedSettings == null || serializedSettings.targetObject == null)
                serializedSettings = new SerializedObject(settings);

            serializedSettings.Update();
            EditorGUILayout.Space(8);
            EditorGUILayout.HelpBox("The configured shortcut and optional corner button open the IMGUI reporter at runtime. The Slack Bot sends the report and attachments into its thread.", MessageType.Info);
            EditorGUILayout.HelpBox("Do not ship a Slack bot token in an untrusted public client. Put secrets behind a rate-limited relay service.", MessageType.Warning);
            EditorGUILayout.Space(8);

#if MACACA_BEACON_PRODUCTION
            EditorGUILayout.HelpBox("Macaca Beacon is disabled by MACACA_BEACON_PRODUCTION for the current build target.", MessageType.Info);
#else
            Section("Activation");
            EditorGUILayout.PropertyField(Property("enableInBuild"));
            EditorGUILayout.PropertyField(Property("shortcut"));
            EditorGUILayout.PropertyField(Property("allowEscapeToClose"));

            Section("Appearance");
            var fullscreen = Property("fullscreen");
            EditorGUILayout.PropertyField(fullscreen);
            EditorGUILayout.PropertyField(Property("backdropOpacity"));
            EditorGUILayout.PropertyField(Property("interfaceScale"));
            using (new EditorGUI.DisabledScope(fullscreen.boolValue))
                EditorGUILayout.PropertyField(Property("desktopWidthRatio"));

            Section("Entry Button");
            var showEntryButton = Property("showEntryButton");
            EditorGUILayout.PropertyField(showEntryButton);
            using (new EditorGUI.DisabledScope(!showEntryButton.boolValue))
            {
                EditorGUILayout.PropertyField(Property("entryButtonCorner"));
                EditorGUILayout.PropertyField(Property("desktopEntryButtonSize"));
                EditorGUILayout.PropertyField(Property("mobileEntryButtonSize"));
                EditorGUILayout.PropertyField(Property("entryButtonOpacity"));
            }

            Section("Mobile Gesture");
            var enableThreeFingerGesture = Property("enableThreeFingerGesture");
            EditorGUILayout.PropertyField(enableThreeFingerGesture);
            using (new EditorGUI.DisabledScope(!enableThreeFingerGesture.boolValue))
                EditorGUILayout.PropertyField(Property("threeFingerGestureHoldSeconds"));

            Section("Slack");
            var botToken = Property("botToken");
            botToken.stringValue = EditorGUILayout.PasswordField(new GUIContent(botToken.displayName, botToken.tooltip), botToken.stringValue);
            EditorGUILayout.PropertyField(Property("channelId"));

            Section("Capture");
            EditorGUILayout.PropertyField(Property("includeScreenshot"));
            EditorGUILayout.PropertyField(Property("includeDiagnostics"));
            EditorGUILayout.PropertyField(Property("includeRecentLogs"));
            EditorGUILayout.PropertyField(Property("screenshotJpegQuality"));
            EditorGUILayout.PropertyField(Property("maximumLogEntries"));

            Section("Rolling Video");
            EditorGUILayout.PropertyField(Property("enableRollingVideo"));
            EditorGUILayout.PropertyField(Property("preferMp4"));
            EditorGUILayout.PropertyField(Property("allowLegacyAviFallback"));
            EditorGUILayout.PropertyField(Property("videoFramesPerSecond"));
            EditorGUILayout.PropertyField(Property("secondsBefore"));
            EditorGUILayout.PropertyField(Property("secondsAfter"));
            EditorGUILayout.PropertyField(Property("videoWidth"));
            EditorGUILayout.PropertyField(Property("videoJpegQuality"));
            EditorGUILayout.PropertyField(Property("videoBitrateKbps"));
            EditorGUILayout.PropertyField(Property("maximumVideoCacheMegabytes"));
            EditorGUILayout.PropertyField(Property("maximumAttachmentMegabytes"));

            Section("Local Fallback");
            EditorGUILayout.PropertyField(Property("saveFailedReportsLocally"));
            EditorGUILayout.PropertyField(Property("maximumRetainedLocalReports"));

            Section("Form");
            EditorGUILayout.PropertyField(Property("reportTitle"));
            EditorGUILayout.PropertyField(Property("privacyNotice"));
            EditorGUILayout.PropertyField(Property("categories"), true);
#endif
            serializedSettings.ApplyModifiedProperties();
        }

#if !MACACA_BEACON_PRODUCTION
        private static SerializedProperty Property(string name)
        {
            return serializedSettings.FindProperty(name);
        }

        private static void Section(string title)
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }
#endif

        private static void EnsureAsset()
        {
            if (settings != null)
                return;
            settings = AssetDatabase.LoadAssetAtPath<BugReporterSettings>(AssetPath);
            if (settings != null)
                return;

            var directory = Path.GetDirectoryName(AssetPath);
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);
            settings = ScriptableObject.CreateInstance<BugReporterSettings>();
            AssetDatabase.CreateAsset(settings, AssetPath);
            AssetDatabase.SaveAssets();
            serializedSettings = new SerializedObject(settings);
        }
    }
}
