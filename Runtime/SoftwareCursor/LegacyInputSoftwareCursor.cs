using UnityEngine;

namespace MacacaGames.RuntimeBugReporter
{
#if ENABLE_LEGACY_INPUT_MANAGER
    internal static class LegacyInputSoftwareCursor
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (!Input.mousePresent)
                return;

            SoftwareCursorInputInstaller.Install(
                ReadDelta,
                ReadButtonState,
                SoftwareCursorInputInstaller.LegacyPriority);
        }

        private static Vector2 ReadDelta()
        {
            return new Vector2(Input.GetAxisRaw("Mouse X"), -Input.GetAxisRaw("Mouse Y"));
        }

        private static BugReporterController.SoftwareCursorButtonState ReadButtonState()
        {
            var state = Input.GetMouseButton(0)
                ? BugReporterController.SoftwareCursorButtonState.Held
                : BugReporterController.SoftwareCursorButtonState.None;
            if (Input.GetMouseButtonDown(0))
                state |= BugReporterController.SoftwareCursorButtonState.Pressed;
            if (Input.GetMouseButtonUp(0))
                state |= BugReporterController.SoftwareCursorButtonState.Released;
            return state;
        }
    }
#endif
}
