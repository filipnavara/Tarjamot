using System.Runtime.CompilerServices;
using Tarjamot.Engine;
using NUnit.Framework;

namespace Tarjamot.Tests;

[TestFixture]
public class RetentionTest
{
	[Test]
	public void WorkerPoolBoundsCountBytesAndModes()
	{
		var pool = new IdleWorkerPool<object>(3, 100, 70);
		var serial = new object();
		var olderParallel = new object();
		var parallel = new object();
		pool.Return(serial, false, 60);
		// Larger than one worker may retain: dropped, the pool keeps what it has.
		pool.Return(new object(), false, 71);
		Assert.That(pool.Retained, Is.EqualTo((1, 60L)));
		// Over the byte budget: the least recently returned worker makes room.
		pool.Return(olderParallel, true, 41);
		pool.Return(parallel, true, 40);
		Assert.That(pool.Retained, Is.EqualTo((2, 81L)));
		Assert.That(pool.Take(false), Is.Null);
		Assert.That(pool.Take(true), Is.SameAs(parallel));
		Assert.That(pool.Take(true), Is.SameAs(olderParallel));
		Assert.That(pool.Retained, Is.EqualTo((0, 0L)));
		for (int i = 0; i < 100; i++)
			pool.Return(new object(), false, 0);
		Assert.That(pool.Retained.Count, Is.EqualTo(3));
	}

	[Test]
	public void BurstOfOneModeDoesNotLockTheOtherModeOut()
	{
		var pool = new IdleWorkerPool<object>(4, 100, 60);
		for (int i = 0; i < 4; i++)
			pool.Return(new object(), false, 10);
		var parallel = new object();

		pool.Return(parallel, true, 60);

		Assert.That(pool.Take(true), Is.SameAs(parallel));
		// The pool was full (four workers); the oldest serial worker made room.
		Assert.That(pool.Retained, Is.EqualTo((3, 30L)));
	}

	[Test]
	public void WorkerLargerThanTheWholePoolIsDroppedWithoutEvictingOthers()
	{
		var pool = new IdleWorkerPool<object>(4, 50, 100);
		var kept = new object();
		pool.Return(kept, false, 20);

		pool.Return(new object(), true, 60);

		Assert.That(pool.Retained, Is.EqualTo((1, 20L)));
		Assert.That(pool.Take(false), Is.SameAs(kept));
	}

	[Test]
	public void WorkerPoolIsBoundedUnderConcurrentReturnsAndBorrows()
	{
		var pool = new IdleWorkerPool<object>(4, 120, 50);
		Parallel.For(0, 10000, i =>
		{
			bool parallel = i % 2 == 0;
			var worker = pool.Take(parallel) ?? new object();
			pool.Return(worker, parallel, i % 3 == 0 ? 50 : 10);
			var retained = pool.Retained;
			Assert.That(retained.Count, Is.LessThanOrEqualTo(4));
			Assert.That(retained.Bytes, Is.InRange(0, 120));
		});
		while (pool.Take(false) is not null) { }
		while (pool.Take(true) is not null) { }
		Assert.That(pool.Retained, Is.EqualTo((0, 0L)));
	}

	[Test]
	public void QuantizedRetentionCountsAllArrayCapacities()
	{
		var activations = new QuantizedActivations { Values = new sbyte[65], Widened = new short[129], Shifted = new byte[33] };
		Assert.That(activations.RetainedBytes, Is.EqualTo(72 + 72 + 264 + 40));
	}

	[Test]
	public void CacheSweepEvictsWithoutAnotherRequestAndUsesLastRelease()
	{
		var time = new ManualTime();
		using var cache = new ModelCache<string, object>(_ => new object(), 4, TimeSpan.FromMinutes(5), time);
		var lease = cache.Borrow("a");
		var model = lease.Value;
		time.Advance(TimeSpan.FromMinutes(10));
		Assert.That(cache.Count, Is.EqualTo(1));
		lease.Dispose();
		time.Advance(TimeSpan.FromMinutes(4));
		Assert.That(cache.Count, Is.EqualTo(1));
		time.Advance(TimeSpan.FromMinutes(1));
		Assert.That(cache.Count, Is.Zero);
		using var reloaded = cache.Borrow("a");
		Assert.That(reloaded.Value, Is.Not.SameAs(model));
	}

	[Test]
	public void SubMillisecondTimeoutStillHasAPeriodicSweep()
	{
		var time = new ManualTime();
		using var cache = new ModelCache<string, object>(_ => new object(), 2, TimeSpan.FromTicks(1), time);
		for (int i = 0; i < 2; i++)
		{
			using (var lease = cache.Borrow("a"))
				_ = lease.Value;
			Assert.That(time.MinimumPeriod, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(1)));
			time.Advance(TimeSpan.FromMilliseconds(1));
			Assert.That(cache.Count, Is.Zero);
		}
	}

	[Test]
	public void RetentionOptionsRejectNegativeLimitsAndAllowDisabledCaches()
	{
		foreach (var options in new[]
		{
			new TranslationOptions { MaxIdleWorkers = -1 }, new TranslationOptions { MaxIdleWorkerBytes = -1 },
			new TranslationOptions { MaxIdleWorkerPoolBytes = -1 }, new TranslationOptions { MaxCachedModels = -1 },
			new TranslationOptions { ModelIdleTimeout = TimeSpan.Zero }
		}) Assert.Throws<ArgumentOutOfRangeException>(() => options.ValidateRetention());
		new TranslationOptions { MaxIdleWorkers = 0, MaxIdleWorkerBytes = 0, MaxIdleWorkerPoolBytes = 0, MaxCachedModels = 0 }.ValidateRetention();
	}

	[Test]
	public void CacheEvictsLeastRecentlyReleasedIdleEntryEvenAtSameTimestamp()
	{
		var time = new ManualTime();
		using var cache = new ModelCache<string, object>(_ => new object(), 2, TimeSpan.FromMinutes(5), time);
		object first;
		object second;
		using (var lease = cache.Borrow("a")) first = lease.Value;
		using (var lease = cache.Borrow("b")) second = lease.Value;
		using (var lease = cache.Borrow("a")) Assert.That(lease.Value, Is.SameAs(first));
		using (var lease = cache.Borrow("c")) _ = lease.Value;
		Assert.That(cache.Count, Is.EqualTo(2));
		using (var lease = cache.Borrow("a")) Assert.That(lease.Value, Is.SameAs(first));
		using (var lease = cache.Borrow("b")) Assert.That(lease.Value, Is.Not.SameAs(second));
	}

	[Test]
	public void ExplicitUnloadDetachesActiveGenerationAndOldReleasePreservesNewOne()
	{
		using var cache = new ModelCache<string, object>(_ => new object(), 2, TimeSpan.FromMinutes(5));
		var old = cache.Borrow("a");
		var oldModel = old.Value;
		cache.Clear();
		using var current = cache.Borrow("a");
		Assert.That(current.Value, Is.Not.SameAs(oldModel));
		old.Dispose();
		using var again = cache.Borrow("a");
		Assert.That(again.Value, Is.SameAs(current.Value));
		Assert.That(cache.Count, Is.EqualTo(1));
	}

	[Test]
	public async Task CacheLoadsOnceWhileProtectedAgainstIdleAndCountEviction()
	{
		var time = new ManualTime();
		using var entered = new CountdownEvent(8);
		using var finish = new ManualResetEventSlim();
		int loads = 0;
		using var cache = new ModelCache<string, object>(_ =>
		{
			Interlocked.Increment(ref loads);
			finish.Wait();
			return new object();
		}, 0, TimeSpan.FromMinutes(5), time);
		var tasks = Enumerable.Range(0, 8).Select(_ => Task.Factory.StartNew(() =>
		{
			using var lease = cache.Borrow("a");
			entered.Signal();
			return lease.Value;
		}, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
		try
		{
			Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
			time.Advance(TimeSpan.FromMinutes(10));
			Assert.That(cache.Count, Is.EqualTo(1));
		}
		finally { finish.Set(); }
		var values = await Task.WhenAll(tasks);
		Assert.That(loads, Is.EqualTo(1));
		Assert.That(values.All(value => ReferenceEquals(value, values[0])), Is.True);
		Assert.That(cache.Count, Is.Zero);
	}

	[Test]
	public void FailedLoadsCanBeRetriedAndDisposalPreservesActiveLease()
	{
		int loads = 0;
		var cache = new ModelCache<string, object>(_ => Interlocked.Increment(ref loads) == 1 ? throw new IOException() : new object(), 2, TimeSpan.FromMinutes(5));
		using (var failed = cache.Borrow("a")) Assert.Throws<IOException>(() => _ = failed.Value);
		Assert.That(cache.Count, Is.Zero);
		using var active = cache.Borrow("a");
		var model = active.Value;
		cache.Dispose();
		Assert.Throws<ObjectDisposedException>(() => cache.Borrow("a"));
		Assert.That(active.Value, Is.SameAs(model));
	}

	[Test]
	public void FailedLoadIsRetriedWhileAnotherLeaseIsOpen()
	{
		int loads = 0;
		using var cache = new ModelCache<string, object>(_ => Interlocked.Increment(ref loads) == 1 ? throw new IOException() : new object(), 2, TimeSpan.FromMinutes(5));
		using var waiting = cache.Borrow("a");
		using (var failed = cache.Borrow("a"))
			Assert.Throws<IOException>(() => _ = failed.Value);

		// A request still holds the failed generation; a new request must load again rather than
		// rethrow the failure cached by the old one.
		using var retried = cache.Borrow("a");

		Assert.That(retried.Value, Is.Not.Null);
		Assert.That(loads, Is.EqualTo(2));
	}

	[Test]
	public void SweepRunsOnlyWhileIdleModelsExist()
	{
		var time = new ManualTime();
		using var cache = new ModelCache<string, object>(_ => new object(), 4, TimeSpan.FromMinutes(5), time);
		Assert.That(cache.IsSweepArmed, Is.False);

		var lease = cache.Borrow("a");
		_ = lease.Value;
		Assert.That(cache.IsSweepArmed, Is.False);

		lease.Dispose();
		Assert.That(cache.IsSweepArmed, Is.True);
		Assert.That(time.ArmedTimers, Is.EqualTo(1));

		// Borrowing the only idle model makes it busy again.
		using (var again = cache.Borrow("a"))
			Assert.That(cache.IsSweepArmed, Is.False);
		Assert.That(cache.IsSweepArmed, Is.True);

		time.Advance(TimeSpan.FromMinutes(5));
		Assert.That(cache.Count, Is.Zero);
		Assert.That(cache.IsSweepArmed, Is.False);
		Assert.That(time.ArmedTimers, Is.Zero);

		using (var other = cache.Borrow("b"))
			_ = other.Value;
		Assert.That(cache.IsSweepArmed, Is.True);
		cache.Clear();
		Assert.That(cache.IsSweepArmed, Is.False);
	}

	[Test]
	public void CancellationReleasesLeaseAndTimerDoesNotRootOwner()
	{
		var time = new ManualTime();
		using (var cache = new ModelCache<string, object>(_ => new object(), 2, TimeSpan.FromMinutes(5), time))
		{
			Assert.Throws<OperationCanceledException>(() =>
			{
				using var lease = cache.Borrow("a");
				_ = lease.Value;
				new CancellationToken(canceled: true).ThrowIfCancellationRequested();
			});
			time.Advance(TimeSpan.FromMinutes(5));
			Assert.That(cache.Count, Is.Zero);
		}
		var weak = CreateUnownedCache(time);
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
		Assert.That(weak.IsAlive, Is.False);
		time.Advance(TimeSpan.FromMinutes(1));
		Assert.That(time.ActiveTimers, Is.Zero);
	}

	// The cache holds an idle model, so its sweep timer is armed when the owner is collected.
	[MethodImpl(MethodImplOptions.NoInlining)]
	static WeakReference CreateUnownedCache(ManualTime time)
	{
		var cache = new ModelCache<string, object>(static _ => new object(), 2, TimeSpan.FromMinutes(5), time);
		using (var lease = cache.Borrow("a"))
			_ = lease.Value;
		return new WeakReference(cache);
	}

	sealed class ManualTime : TimeProvider
	{
		readonly List<ManualTimer> timers = [];
		long ticks;
		public override long TimestampFrequency => TimeSpan.TicksPerSecond;
		public override long GetTimestamp() => ticks;
		public TimeSpan MinimumPeriod => timers.Where(timer => timer.Armed).Min(timer => timer.Period);
		public int ActiveTimers => timers.Count(timer => !timer.Disposed);
		public int ArmedTimers => timers.Count(timer => timer.Armed);

		public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
		{
			var timer = new ManualTimer(this, callback, state, dueTime, period);
			timers.Add(timer);
			return timer;
		}

		public void Advance(TimeSpan elapsed)
		{
			ticks += elapsed.Ticks;
			foreach (var timer in timers.ToArray()) timer.Fire();
		}

		// Timeout.InfiniteTimeSpan disarms the timer (due) or makes it fire once (period).
		sealed class ManualTimer(ManualTime time, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
		{
			long due = Due(time, dueTime);
			TimeSpan repeat = period;
			public bool Disposed { get; private set; }
			public TimeSpan Period => repeat;
			public bool Armed => !Disposed && due != long.MaxValue;
			public bool Change(TimeSpan dueTime, TimeSpan period)
			{
				due = Due(time, dueTime);
				repeat = period;
				return !Disposed;
			}
			public void Dispose() => Disposed = true;
			public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
			public void Fire()
			{
				if (Disposed || due > time.ticks) return;
				due = Due(time, repeat);
				callback(state);
			}
			static long Due(ManualTime time, TimeSpan delay) => delay == Timeout.InfiniteTimeSpan ? long.MaxValue : time.ticks + delay.Ticks;
		}
	}
}
