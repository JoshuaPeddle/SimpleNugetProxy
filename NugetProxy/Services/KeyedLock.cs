namespace NugetProxy.Services
{
    /// <summary>
    /// Lets one request at a time work on a given key, so a package that many builds ask for at once is
    /// downloaded from the upstream once instead of once per build.
    /// </summary>
    public sealed class KeyedLock
    {
        private sealed class Gate
        {
            public readonly SemaphoreSlim Semaphore = new(1, 1);
            public int Holders;
        }

        private readonly object _sync = new();
        private readonly Dictionary<string, Gate> _gates = new(StringComparer.Ordinal);

        public async Task<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken)
        {
            Gate gate;
            lock (_sync)
            {
                if (!_gates.TryGetValue(key, out gate!))
                {
                    gate = new Gate();
                    _gates[key] = gate;
                }

                gate.Holders++;
            }

            try
            {
                await gate.Semaphore.WaitAsync(cancellationToken);
            }
            catch
            {
                Release(key, gate, entered: false);
                throw;
            }

            return new Releaser(this, key, gate);
        }

        private void Release(string key, Gate gate, bool entered)
        {
            if (entered)
            {
                gate.Semaphore.Release();
            }

            lock (_sync)
            {
                if (--gate.Holders == 0)
                {
                    _gates.Remove(key);
                }
            }
        }

        private sealed class Releaser : IDisposable
        {
            private readonly KeyedLock _owner;
            private readonly string _key;
            private readonly Gate _gate;
            private int _disposed;

            public Releaser(KeyedLock owner, string key, Gate gate)
            {
                _owner = owner;
                _key = key;
                _gate = gate;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _owner.Release(_key, _gate, entered: true);
                }
            }
        }
    }
}
