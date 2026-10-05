// Desktop-only stub: ContentImporter logs through Dishwasher.Log, which the
// Android build backs with logcat.  The harness just echoes to stdout.
using System;

namespace Dishwasher
{
    internal static class Log
    {
        public static void Info(string message) => Console.WriteLine("[info] " + message);
        public static void Error(string message) => Console.WriteLine("[error] " + message);
        public static void Exception(string context, Exception ex) =>
            Console.WriteLine("[ex] " + context + ": " + ex.GetType().Name + ": " + ex.Message);
    }
}
