using System;
using System.IO;
using MacacaGames.RuntimeBugReporter;
using UnityEditor;
using UnityEngine;

namespace MacacaGames.RuntimeBugReporter.Editor
{
    [InitializeOnLoad]
    internal static class WindowsGpuVideoSmoke
    {
        private static bool active;
        private static bool opened;
        private static double enteredPlayModeAt;
        private static DateTime startedAtUtc;

        static WindowsGpuVideoSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-macacaWindowsGpuSmoke") < 0)
                return;
            active = true;
            startedAtUtc = DateTime.UtcNow;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.update += Update;
            EditorApplication.delayCall += () => EditorApplication.EnterPlaymode();
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredPlayMode)
                return;
            enteredPlayModeAt = EditorApplication.timeSinceStartup;
            BugReporter.SetVideoRecordingEnabled(true);
        }

        private static void Update()
        {
            if (!active || !EditorApplication.isPlaying || enteredPlayModeAt <= 0d)
                return;

            var elapsed = EditorApplication.timeSinceStartup - enteredPlayModeAt;
            if (!opened && elapsed >= 5d)
            {
                opened = true;
                BugReporter.Open();
            }

            if (elapsed < 12d)
                return;

            var captureDirectory = Path.Combine(Application.temporaryCachePath, "MacacaBeacon", "Captures");
            FileInfo newestMp4 = null;
            FileInfo newestAvi = null;
            if (Directory.Exists(captureDirectory))
            {
                foreach (var path in Directory.GetFiles(captureDirectory))
                {
                    var info = new FileInfo(path);
                    if (info.LastWriteTimeUtc < startedAtUtc)
                        continue;
                    if (string.Equals(info.Extension, ".mp4", StringComparison.OrdinalIgnoreCase) &&
                        (newestMp4 == null || info.LastWriteTimeUtc > newestMp4.LastWriteTimeUtc))
                        newestMp4 = info;
                    if (string.Equals(info.Extension, ".avi", StringComparison.OrdinalIgnoreCase) &&
                        (newestAvi == null || info.LastWriteTimeUtc > newestAvi.LastWriteTimeUtc))
                        newestAvi = info;
                }
            }

            var success = newestMp4 != null && newestMp4.Length > 0 && newestAvi == null;
            Debug.Log("[Macaca Beacon Smoke] Windows GPU MP4=" + (newestMp4?.FullName ?? "none") +
                      ", bytes=" + (newestMp4?.Length ?? 0) + ", AVI=" + (newestAvi?.FullName ?? "none"));
            active = false;
            EditorApplication.Exit(success ? 0 : 1);
        }
    }
}
