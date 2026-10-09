using System.Runtime.InteropServices;

namespace V1Bridge
{
    /// <summary>Microseconds on Windows' performance counter, the one clock both games can compare. (Mono's Stopwatch
    /// counts from when its own process started.)</summary>
    internal static class Clock
    {
        [DllImport("kernel32.dll")] static extern bool QueryPerformanceCounter(out long value);
        [DllImport("kernel32.dll")] static extern bool QueryPerformanceFrequency(out long value);
        static readonly double toMicros = Init();

        static double Init()
        {
            QueryPerformanceFrequency(out long f);
            return 1e6 / f;
        }

        public static long Micros()
        {
            QueryPerformanceCounter(out long v);
            return (long)(v * toMicros);
        }
    }
}