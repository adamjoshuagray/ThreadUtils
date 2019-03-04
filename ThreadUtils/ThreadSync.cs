using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ThreadUtils
{
    public class ExceptionEventArgs : EventArgs
    {
        public Exception ThrownException { get; }

        public ExceptionEventArgs(Exception ex)
        {
            ThrownException = ex;
        }
    }

    public abstract class SingleThreadWorker<T> : IDisposable
    {

        private int _Core = 0;
        private ThreadPriorityLevel _Priority = ThreadPriorityLevel.Normal;
        public bool IsDisposed { get; private set; }

        public virtual void Dispose()
        {
            IsDisposed = true;
            _mre_work.Set();
            _mre_dispose.Wait();
        }

        public bool CrashOnExcept { get; set; }

        protected SingleThreadWorker(int core, ThreadPriorityLevel priority)
        {
            _Core = core;
            _Priority = priority;
            _thread = new Thread(_Run);
            _thread.IsBackground = true;
            _thread.Start();
        }

        protected SingleThreadWorker()
        {
            _Core = CoreBinder.Allocate();
            _Priority = ThreadPriorityLevel.Normal;
            _thread = new Thread(_Run);
            _thread.IsBackground = true;
            _thread.Start();
        }

        public static event EventHandler<ExceptionEventArgs> ExceptionThrown;

        public bool BusySpin { get; set; } = false;

        private void _Run()
        {
            if (_Core != 0)
            {
                CoreBinder.Bind(_Core, _Priority);
            }

            while (!IsDisposed)
            {
                if (!BusySpin)
                {
                    _mre_work.Wait();
                    _mre_work.Reset();
                }

                if (!_jobs.IsEmpty)
                {
                    while (_jobs.TryDequeue(out T tpl))
                    {
                        try
                        {
                            DoWork(tpl);
                        }
                        catch (Exception ex)
                        {
                            if (CrashOnExcept)
                            {
                                throw new Exception("STE Crash: " + GetType(), ex);
                            }

                            ExceptionThrown?.Invoke(this, new ExceptionEventArgs(ex));
                        }
                    }
                }
            }

            _mre_dispose.Set();
        }

        protected abstract void DoWork(T obj);

        protected void EnqueueWork(T obj)
        {
            _jobs.Enqueue(obj);
            _mre_work.Set();
        }

        public int QueueLength => _jobs.Count;

        protected Thread _thread;
        private ConcurrentQueue<T> _jobs = new ConcurrentQueue<T>();
        private ManualResetEventSlim _mre_work = new ManualResetEventSlim(false);
        private ManualResetEventSlim _mre_dispose = new ManualResetEventSlim(false);
    }

    public class ThreadSyncWork
    {
        public bool FastInvoke;
        public Delegate Action;
        public Func<object, object[], object> FastAction;
        public object This;
        public object[] Args;
    }

    public class ThreadSync : SingleThreadWorker<ThreadSyncWork>
    {
        public ThreadSync(int core = 0, ThreadPriorityLevel priority = ThreadPriorityLevel.Normal) : base(core,
            priority)
        {
            lock (_syncs)
            {
                _syncs.Add(_thread, this);
            }
        }

        protected override void DoWork(ThreadSyncWork job)
        {
            // This is very slow
            if (job.FastInvoke)
            {
                job.FastAction.Invoke(job.This, job.Args);
            }
            else
            {
                job.Action.DynamicInvoke(job.Args);
            }
        }

        public void Invoke(Delegate method, params object[] args) =>
            EnqueueWork(new ThreadSyncWork { Action = method, Args = args, FastInvoke = false });

        /// <summary>
        /// *Relatively* fast invokation. Call methodinfo.bind to get the needed func object.
        /// </summary>
        /// <param name="func"></param>
        /// <param name="_this"></param>
        /// <param name="args"></param>
        public void FastInvoke(Func<object, object[], object> func, object _this, params object[] args) =>
            EnqueueWork(new ThreadSyncWork { FastAction = func, This = _this, Args = args, FastInvoke = true });

        public void Invoke(Action action) => EnqueueWork(new ThreadSyncWork { Action = action, Args = null });
        ManualResetEventSlim barrier_mre = new ManualResetEventSlim(false);

        public T FInvoke<T>(Func<T> func)
        {
            SingleCompletionToken<T> token = new SingleCompletionToken<T>();
            EnqueueWork(new ThreadSyncWork {Action = new Action(() => token.Set(func())), Args = null});
            return token.Get();
        }

        public IEnumerable<T> EInvoke<T>(Func<IEnumerable<T>> enumerator)
        {
            MultiCompletionToken<T> token = new MultiCompletionToken<T>();
            EnqueueWork(new ThreadSyncWork
            {
                Action = new Action(() =>
                {
                    foreach (var val in enumerator())
                    {
                        token.Set(val);
                    }
                    token.End();
                }),
                Args = null
            });
            foreach (var v in token.Get())
            {
                yield return v;
            }
        }

        public void AwaitBarrier()
        {
            Invoke(new Action<ManualResetEventSlim>(slim => slim.Set()), barrier_mre);
            barrier_mre.Wait();
            barrier_mre.Reset();
        }

        public void AbortBarrier()
        {
            if (!barrier_mre.IsSet)
            {
                barrier_mre.Set();
            }
        }

        private static Dictionary<Thread, ThreadSync> _syncs = new Dictionary<Thread, ThreadSync>();

        /// <summary>
        /// Gets the ThreadSync associated with the current running thread. If there is no ThreadSync associated then it will return null.
        /// This is a blocking call and is not guarenteed to be *fast*.
        /// </summary>
        /// <returns></returns>
        public static ThreadSync GetCurrentThreadSync()
        {
            var thd = Thread.CurrentThread;
            lock (_syncs)
            {
                if (_syncs.TryGetValue(thd, out var sync))
                {
                    return sync;
                }
            }

            return null;
        }
    }
}
