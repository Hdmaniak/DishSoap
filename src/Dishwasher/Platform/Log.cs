// Tiny diagnostic logger for the Android port.
//
// The decompiled game funnels *all* loader/update failures into
// Globals.FatalErrorDie(), which shows a GamerServices message box. The shim
// makes that a no-op, so exceptions in the async Loader thread would otherwise
// vanish. This helper routes boot milestones and the first real exception to
// logcat (tag "Dishwasher") so boot iteration is observable.
using System;

namespace Dishwasher
{
    public static class Log
    {
        public static void Info(string message)
        {
#if ANDROID
            Android.Util.Log.Info("Dishwasher", message ?? "");
#else
            Console.WriteLine("[Dishwasher] " + message);
#endif
        }

        public static void Error(string message)
        {
#if ANDROID
            Android.Util.Log.Error("Dishwasher", message ?? "");
#else
            Console.Error.WriteLine("[Dishwasher] " + message);
#endif
        }

        public static void Exception(string where, Exception ex)
        {
            Error("[" + where + "] " + (ex == null ? "<null exception>" : ex.ToString()));
        }
    }
}
