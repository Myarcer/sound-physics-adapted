using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace soundphysicsadapted.Core
{
    public static class ExecutionTracer
    {
        private static readonly string TraceFilePath = @"Y:\ClaudeWINDOWS\learning\sound-physics-trace\trace_output.csv";
        private static readonly ConcurrentQueue<string> _messageQueue = new ConcurrentQueue<string>();
        private static CancellationTokenSource _cancellationTokenSource;
        private static Task _writerTask;
        public static bool IsEnabled { get; set; } = true;

        public static void Initialize()
        {
            if (!IsEnabled) return;
            try
            {
                var dir = Path.GetDirectoryName(TraceFilePath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                using (var writer = new StreamWriter(new FileStream(TraceFilePath, FileMode.Create, FileAccess.Write, FileShare.Read)))
                {
                    writer.WriteLine("ThreadId,Timestamp,EventType,ClassName,MethodName,Details");
                }

                _cancellationTokenSource = new CancellationTokenSource();
                _writerTask = Task.Run(ProcessQueue, _cancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                IsEnabled = false;
                Console.WriteLine($"[ExecutionTracer] Failed to initialize: {ex}");
            }
        }

        private static async Task ProcessQueue()
        {
            try
            {
                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    await FlushToFile();
                    await Task.Delay(100, _cancellationTokenSource.Token);
                }
            }
            catch (TaskCanceledException)
            {
                // Expected on shutdown
            }
            finally
            {
                await FlushToFile(); // Final flush before exiting
            }
        }

        private static async Task FlushToFile()
        {
            if (_messageQueue.IsEmpty) return;

            try
            {
                using (var writer = new StreamWriter(new FileStream(TraceFilePath, FileMode.Append, FileAccess.Write, FileShare.Read)))
                {
                    while (_messageQueue.TryDequeue(out string line))
                    {
                        await writer.WriteLineAsync(line);
                    }
                }
            }
            catch
            {
                // Silently drop errors (e.g., file locked)
            }
        }

        public static void Enter(string className, string methodName, string details = "")
        {
            if (!IsEnabled) return;
            long ticks = DateTime.UtcNow.Ticks;
            int threadId = Thread.CurrentThread.ManagedThreadId;
            _messageQueue.Enqueue($"{threadId},{ticks},ENTER,{className},{methodName},{details}");
        }

        public static void Exit(string className, string methodName, string details = "")
        {
            if (!IsEnabled) return;
            long ticks = DateTime.UtcNow.Ticks;
            int threadId = Thread.CurrentThread.ManagedThreadId;
            _messageQueue.Enqueue($"{threadId},{ticks},EXIT,{className},{methodName},{details}");
        }

        public static void Close()
        {
            if (!IsEnabled) return;
            IsEnabled = false;
            try
            {
                _cancellationTokenSource?.Cancel();
                _writerTask?.Wait(1500); // Wait up to 1.5s for final writes
            }
            catch
            {
                // Ignore teardown errors
            }
        }
    }
}
