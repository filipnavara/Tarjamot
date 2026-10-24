namespace Tarjamot.Engine;

/// <summary>
/// Idle workers of both execution modes, kept apart so that each mode takes its own kind, under
/// one count and byte budget. When a returned worker does not fit, the least recently returned
/// idle workers (of either mode) are evicted to make room, so a burst of one mode cannot lock the
/// other out of the pool. Only a worker larger than the per-worker or total budget is dropped.
/// </summary>
internal sealed class IdleWorkerPool<T> where T : class
{
	readonly Lock poolLock = new();
	// Oldest first: workers are returned at the end and taken from the end.
	readonly List<(T Worker, long Bytes, long Order)> serialWorkers = [];
	readonly List<(T Worker, long Bytes, long Order)> parallelWorkers = [];
	readonly int maxCount;
	readonly long maxBytes;
	readonly long maxWorkerBytes;
	long retainedBytes;
	long returnOrder;

	public IdleWorkerPool(int maxCount, long maxBytes, long maxWorkerBytes)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(maxCount);
		ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
		ArgumentOutOfRangeException.ThrowIfNegative(maxWorkerBytes);
		this.maxCount = maxCount;
		this.maxBytes = maxBytes;
		this.maxWorkerBytes = maxWorkerBytes;
	}

	internal (int Count, long Bytes) Retained
	{
		get { lock (poolLock) return (serialWorkers.Count + parallelWorkers.Count, retainedBytes); }
	}

	/// <summary>The most recently returned idle worker of the mode, or null.</summary>
	public T? Take(bool parallel)
	{
		lock (poolLock)
		{
			var workers = parallel ? parallelWorkers : serialWorkers;
			if (workers.Count == 0)
				return null;
			var entry = workers[^1];
			workers.RemoveAt(workers.Count - 1);
			retainedBytes -= entry.Bytes;
			return entry.Worker;
		}
	}

	public void Return(T worker, bool parallel, long bytes)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(bytes);
		lock (poolLock)
		{
			if (maxCount == 0 || bytes > maxWorkerBytes || bytes > maxBytes)
				return;
			(parallel ? parallelWorkers : serialWorkers).Add((worker, bytes, ++returnOrder));
			retainedBytes += bytes;
			// The returned worker is the newest and fits on its own, so it is never the one evicted.
			while (serialWorkers.Count + parallelWorkers.Count > maxCount || retainedBytes > maxBytes)
				EvictOldest();
		}
	}

	void EvictOldest()
	{
		var workers = parallelWorkers.Count == 0 || (serialWorkers.Count != 0 && serialWorkers[0].Order < parallelWorkers[0].Order)
			? serialWorkers
			: parallelWorkers;
		retainedBytes -= workers[0].Bytes;
		workers.RemoveAt(0);
	}
}
