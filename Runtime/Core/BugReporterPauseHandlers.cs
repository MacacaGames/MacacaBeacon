using UnityEngine;

namespace MacacaGames.RuntimeBugReporter
{
    /// <summary>
    /// Pauses gameplay by setting Unity's time scale to zero and restores the
    /// time scale that was active before the pause.
    /// </summary>
    public sealed class TimeScaleBugReporterPauseHandler : IBugReporterPauseHandler
    {
        private float previousTimeScale;
        private bool isPaused;

        public void Pause()
        {
            if (isPaused)
                return;

            previousTimeScale = Time.timeScale;
            Time.timeScale = 0f;
            isPaused = true;
        }

        public void Resume()
        {
            if (!isPaused)
                return;

            Time.timeScale = previousTimeScale;
            isPaused = false;
        }
    }
}
