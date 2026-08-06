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
                keywords = new[] { "bug", "report", "slack", "webhook", "screenshot", "video", "F6" }
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
            EditorGUILayout.HelpBox("F6 opens the IMGUI reporter at runtime. Incoming Webhooks send the report message; Slack Bot credentials are additionally required for screenshot/video uploads.", MessageType.Info);
            EditorGUILayout.HelpBox("Do not ship a Slack webhook or bot token in an untrusted public client. Put secrets behind a rate-limited relay service.", MessageType.Warning);
            EditorGUILayout.Space(8);

            var iterator = serializedSettings.GetIterator();
            var enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (iterator.propertyPath == "m_Script")
                {
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.PropertyField(iterator, true);
                    continue;
                }

                if (iterator.propertyPath == "botToken")
                {
                    iterator.stringValue = EditorGUILayout.PasswordField(new GUIContent(iterator.displayName, iterator.tooltip), iterator.stringValue);
                    continue;
                }
                EditorGUILayout.PropertyField(iterator, true);
            }
            serializedSettings.ApplyModifiedProperties();
        }

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
