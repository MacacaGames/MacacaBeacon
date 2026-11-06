using UnityEngine;

namespace MacacaGames.RuntimeBugReporter
{
    // Shared ordering for focus, keyboard navigation and reveal requests.
    internal static class MobileFormNavigation
    {
        internal static readonly string[] Controls =
            { "BugReportTitle", "BugReportDescription", "BugReportReporter" };

        internal static int IndexOf(string control) => System.Array.IndexOf(Controls, control);
        internal static int Adjacent(int current, int delta) =>
            current < 0 ? -1 : Mathf.Clamp(current + delta, 0, Controls.Length - 1);

        // Native coordinates are relative to the physical screen; the Unity surface
        // may already have been resized by Android. Map through that surface once.
        internal static float AvailableHeight(float keyboardTop, float surfaceTop, float surfaceHeight, float guiHeight)
        {
            return surfaceHeight <= 0f ? guiHeight :
                Mathf.Clamp((keyboardTop - surfaceTop) / surfaceHeight, 0f, 1f) * guiHeight;
        }

        internal static float RevealScroll(float scroll, Rect target, Rect viewport, float margin)
        {
            var top = viewport.yMin + margin;
            var bottom = viewport.yMax - margin;
            if (target.height > bottom - top || target.yMin < top)
                return Mathf.Max(0f, scroll + target.yMin - top);
            if (target.yMax > bottom)
                return Mathf.Max(0f, scroll + target.yMax - bottom);
            return scroll;
        }
    }
}
