using System;
using System.Collections.Generic;
using UnityEngine;

namespace MacacaGames.RuntimeBugReporter
{
    public static class BugReporter
    {
        private static readonly List<IBugReportDataProvider> Providers = new List<IBugReportDataProvider>();
        internal static IBugReportTransport TransportOverride;
        internal static IVideoEncoderBackend VideoEncoderOverride;

        public static bool IsOpen => BugReporterController.Instance != null && BugReporterController.Instance.IsOpen;

        /// <summary>
        /// Gets whether the rolling video recorder is currently enabled.
        /// </summary>
        public static bool IsVideoRecordingEnabled
        {
            get
            {
#if MACACA_BEACON_PRODUCTION
                return false;
#else
                if (BugReporterController.Instance != null)
                    return BugReporterController.Instance.IsVideoRecordingEnabled;
                var settings = BugReporterSettings.LoadOrDefault();
                return IsEnabled(settings) && settings.enableRollingVideo;
#endif
            }
        }

        public static void Open()
        {
#if MACACA_BEACON_PRODUCTION
            return;
#else
            if (EnsureController())
                BugReporterController.Instance.RequestOpen();
#endif
        }

        public static void Close()
        {
            if (BugReporterController.Instance != null)
                BugReporterController.Instance.Close();
        }

        /// <summary>
        /// Enables or disables rolling video capture at runtime. The setting
        /// is session-only and does not modify the project asset.
        /// </summary>
        public static void SetVideoRecordingEnabled(bool enabled)
        {
#if MACACA_BEACON_PRODUCTION
            return;
#else
            if (EnsureController())
                BugReporterController.Instance.SetVideoRecordingEnabled(enabled);
#endif
        }

        public static void RegisterDataProvider(IBugReportDataProvider provider)
        {
            if (provider != null && !Providers.Contains(provider))
                Providers.Add(provider);
        }

        public static void UnregisterDataProvider(IBugReportDataProvider provider) => Providers.Remove(provider);

        public static void SetTransport(IBugReportTransport transport) => TransportOverride = transport;

        /// <summary>
        /// Registers a project-specific runtime encoder, for example a MediaCodec or Media Foundation backend.
        /// Pass null to restore the package's platform default.
        /// </summary>
        public static void SetVideoEncoder(IVideoEncoderBackend encoder) => VideoEncoderOverride = encoder;

        internal static void CollectCustomData(BugReport report)
        {
            foreach (var provider in Providers.ToArray())
            {
                try { provider.Collect(report); }
                catch (Exception exception) { Debug.LogException(exception); }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoBootstrap()
        {
#if MACACA_BEACON_PRODUCTION
            return;
#else
            var settings = BugReporterSettings.LoadOrDefault();
            if (IsEnabled(settings))
                EnsureController(settings);
#endif
        }

        internal static bool IsEnabled(BugReporterSettings settings)
        {
            return settings != null && settings.enableBugReporter;
        }

        private static bool EnsureController(BugReporterSettings settings = null)
        {
            if (BugReporterController.Instance != null)
                return true;

            settings = settings ?? BugReporterSettings.LoadOrDefault();
            if (!IsEnabled(settings))
                return false;

            var host = new GameObject("[Macaca Beacon]");
            host.hideFlags = HideFlags.HideInHierarchy;
            UnityEngine.Object.DontDestroyOnLoad(host);
            var controller = host.AddComponent<BugReporterController>();
            controller.Initialize(settings);
            return true;
        }
    }
}
