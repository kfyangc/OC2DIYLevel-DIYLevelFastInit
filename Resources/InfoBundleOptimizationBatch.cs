using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace DIYLevelFastInit
{
    /// <summary>Processes startup bundle replacements serially without calling Unity from its worker.</summary>
    internal sealed class InfoBundleOptimizationBatch : IDisposable
    {
        private readonly string[] _paths;
        private readonly bool _restore;
        private readonly object _sync = new object();
        private readonly Queue<string> _logs = new Queue<string>();
        private Thread _worker;
        private volatile bool _cancelled, _done;
        private volatile int _completed;
        private volatile string _current;

        internal InfoBundleOptimizationBatch(string[] paths, bool restore)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            _paths = (string[])paths.Clone();
            _restore = restore;
        }

        internal bool Done { get { return _done; } }
        internal int Completed { get { return _completed; } }
        internal int Total { get { return _paths.Length; } }
        internal string Current { get { return _current; } }

        internal void Start()
        {
            lock (_sync)
            {
                if (_worker != null) throw new InvalidOperationException("Info bundle batch already started");
                try
                {
                    _worker = new Thread(Work) { IsBackground = true, Name = "DIY info bundle optimizer", Priority = ThreadPriority.BelowNormal };
                    _worker.Start();
                }
                catch { _done = true; throw; }
            }
        }

        private void Work()
        {
            int replaced = 0, ready = 0, skipped = 0, failed = 0, restored = 0;
            try
            {
                foreach (string path in _paths)
                {
                    if (_cancelled) break;
                    _current = Path.GetFileName(path);
                    InfoBundleOptimizer.Result result = _restore
                        ? InfoBundleOptimizer.Restore(path, () => _cancelled, Log)
                        : InfoBundleOptimizer.Optimize(path, () => _cancelled, Log);
                    switch (result)
                    {
                        case InfoBundleOptimizer.Result.Replaced: replaced++; break;
                        case InfoBundleOptimizer.Result.AlreadyOptimized: ready++; break;
                        case InfoBundleOptimizer.Result.Restored: restored++; break;
                        case InfoBundleOptimizer.Result.Failed: failed++; break;
                        default: skipped++; break;
                    }
                    _completed++;
                }
            }
            catch (OperationCanceledException) { _cancelled = true; }
            catch (Exception error) { failed++; Log("info bundle startup worker failed: " + error); }
            finally
            {
                Log("info bundle startup " + (_restore ? "restore" : "optimization") + (_cancelled ? " cancelled" : " complete")
                    + ": " + replaced + " replaced, " + ready + " already independent, " + restored + " restored, "
                    + skipped + " skipped, " + failed + " failed (" + _completed + "/" + _paths.Length + ")");
                _current = null;
                _done = true;
            }
        }

        private void Log(string message) { lock (_sync) _logs.Enqueue(message); }

        internal void Drain(Action<string> log)
        {
            List<string> messages = new List<string>();
            lock (_sync) while (_logs.Count != 0) messages.Add(_logs.Dequeue());
            foreach (string message in messages) if (log != null) log(message);
        }

        public void Dispose() { _cancelled = true; }
    }
}
