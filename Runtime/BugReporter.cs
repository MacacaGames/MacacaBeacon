using System;
using System.Collections.Generic;
using UnityEngine;

namespace MacacaGames.RuntimeBugReporter
{
    public static class BugReporter
    {
        private static readonly List<IBugReportDataProvider> Providers = new List<IBugReportDataProvider>();
        internal static IBugReportTransport TransportOverride;

        public static bool IsOpen => BugReporterController.Instance != null && BugReporterController.Instance.IsOpen;

        public static void Open()
        {
            EnsureController();
            BugReporterController.Instance.RequestOpen();
        }

        public static void Close()
        {
            if (BugReporterController.Instance != null)
                BugReporterController.Instance.Close();
        }

        public static void RegisterDataProvider(IBugReportDataProvider provider)
        {
            if (provider != null && !Providers.Contains(provider))
                Providers.Add(provider);
        }

        public static void UnregisterDataProvider(IBugReportDataProvider provider) => Providers.Remove(provider);

        public static void SetTransport(IBugReportTransport transport) => TransportOverride = transport;

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
            var settings = BugReporterSettings.LoadOrDefault();
            if (settings.enabledInBuild)
                EnsureController(settings);
        }

        private static void EnsureController(BugReporterSettings settings = null)
        {
            if (BugReporterController.Instance != null)
                return;

            var host = new GameObject("[Macaca Beacon]");
            host.hideFlags = HideFlags.HideInHierarchy;
            UnityEngine.Object.DontDestroyOnLoad(host);
            var controller = host.AddComponent<BugReporterController>();
            controller.Initialize(settings ?? BugReporterSettings.LoadOrDefault());
        }
    }
}
