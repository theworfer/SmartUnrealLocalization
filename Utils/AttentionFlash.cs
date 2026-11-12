using System;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace SmartUnrealLocalization.Utils
{
    /// <summary>
    /// Briefly pulses a ring around a control the app is telling the user to press.
    ///
    /// <para>A message like "Run the local QC pass first" names an action but not
    /// its location, and the panel has half a dozen buttons. Pointing at the right
    /// one is the difference between an instruction and a hunt.</para>
    ///
    /// <para>The ring is a BORDER COLOUR only — every style that opts in reserves
    /// a transparent ring of the same thickness up front, so turning it on cannot
    /// move the button by a pixel. A layout shift under the user's cursor is worse
    /// than no hint at all.</para>
    /// </summary>
    public static class AttentionFlash
    {
        private const string Cls = "attention";

        // Weak: a window closed mid-pulse must not be kept alive by a timer.
        private static readonly ConditionalWeakTable<Control, DispatcherTimer> Running = new();

        /// <summary>
        /// Blink <paramref name="target"/>'s ring <paramref name="blinks"/> times.
        /// Calling again on the same control restarts the pulse rather than
        /// stacking a second timer on top of the first.
        /// </summary>
        public static void Pulse(Control? target, int blinks = 3, int intervalMs = 220)
        {
            if (target is null || blinks <= 0) return;

            if (Running.TryGetValue(target, out var previous))
            {
                previous.Stop();
                Running.Remove(target);
            }

            int stepsLeft = blinks * 2;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(intervalMs) };
            timer.Tick += (_, _) =>
            {
                stepsLeft--;
                if (stepsLeft <= 0)
                {
                    timer.Stop();
                    Running.Remove(target);
                    target.Classes.Remove(Cls);      // always end OFF, whatever the parity
                    return;
                }
                if (target.Classes.Contains(Cls)) target.Classes.Remove(Cls);
                else target.Classes.Add(Cls);
            };

            target.Classes.Add(Cls);                 // visible immediately, not one tick later
            Running.Add(target, timer);
            timer.Start();
        }
    }
}
