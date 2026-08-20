using System;
using UnityEngine;

namespace MacacaGames.RuntimeBugReporter
{
    internal static class SoftwareCursorInputInstaller
    {
        internal const int LegacyPriority = 100;
        internal const int InputSystemPriority = 200;

        private static int currentPriority = int.MinValue;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            currentPriority = int.MinValue;
            BugReporterController.SoftwareCursorDeltaReader = null;
            BugReporterController.SoftwareCursorButtonReader = null;
        }

        internal static void Install(
            Func<Vector2> deltaReader,
            Func<BugReporterController.SoftwareCursorButtonState> buttonReader,
            int priority)
        {
            if (deltaReader == null || buttonReader == null)
                return;
            if (priority < currentPriority)
                return;

            BugReporterController.SoftwareCursorDeltaReader = deltaReader;
            BugReporterController.SoftwareCursorButtonReader = buttonReader;
            currentPriority = priority;
        }
    }
}
