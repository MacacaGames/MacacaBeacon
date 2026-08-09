using System;
using System.IO;
using MacacaGames.RuntimeBugReporter;
using UnityEditor;
using UnityEngine;

namespace MacacaGames.RuntimeBugReporter.Editor
{
    [InitializeOnLoad]
    public static class WindowsGpuVideoSmoke
    {
        private const int Width = 640;
        private const int Height = 360;
        private const int FramesPerSecond = 30;
        private const int FrameCount = 45;
        private static bool active;
        private static bool enteredPlayMode;
        private static int submittedFrames;
        private static int settleUpdates;
        private static double startedAt;
        private static IntPtr session;
        private static RenderTexture texture;
        private static string outputPath;

        static WindowsGpuVideoSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-macacaWindowsGpuSmoke") < 0)
                return;
            Arm(false);
        }

        /// <summary>
        /// Explicit command-line entry point for
        /// -executeMethod MacacaGames.RuntimeBugReporter.Editor.WindowsGpuVideoSmoke.Run.
        /// </summary>
        public static void Run()
        {
            Arm(true);
        }

        private static void Arm(bool enterPlayModeImmediately)
        {
            if (!active)
            {
                active = true;
                startedAt = EditorApplication.timeSinceStartup;
                EditorApplication.playModeStateChanged += OnPlayModeChanged;
                EditorApplication.update += Update;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            if (enterPlayModeImmediately)
                EditorApplication.EnterPlaymode();
            else
                EditorApplication.delayCall += () => EditorApplication.EnterPlaymode();
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredPlayMode)
                return;
            enteredPlayMode = true;
            Debug.Log("[Macaca Beacon Smoke] Entered Play Mode, graphics=" + SystemInfo.graphicsDeviceType +
                      ", device=" + SystemInfo.graphicsDeviceName);

            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Direct3D12 ||
                !WindowsGpuVideoSmokeBridge.IsAvailable)
            {
                Fail("D3D12 Windows GPU video bridge is unavailable.");
                return;
            }

            var captureDirectory = Path.Combine(Application.temporaryCachePath, "MacacaBeacon", "Captures");
            Directory.CreateDirectory(captureDirectory);
            outputPath = Path.Combine(captureDirectory, "dx12-smoke-" + Guid.NewGuid().ToString("N") + ".mp4");
            texture = new RenderTexture(Width, Height, 0, RenderTextureFormat.BGRA32, RenderTextureReadWrite.Linear)
            {
                name = "MacacaBeacon.DX12Smoke",
                antiAliasing = 1,
                useMipMap = false,
                autoGenerateMips = false
            };
            texture.Create();
            session = WindowsGpuVideoSmokeBridge.CreateSession(
                outputPath, texture, FramesPerSecond, 1500);
            if (session == IntPtr.Zero)
                Fail("Could not create the native D3D12 GPU video session.");
        }

        private static void Update()
        {
            if (!active)
                return;
            if (EditorApplication.timeSinceStartup - startedAt > 90d)
            {
                Fail("Timed out waiting for the D3D12 smoke test.");
                return;
            }
            if (!enteredPlayMode || !EditorApplication.isPlaying || session == IntPtr.Zero)
                return;

            if (submittedFrames < FrameCount)
            {
                var previous = RenderTexture.active;
                RenderTexture.active = texture;
                try
                {
                    var phase = submittedFrames / (float)Math.Max(1, FrameCount - 1);
                    GL.Clear(true, true, new Color(phase, 0.25f, 1f - phase, 1f));
                }
                finally
                {
                    RenderTexture.active = previous;
                }

                if (!WindowsGpuVideoSmokeBridge.Submit(
                        session, texture, submittedFrames / (double)FramesPerSecond))
                {
                    Fail("Frame submission failed: " +
                         (WindowsGpuVideoSmokeBridge.GetLastError(session) ?? "unknown error"));
                    return;
                }
                submittedFrames++;
                return;
            }

            // Leave two player-loop updates after the final submit before
            // finalizing, so the last plugin event has been consumed.
            if (settleUpdates++ < 2)
                return;

            var finished = WindowsGpuVideoSmokeBridge.FinishSession(session);
            var error = WindowsGpuVideoSmokeBridge.GetLastError(session);
            var info = File.Exists(outputPath) ? new FileInfo(outputPath) : null;
            var success = finished && info != null && info.Length > 512;
            Debug.Log("[Macaca Beacon Smoke] EnteredPlayMode=true, submitted=" + submittedFrames +
                      ", Windows GPU MP4=" + (info?.FullName ?? "none") +
                      ", bytes=" + (info?.Length ?? 0) + ", error=" + (error ?? "none"));
            Cleanup();
            EditorApplication.Exit(success ? 0 : 1);
        }

        private static void Fail(string message)
        {
            Debug.LogError("[Macaca Beacon Smoke] " + message);
            Cleanup();
            EditorApplication.Exit(1);
        }

        private static void Cleanup()
        {
            active = false;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.update -= Update;
            if (session != IntPtr.Zero)
            {
                WindowsGpuVideoSmokeBridge.DestroySession(session);
                session = IntPtr.Zero;
            }
            if (texture != null)
            {
                texture.Release();
                UnityEngine.Object.DestroyImmediate(texture);
                texture = null;
            }
        }
    }
}
