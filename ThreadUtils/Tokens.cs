using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ThreadUtils
{
    public class SingleCompletionToken<T>
    {
        private T _Result;
        private ManualResetEventSlim _Event;

        public void Set(T value)
        {
            _Result = value;
            _Event.Set();
        }

        public T Get()
        {
            _Event.Wait();
            return _Result;
        }

        public SingleCompletionToken()
        {
            _Event = new ManualResetEventSlim(false);
        }
    }

    public class MultiCompletionToken<T>
    {
        private ConcurrentQueue<T> _ResultQueue;
        private ManualResetEventSlim _Event;
        private bool _Ended;
        public void Set(T value)
        {
            _ResultQueue.Enqueue(value);
            _Event.Set();
        }

        public void End()
        {
            _Ended = true;
            _Event.Set();
        }

        public IEnumerable<T> Get()
        {
            while (!_Ended)
            {
                _Event.Wait();
                _Event.Reset();
                while (_ResultQueue.TryDequeue(out T val))
                {
                    yield return val;
                }
            }
        }

        public MultiCompletionToken()
        {
            _Event = new ManualResetEventSlim(false);
            _Ended = false;
            _ResultQueue = new ConcurrentQueue<T>();
        }
    }
}
