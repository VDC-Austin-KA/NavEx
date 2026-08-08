using System;

namespace NavEx.Core
{
    public enum LogLevel { Debug, Info, Success, Warning, Error }

    /// <summary>
    /// Static message sink so the engine classes can report into the window's log
    /// panel without holding a reference to it. When no sink is wired (unit tests,
    /// or before the window is constructed) messages are dropped — never turned
    /// into a MessageBox, because a batch export must never stop on a pop-up.
    /// </summary>
    public static class Log
    {
        public static Action<string, LogLevel> Sink;

        public static void Debug(string message) { Emit(message, LogLevel.Debug); }
        public static void Info(string message) { Emit(message, LogLevel.Info); }
        public static void Success(string message) { Emit(message, LogLevel.Success); }
        public static void Warning(string message) { Emit(message, LogLevel.Warning); }
        public static void Error(string message) { Emit(message, LogLevel.Error); }

        public static void Error(string context, Exception ex)
        {
            if (ex == null) { Emit(context, LogLevel.Error); return; }
            Emit(context + ": " + ex.Message, LogLevel.Error);
            Emit(ex.StackTrace ?? "", LogLevel.Debug);
        }

        private static void Emit(string message, LogLevel level)
        {
            Action<string, LogLevel> sink = Sink;
            if (sink != null) sink(message ?? "", level);
        }
    }

    /// <summary>
    /// Progress + cancellation channel handed down into the extractor and writers.
    /// Navisworks' own API is single-threaded and must be driven from the main
    /// thread, so <see cref="Pump"/> is how the UI stays responsive: the window
    /// wires it to a bounded Dispatcher pump between work batches.
    /// </summary>
    public class ProgressContext
    {
        public Action<string, double> Report;   // message, 0..1 (negative = indeterminate)
        public Action Pump;
        public volatile bool CancelRequested;

        public bool IsCancelled { get { return CancelRequested; } }

        public void Update(string message, double fraction)
        {
            Action<string, double> r = Report;
            if (r != null) r(message, fraction);
        }

        public void Tick()
        {
            Action p = Pump;
            if (p != null) p();
        }

        public void ThrowIfCancelled()
        {
            if (CancelRequested) throw new OperationCanceledException("Export cancelled.");
        }
    }
}
