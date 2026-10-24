namespace Tarjamot;

/// <summary>
/// Leased model cache. Automatic eviction touches only idle entries; explicit invalidation
/// detaches a generation immediately while its existing leases keep it alive.
/// </summary>
internal sealed class ModelCache<TKey, TValue> : IDisposable where TKey : notnull where TValue : class
{
	// Erase only the Lazy factory's type parameter: model construction never uses reflection
	// or a default constructor, which Lazy<T>'s generic trimming annotation would require.
	internal sealed class Entry(Lazy<object> Value)
	{
		public Lazy<object> Value { get; } = Value;
		public int Users;
		public long LastRelease;
		public long LastUse;
		public bool Failed;
	}

	// The timer must not keep a translator (and its models) alive after its owner is gone.
	sealed class SweepState(ModelCache<TKey, TValue> Cache)
	{
		readonly WeakReference<ModelCache<TKey, TValue>> owner = new(Cache);
		public ITimer? Timer;

		public void Sweep()
		{
			if (owner.TryGetTarget(out var cache))
				cache.EvictIdle();
			else
				Timer?.Dispose();
		}
	}

	internal sealed class Lease : IDisposable
	{
		ModelCache<TKey, TValue>? owner;
		readonly TKey key;
		readonly Entry entry;

		internal Lease(ModelCache<TKey, TValue> owner, TKey key, Entry entry)
		{
			this.owner = owner;
			this.key = key;
			this.entry = entry;
		}

		public TValue Value
		{
			get
			{
				var cache = owner ?? throw new ObjectDisposedException(nameof(Lease));
				try { return (TValue)entry.Value.Value; }
				catch
				{
					lock (cache.cacheLock)
					{
						entry.Failed = true;
						// Lazy caches the exception, so detach the failed generation now: leases
						// that are still open must not make new requests rethrow it.
						if (cache.entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
							cache.entries.Remove(key);
					}
					throw;
				}
			}
		}

		public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(key, entry);
	}

	readonly Lock cacheLock = new();
	readonly Dictionary<TKey, Entry> entries = new();
	readonly Func<TKey, TValue> load;
	readonly int maxCount;
	readonly TimeSpan idleTimeout;
	readonly TimeProvider time;
	readonly ITimer timer;
	readonly TimeSpan sweepInterval;
	bool sweepArmed;
	bool disposed;
	long useOrder;

	public ModelCache(Func<TKey, TValue> load, int maxCount, TimeSpan idleTimeout, TimeProvider? time = null)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(maxCount);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleTimeout, TimeSpan.Zero);
		this.load = load;
		this.maxCount = maxCount;
		this.idleTimeout = idleTimeout;
		this.time = time ?? TimeProvider.System;
		var state = new SweepState(this);
		sweepInterval = idleTimeout < TimeSpan.FromMinutes(1) ? idleTimeout : TimeSpan.FromMinutes(1);
		if (sweepInterval < TimeSpan.FromMilliseconds(1))
			sweepInterval = TimeSpan.FromMilliseconds(1);
		// The sweep runs only while idle models exist (see UpdateSweep), so an empty or fully
		// leased cache does not wake the process up.
		timer = this.time.CreateTimer(static state => ((SweepState)state!).Sweep(), state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
		state.Timer = timer;
	}

	internal int Count { get { lock (cacheLock) return entries.Count; } }

	public Lease Borrow(TKey key)
	{
		lock (cacheLock)
		{
			ObjectDisposedException.ThrowIf(disposed, this);
			EvictIdleCore();
			if (!entries.TryGetValue(key, out var entry))
				entries[key] = entry = new Entry(new Lazy<object>(() => load(key), LazyThreadSafetyMode.ExecutionAndPublication));
			entry.Users++;
			if (entries.Count > maxCount)
				EvictIdleCore();
			UpdateSweep();
			return new Lease(this, key, entry);
		}
	}

	public void Clear()
	{
		lock (cacheLock)
		{
			entries.Clear();
			UpdateSweep();
		}
	}

	public void Dispose()
	{
		lock (cacheLock)
		{
			disposed = true;
			entries.Clear();
		}
		timer.Dispose();
	}

	internal void EvictIdle()
	{
		lock (cacheLock) EvictIdleCore();
	}

	/// <summary>Whether the periodic sweep is running, i.e. idle models exist.</summary>
	internal bool IsSweepArmed
	{
		get { lock (cacheLock) return sweepArmed; }
	}

	void Release(TKey key, Entry entry)
	{
		lock (cacheLock)
		{
			entry.Users--;
			if (entry.Users != 0 || !entries.TryGetValue(key, out var current) || !ReferenceEquals(current, entry))
				return;
			entry.LastRelease = time.GetTimestamp();
			entry.LastUse = ++useOrder;
			if (entry.Failed)
				entries.Remove(key);
			EvictIdleCore();
		}
	}

	void EvictIdleCore()
	{
		long now = time.GetTimestamp();
		List<TKey>? expired = null;
		foreach (var (key, entry) in entries)
		{
			if (entry.Users == 0 && time.GetElapsedTime(entry.LastRelease, now) >= idleTimeout)
				(expired ??= []).Add(key);
		}
		if (expired is not null)
		{
			foreach (var key in expired) entries.Remove(key);
		}
		EvictOverLimit();
		UpdateSweep();
	}

	void EvictOverLimit()
	{
		while (entries.Count > maxCount)
		{
			Entry? oldest = null;
			TKey key = default!;
			foreach (var pair in entries)
			{
				if (pair.Value.Users == 0 && (oldest is null || pair.Value.LastUse < oldest.LastUse))
				{
					oldest = pair.Value;
					key = pair.Key;
				}
			}
			if (oldest is null)
				break;
			entries.Remove(key);
		}
	}

	// Called under the lock whenever entries are added, released or removed.
	void UpdateSweep()
	{
		bool idle = false;
		foreach (var entry in entries.Values)
		{
			if (entry.Users == 0)
			{
				idle = true;
				break;
			}
		}
		if (idle == sweepArmed || disposed)
			return;
		sweepArmed = idle;
		timer.Change(idle ? sweepInterval : Timeout.InfiniteTimeSpan, idle ? sweepInterval : Timeout.InfiniteTimeSpan);
	}
}
