using UnityEngine;
using UnityEngine.InputSystem;

namespace MacacaGames.RuntimeBugReporter
{
    internal static class InputSystemSoftwareCursor
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (Mouse.current == null)
                return;

            SoftwareCursorInputInstaller.Install(
                ReadDelta,
                ReadButtonState,
                SoftwareCursorInputInstaller.InputSystemPriority);
        }

        private static Vector2 ReadDelta()
        {
            var mouse = Mouse.current;
            if (mouse == null)
                return Vector2.zero;

            var delta = mouse.delta.ReadValue();
            return new Vector2(delta.x, -delta.y);
        }

        private static BugReporterController.SoftwareCursorButtonState ReadButtonState()
        {
            var mouse = Mouse.current;
            if (mouse == null)
                return BugReporterController.SoftwareCursorButtonState.None;

            var state = mouse.leftButton.isPressed
                ? BugReporterController.SoftwareCursorButtonState.Held
                : BugReporterController.SoftwareCursorButtonState.None;
            if (mouse.leftButton.wasPressedThisFrame)
                state |= BugReporterController.SoftwareCursorButtonState.Pressed;
            if (mouse.leftButton.wasReleasedThisFrame)
                state |= BugReporterController.SoftwareCursorButtonState.Released;
            return state;
        }
    }
}
