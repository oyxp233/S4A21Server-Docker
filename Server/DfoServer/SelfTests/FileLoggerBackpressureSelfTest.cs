using System;

namespace DfoServer.SelfTests
{
    public static class FileLoggerBackpressureSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== FILE_LOGGER_BACKPRESSURE selftest ===");
            var failures = 0;
            var queue = new BoundedAsyncLogQueue(2);

            Check("queue keeps the configured capacity", queue.Capacity == 2, ref failures);
            Check(
                "queue accepts entries until full",
                queue.TryEnqueue("one") && queue.TryEnqueue("two"),
                ref failures);
            Check(
                "queue rejects instead of retaining an unbounded entry",
                !queue.TryEnqueue("three") && queue.DroppedCount == 1,
                ref failures);

            queue.TryComplete();
            Check(
                "completed queue also accounts for dropped entries",
                !queue.TryEnqueue("after-complete") && queue.DroppedCount == 2,
                ref failures);

            Console.WriteLine(
                failures == 0
                    ? "FILE_LOGGER_BACKPRESSURE selftest passed."
                    : $"FILE_LOGGER_BACKPRESSURE selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
