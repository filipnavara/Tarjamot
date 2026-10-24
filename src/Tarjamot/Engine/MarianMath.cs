using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Tarjamot.Engine;

/// <summary>
/// Float operations with the exact semantics of Firefox's Bergamot WASM build, which compiles
/// Marian with SSE (emulated on WASM SIMD) and Eigen. Every multiply and add is a separate
/// operation (no fused multiply-add), and sums keep Marian's order, so the results are bit-identical.
/// </summary>
internal static class MarianMath
{
	static readonly Vector128<float> ExpHigh = Vector128.Create(88.3762626647949f);
	static readonly Vector128<float> ExpLow = Vector128.Create(-88.3762626647949f);
	static readonly Vector128<float> Log2E = Vector128.Create(1.44269504088896341f);
	static readonly Vector128<float> Half = Vector128.Create(0.5f);
	static readonly Vector128<float> One = Vector128.Create(1f);
	static readonly Vector128<float> C1 = Vector128.Create(0.693359375f);
	static readonly Vector128<float> C2 = Vector128.Create(-2.12194440e-4f);
	static readonly Vector128<float> P0 = Vector128.Create(1.9875691500E-4f);
	static readonly Vector128<float> P1 = Vector128.Create(1.3981999507E-3f);
	static readonly Vector128<float> P2 = Vector128.Create(8.3334519073E-3f);
	static readonly Vector128<float> P3 = Vector128.Create(4.1665795894E-2f);
	static readonly Vector128<float> P4 = Vector128.Create(1.6666665459E-1f);
	static readonly Vector128<float> P5 = Vector128.Create(5.0000001201E-1f);

	/// <summary>Cephes exp as in Marian's sse_mathfun.h exp_ps.</summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Vector128<float> ExpPs(Vector128<float> value)
	{
		var x = Vector128.Max(Vector128.Min(value, ExpHigh), ExpLow);
		var fx = x * Log2E + Half;
		// floorf as in exp_ps: truncate, then subtract 1 where the truncation is greater.
		var truncated = Vector128.ConvertToSingle(Vector128.ConvertToInt32(fx));
		fx = truncated - (Vector128.GreaterThan(truncated, fx) & One);
		x = x - fx * C1;
		x = x - fx * C2;
		var z = x * x;
		var y = P0 * x + P1;
		y = y * x + P2;
		y = y * x + P3;
		y = y * x + P4;
		y = y * x + P5;
		y = y * z + x;
		y = y + One;
		var pow2n = Vector128.ShiftLeft(Vector128.ConvertToInt32(fx) + Vector128.Create(0x7f), 23).AsSingle();
		return y * pow2n;
	}

	public static float ExpPs(float value) => ExpPs(Vector128.Create(value)).ToScalar();

	/// <summary>
	/// Marian's CPU Softmax. Marian takes the 4-lane path (lane-wise max and sums, exp_ps) when the
	/// row length is a multiple of 4, and the scalar path (expf) otherwise. In a padded batch the row
	/// length is the batch's maximum length, so it is passed separately; padded entries are masked
	/// to exp() ~ 0 and do not change the sums.
	/// </summary>
	public static void Softmax(Span<float> x, int rowLength)
	{
		if (rowLength % 4 != 0)
		{
			float max = x[0];
			for (int i = 1; i < x.Length; i++)
				max = MathF.Max(max, x[i]);
			float sum = 0f;
			for (int i = 0; i < x.Length; i++)
			{
				x[i] = Expf(x[i] - max);
				sum += x[i];
			}
			for (int i = 0; i < x.Length; i++)
				x[i] = x[i] / sum;
			return;
		}

		int vectors = x.Length / 4;
		ref float rx = ref MemoryMarshal.GetReference(x);
		// Lane-wise maximum; a partial last vector only has its valid lanes compared.
		var maxLanes = Vector128.Create(float.NegativeInfinity);
		for (int v = 0; v < vectors; v++)
			maxLanes = Vector128.Max(maxLanes, Vector128.LoadUnsafe(ref rx, (nuint)(v * 4)));
		float maxAll = MathF.Max(MathF.Max(maxLanes[0], maxLanes[1]), MathF.Max(maxLanes[2], maxLanes[3]));
		for (int i = vectors * 4; i < x.Length; i++)
			maxAll = MathF.Max(maxAll, x[i]);
		var maxVector = Vector128.Create(maxAll);
		var sums = Vector128<float>.Zero;
		for (int v = 0; v < vectors; v++)
		{
			var e = ExpPs(Vector128.LoadUnsafe(ref rx, (nuint)(v * 4)) - maxVector);
			e.StoreUnsafe(ref rx, (nuint)(v * 4));
			sums += e;
		}
		// Padded (masked) entries become exp_ps at its lower clamp, a tiny constant, which Marian
		// still adds to the lane sums after the real entries.
		float padded = ExpPs(-88.3762626647949f);
		for (int i = vectors * 4; i < rowLength; i++)
		{
			int lane = i % 4;
			float e = padded;
			if (i < x.Length)
			{
				e = ExpPs(x[i] - maxAll);
				x[i] = e;
			}
			sums = sums.WithElement(lane, sums[lane] + e);
		}
		float total = 0f;
		total += sums[0];
		total += sums[1];
		total += sums[2];
		total += sums[3];
		var totalVector = Vector128.Create(total);
		for (int v = 0; v < vectors; v++)
			(Vector128.LoadUnsafe(ref rx, (nuint)(v * 4)) / totalVector).StoreUnsafe(ref rx, (nuint)(v * 4));
		for (int i = vectors * 4; i < x.Length; i++)
			x[i] = x[i] / total;
	}

	/// <summary>
	/// Scaled attention scores of two heads: scores[h * padded + j] = scale * sum over d of
	/// q[h * headDim + d] * keys[(h * headDim + d) * padded + j], for h = 0, 1, summed over d in order.
	/// </summary>
	/// <remarks>
	/// Each lane is one key, and every key's sum depends on the previous step, so a single head keeps
	/// only one or two additions in flight per step. Two heads' sums are independent and double that.
	/// </remarks>
	public static void AttentionScoresPair(ReadOnlySpan<float> q, ReadOnlySpan<float> keys, int headDim, int padded, float scale, Span<float> scores)
	{
		if ((long)2 * headDim > q.Length || (long)2 * headDim * padded > keys.Length || 2 * padded > scores.Length)
			throw new ArgumentOutOfRangeException(nameof(scores));
		ref float q0 = ref MemoryMarshal.GetReference(q);
		ref float q1 = ref Unsafe.Add(ref q0, headDim);
		ref float k0 = ref MemoryMarshal.GetReference(keys);
		ref float k1 = ref Unsafe.Add(ref k0, headDim * padded);
		ref float s0 = ref MemoryMarshal.GetReference(scores);
		ref float s1 = ref Unsafe.Add(ref s0, padded);
		nuint stride = (nuint)padded;
		int j = 0;
		if (Vector512.IsHardwareAccelerated)
		{
			var scale512 = Vector512.Create(scale);
			for (; j + 32 <= padded; j += 32)
			{
				Vector512<float> a0 = default, a1 = default, b0 = default, b1 = default;
				ref float r0 = ref Unsafe.Add(ref k0, j);
				ref float r1 = ref Unsafe.Add(ref k1, j);
				for (int d = 0; d < headDim; d++)
				{
					var x0 = Vector512.Create(Unsafe.Add(ref q0, d));
					var x1 = Vector512.Create(Unsafe.Add(ref q1, d));
					a0 = a0 + x0 * Vector512.LoadUnsafe(ref r0);
					a1 = a1 + x0 * Vector512.LoadUnsafe(ref r0, 16);
					b0 = b0 + x1 * Vector512.LoadUnsafe(ref r1);
					b1 = b1 + x1 * Vector512.LoadUnsafe(ref r1, 16);
					r0 = ref Unsafe.Add(ref r0, stride);
					r1 = ref Unsafe.Add(ref r1, stride);
				}
				(scale512 * a0).StoreUnsafe(ref s0, (nuint)j);
				(scale512 * a1).StoreUnsafe(ref s0, (nuint)j + 16);
				(scale512 * b0).StoreUnsafe(ref s1, (nuint)j);
				(scale512 * b1).StoreUnsafe(ref s1, (nuint)j + 16);
			}
			for (; j + 16 <= padded; j += 16)
			{
				Vector512<float> a = default, b = default;
				ref float r0 = ref Unsafe.Add(ref k0, j);
				ref float r1 = ref Unsafe.Add(ref k1, j);
				for (int d = 0; d < headDim; d++)
				{
					a = a + Vector512.Create(Unsafe.Add(ref q0, d)) * Vector512.LoadUnsafe(ref r0);
					b = b + Vector512.Create(Unsafe.Add(ref q1, d)) * Vector512.LoadUnsafe(ref r1);
					r0 = ref Unsafe.Add(ref r0, stride);
					r1 = ref Unsafe.Add(ref r1, stride);
				}
				(scale512 * a).StoreUnsafe(ref s0, (nuint)j);
				(scale512 * b).StoreUnsafe(ref s1, (nuint)j);
			}
		}
		if (Vector256.IsHardwareAccelerated)
		{
			var scale256 = Vector256.Create(scale);
			for (; j + 8 <= padded; j += 8)
			{
				Vector256<float> a = default, b = default;
				ref float r0 = ref Unsafe.Add(ref k0, j);
				ref float r1 = ref Unsafe.Add(ref k1, j);
				for (int d = 0; d < headDim; d++)
				{
					a = a + Vector256.Create(Unsafe.Add(ref q0, d)) * Vector256.LoadUnsafe(ref r0);
					b = b + Vector256.Create(Unsafe.Add(ref q1, d)) * Vector256.LoadUnsafe(ref r1);
					r0 = ref Unsafe.Add(ref r0, stride);
					r1 = ref Unsafe.Add(ref r1, stride);
				}
				(scale256 * a).StoreUnsafe(ref s0, (nuint)j);
				(scale256 * b).StoreUnsafe(ref s1, (nuint)j);
			}
		}
		var scale128 = Vector128.Create(scale);
		// Four vectors per head: eight independent sums keep the adds in flight (ARM's only width).
		for (; j + 16 <= padded; j += 16)
		{
			Vector128<float> a0 = default, a1 = default, a2 = default, a3 = default, b0 = default, b1 = default, b2 = default, b3 = default;
			ref float r0 = ref Unsafe.Add(ref k0, j);
			ref float r1 = ref Unsafe.Add(ref k1, j);
			for (int d = 0; d < headDim; d++)
			{
				var x0 = Vector128.Create(Unsafe.Add(ref q0, d));
				var x1 = Vector128.Create(Unsafe.Add(ref q1, d));
				a0 = a0 + x0 * Vector128.LoadUnsafe(ref r0);
				a1 = a1 + x0 * Vector128.LoadUnsafe(ref r0, 4);
				a2 = a2 + x0 * Vector128.LoadUnsafe(ref r0, 8);
				a3 = a3 + x0 * Vector128.LoadUnsafe(ref r0, 12);
				b0 = b0 + x1 * Vector128.LoadUnsafe(ref r1);
				b1 = b1 + x1 * Vector128.LoadUnsafe(ref r1, 4);
				b2 = b2 + x1 * Vector128.LoadUnsafe(ref r1, 8);
				b3 = b3 + x1 * Vector128.LoadUnsafe(ref r1, 12);
				r0 = ref Unsafe.Add(ref r0, stride);
				r1 = ref Unsafe.Add(ref r1, stride);
			}
			(scale128 * a0).StoreUnsafe(ref s0, (nuint)j);
			(scale128 * a1).StoreUnsafe(ref s0, (nuint)j + 4);
			(scale128 * a2).StoreUnsafe(ref s0, (nuint)j + 8);
			(scale128 * a3).StoreUnsafe(ref s0, (nuint)j + 12);
			(scale128 * b0).StoreUnsafe(ref s1, (nuint)j);
			(scale128 * b1).StoreUnsafe(ref s1, (nuint)j + 4);
			(scale128 * b2).StoreUnsafe(ref s1, (nuint)j + 8);
			(scale128 * b3).StoreUnsafe(ref s1, (nuint)j + 12);
		}
		for (; j + 4 <= padded; j += 4)
		{
			Vector128<float> a = default, b = default;
			ref float r0 = ref Unsafe.Add(ref k0, j);
			ref float r1 = ref Unsafe.Add(ref k1, j);
			for (int d = 0; d < headDim; d++)
			{
				a = a + Vector128.Create(Unsafe.Add(ref q0, d)) * Vector128.LoadUnsafe(ref r0);
				b = b + Vector128.Create(Unsafe.Add(ref q1, d)) * Vector128.LoadUnsafe(ref r1);
				r0 = ref Unsafe.Add(ref r0, stride);
				r1 = ref Unsafe.Add(ref r1, stride);
			}
			(scale128 * a).StoreUnsafe(ref s0, (nuint)j);
			(scale128 * b).StoreUnsafe(ref s1, (nuint)j);
		}
	}

	/// <summary>Whether <see cref="AttentionValuesPair"/> supports a head size on this machine.</summary>
	public static bool SupportsAttentionValuesPair(int headDim) => Vector512.IsHardwareAccelerated && headDim is 32 or 48 or 64;

	/// <summary>
	/// <see cref="AttentionValues"/> for two adjacent heads in one pass over the keys:
	/// output[h * headDim + c] = sum over s of scores[h][s] * values[s * stride + h * headDim + c].
	/// </summary>
	/// <remarks>
	/// Each lane is one output column summed over the keys in order, so the sums are the same. One
	/// pass with an accumulator for every 16 columns of both heads keeps four to eight additions in
	/// flight, where a 48-column head took one 256-bit and one 128-bit pass of four additions each.
	/// </remarks>
	public static void AttentionValuesPair(ReadOnlySpan<float> scores0, ReadOnlySpan<float> scores1, ReadOnlySpan<float> values, int stride, int headDim, Span<float> output)
	{
		if (!SupportsAttentionValuesPair(headDim) || scores1.Length != scores0.Length || output.Length < 2 * headDim ||
			(scores0.Length > 0 && (long)(scores0.Length - 1) * stride + 2 * headDim > values.Length))
			throw new ArgumentOutOfRangeException(nameof(headDim));
		int blocks = headDim / 16;
		ref float row = ref MemoryMarshal.GetReference(values);
		ref float r1 = ref MemoryMarshal.GetReference(scores1);
		nuint second = (nuint)headDim;
		Vector512<float> a0 = default, a1 = default, a2 = default, a3 = default;
		Vector512<float> b0 = default, b1 = default, b2 = default, b3 = default;
		for (int s = 0; s < scores0.Length; s++)
		{
			var x0 = Vector512.Create(scores0[s]);
			var x1 = Vector512.Create(Unsafe.Add(ref r1, s));
			a0 = a0 + x0 * Vector512.LoadUnsafe(ref row);
			a1 = a1 + x0 * Vector512.LoadUnsafe(ref row, 16);
			b0 = b0 + x1 * Vector512.LoadUnsafe(ref row, second);
			b1 = b1 + x1 * Vector512.LoadUnsafe(ref row, second + 16);
			if (blocks > 2)
			{
				a2 = a2 + x0 * Vector512.LoadUnsafe(ref row, 32);
				b2 = b2 + x1 * Vector512.LoadUnsafe(ref row, second + 32);
			}
			if (blocks > 3)
			{
				a3 = a3 + x0 * Vector512.LoadUnsafe(ref row, 48);
				b3 = b3 + x1 * Vector512.LoadUnsafe(ref row, second + 48);
			}
			row = ref Unsafe.Add(ref row, (nuint)stride);
		}
		ref float o = ref MemoryMarshal.GetReference(output);
		a0.StoreUnsafe(ref o);
		a1.StoreUnsafe(ref o, 16);
		b0.StoreUnsafe(ref o, second);
		b1.StoreUnsafe(ref o, second + 16);
		if (blocks > 2)
		{
			a2.StoreUnsafe(ref o, 32);
			b2.StoreUnsafe(ref o, second + 32);
		}
		if (blocks > 3)
		{
			a3.StoreUnsafe(ref o, 48);
			b3.StoreUnsafe(ref o, second + 48);
		}
	}

	/// <summary>Attention's weighted values, summing keys in order with separate multiply and add.</summary>
	public static void AttentionValues(ReadOnlySpan<float> scores, ReadOnlySpan<float> values, int stride, Span<float> output)
	{
		ref float rv = ref MemoryMarshal.GetReference(values);
		ref float ro = ref MemoryMarshal.GetReference(output);
		// Share each score load across four independent accumulators without changing the sum order.
		// Each lane is one output column, so wider vectors keep every column's sum order.
		int c = 0;
		if (Vector512.IsHardwareAccelerated)
		{
			for (; c + 64 <= output.Length; c += 64)
			{
				Vector512<float> acc0 = default, acc1 = default, acc2 = default, acc3 = default;
				for (int s = 0; s < scores.Length; s++)
				{
					var score = Vector512.Create(scores[s]);
					nuint offset = (nuint)(s * stride + c);
					acc0 = acc0 + score * Vector512.LoadUnsafe(ref rv, offset);
					acc1 = acc1 + score * Vector512.LoadUnsafe(ref rv, offset + 16);
					acc2 = acc2 + score * Vector512.LoadUnsafe(ref rv, offset + 32);
					acc3 = acc3 + score * Vector512.LoadUnsafe(ref rv, offset + 48);
				}
				acc0.StoreUnsafe(ref ro, (nuint)c);
				acc1.StoreUnsafe(ref ro, (nuint)c + 16);
				acc2.StoreUnsafe(ref ro, (nuint)c + 32);
				acc3.StoreUnsafe(ref ro, (nuint)c + 48);
			}
		}
		if (Vector256.IsHardwareAccelerated)
		{
			for (; c + 32 <= output.Length; c += 32)
			{
				Vector256<float> acc0 = default, acc1 = default, acc2 = default, acc3 = default;
				for (int s = 0; s < scores.Length; s++)
				{
					var score = Vector256.Create(scores[s]);
					nuint offset = (nuint)(s * stride + c);
					acc0 = acc0 + score * Vector256.LoadUnsafe(ref rv, offset);
					acc1 = acc1 + score * Vector256.LoadUnsafe(ref rv, offset + 8);
					acc2 = acc2 + score * Vector256.LoadUnsafe(ref rv, offset + 16);
					acc3 = acc3 + score * Vector256.LoadUnsafe(ref rv, offset + 24);
				}
				acc0.StoreUnsafe(ref ro, (nuint)c);
				acc1.StoreUnsafe(ref ro, (nuint)c + 8);
				acc2.StoreUnsafe(ref ro, (nuint)c + 16);
				acc3.StoreUnsafe(ref ro, (nuint)c + 24);
			}
		}
		// A whole head of 64 or 48 columns at once: 16 or 12 independent sums (ARM's only width).
		for (; c + 64 <= output.Length; c += 64)
		{
			Vector128<float> acc0 = default, acc1 = default, acc2 = default, acc3 = default, acc4 = default, acc5 = default, acc6 = default, acc7 = default, acc8 = default, acc9 = default, acc10 = default, acc11 = default, acc12 = default, acc13 = default, acc14 = default, acc15 = default;
			for (int s = 0; s < scores.Length; s++)
			{
				var score = Vector128.Create(scores[s]);
				nuint offset = (nuint)(s * stride + c);
				acc0 = acc0 + score * Vector128.LoadUnsafe(ref rv, offset);
				acc1 = acc1 + score * Vector128.LoadUnsafe(ref rv, offset + 4);
				acc2 = acc2 + score * Vector128.LoadUnsafe(ref rv, offset + 8);
				acc3 = acc3 + score * Vector128.LoadUnsafe(ref rv, offset + 12);
				acc4 = acc4 + score * Vector128.LoadUnsafe(ref rv, offset + 16);
				acc5 = acc5 + score * Vector128.LoadUnsafe(ref rv, offset + 20);
				acc6 = acc6 + score * Vector128.LoadUnsafe(ref rv, offset + 24);
				acc7 = acc7 + score * Vector128.LoadUnsafe(ref rv, offset + 28);
				acc8 = acc8 + score * Vector128.LoadUnsafe(ref rv, offset + 32);
				acc9 = acc9 + score * Vector128.LoadUnsafe(ref rv, offset + 36);
				acc10 = acc10 + score * Vector128.LoadUnsafe(ref rv, offset + 40);
				acc11 = acc11 + score * Vector128.LoadUnsafe(ref rv, offset + 44);
				acc12 = acc12 + score * Vector128.LoadUnsafe(ref rv, offset + 48);
				acc13 = acc13 + score * Vector128.LoadUnsafe(ref rv, offset + 52);
				acc14 = acc14 + score * Vector128.LoadUnsafe(ref rv, offset + 56);
				acc15 = acc15 + score * Vector128.LoadUnsafe(ref rv, offset + 60);
			}
			acc0.StoreUnsafe(ref ro, (nuint)c);
			acc1.StoreUnsafe(ref ro, (nuint)c + 4);
			acc2.StoreUnsafe(ref ro, (nuint)c + 8);
			acc3.StoreUnsafe(ref ro, (nuint)c + 12);
			acc4.StoreUnsafe(ref ro, (nuint)c + 16);
			acc5.StoreUnsafe(ref ro, (nuint)c + 20);
			acc6.StoreUnsafe(ref ro, (nuint)c + 24);
			acc7.StoreUnsafe(ref ro, (nuint)c + 28);
			acc8.StoreUnsafe(ref ro, (nuint)c + 32);
			acc9.StoreUnsafe(ref ro, (nuint)c + 36);
			acc10.StoreUnsafe(ref ro, (nuint)c + 40);
			acc11.StoreUnsafe(ref ro, (nuint)c + 44);
			acc12.StoreUnsafe(ref ro, (nuint)c + 48);
			acc13.StoreUnsafe(ref ro, (nuint)c + 52);
			acc14.StoreUnsafe(ref ro, (nuint)c + 56);
			acc15.StoreUnsafe(ref ro, (nuint)c + 60);
		}
		for (; c + 48 <= output.Length; c += 48)
		{
			Vector128<float> acc0 = default, acc1 = default, acc2 = default, acc3 = default, acc4 = default, acc5 = default, acc6 = default, acc7 = default, acc8 = default, acc9 = default, acc10 = default, acc11 = default;
			for (int s = 0; s < scores.Length; s++)
			{
				var score = Vector128.Create(scores[s]);
				nuint offset = (nuint)(s * stride + c);
				acc0 = acc0 + score * Vector128.LoadUnsafe(ref rv, offset);
				acc1 = acc1 + score * Vector128.LoadUnsafe(ref rv, offset + 4);
				acc2 = acc2 + score * Vector128.LoadUnsafe(ref rv, offset + 8);
				acc3 = acc3 + score * Vector128.LoadUnsafe(ref rv, offset + 12);
				acc4 = acc4 + score * Vector128.LoadUnsafe(ref rv, offset + 16);
				acc5 = acc5 + score * Vector128.LoadUnsafe(ref rv, offset + 20);
				acc6 = acc6 + score * Vector128.LoadUnsafe(ref rv, offset + 24);
				acc7 = acc7 + score * Vector128.LoadUnsafe(ref rv, offset + 28);
				acc8 = acc8 + score * Vector128.LoadUnsafe(ref rv, offset + 32);
				acc9 = acc9 + score * Vector128.LoadUnsafe(ref rv, offset + 36);
				acc10 = acc10 + score * Vector128.LoadUnsafe(ref rv, offset + 40);
				acc11 = acc11 + score * Vector128.LoadUnsafe(ref rv, offset + 44);
			}
			acc0.StoreUnsafe(ref ro, (nuint)c);
			acc1.StoreUnsafe(ref ro, (nuint)c + 4);
			acc2.StoreUnsafe(ref ro, (nuint)c + 8);
			acc3.StoreUnsafe(ref ro, (nuint)c + 12);
			acc4.StoreUnsafe(ref ro, (nuint)c + 16);
			acc5.StoreUnsafe(ref ro, (nuint)c + 20);
			acc6.StoreUnsafe(ref ro, (nuint)c + 24);
			acc7.StoreUnsafe(ref ro, (nuint)c + 28);
			acc8.StoreUnsafe(ref ro, (nuint)c + 32);
			acc9.StoreUnsafe(ref ro, (nuint)c + 36);
			acc10.StoreUnsafe(ref ro, (nuint)c + 40);
			acc11.StoreUnsafe(ref ro, (nuint)c + 44);
		}
		for (; c + 16 <= output.Length; c += 16)
		{
			Vector128<float> acc0 = default, acc1 = default, acc2 = default, acc3 = default;
			for (int s = 0; s < scores.Length; s++)
			{
				var score = Vector128.Create(scores[s]);
				nuint offset = (nuint)(s * stride + c);
				acc0 = acc0 + score * Vector128.LoadUnsafe(ref rv, offset);
				acc1 = acc1 + score * Vector128.LoadUnsafe(ref rv, offset + 4);
				acc2 = acc2 + score * Vector128.LoadUnsafe(ref rv, offset + 8);
				acc3 = acc3 + score * Vector128.LoadUnsafe(ref rv, offset + 12);
			}
			acc0.StoreUnsafe(ref ro, (nuint)c);
			acc1.StoreUnsafe(ref ro, (nuint)c + 4);
			acc2.StoreUnsafe(ref ro, (nuint)c + 8);
			acc3.StoreUnsafe(ref ro, (nuint)c + 12);
		}
		for (; c + 4 <= output.Length; c += 4)
		{
			var acc = Vector128<float>.Zero;
			for (int s = 0; s < scores.Length; s++)
				acc = acc + Vector128.Create(scores[s]) * Vector128.LoadUnsafe(ref rv, (nuint)(s * stride + c));
			acc.StoreUnsafe(ref ro, (nuint)c);
		}
		for (; c < output.Length; c++)
		{
			float sum = 0f;
			for (int s = 0; s < scores.Length; s++)
				sum += scores[s] * values[s * stride + c];
			output[c] = sum;
		}
	}

	/// <summary>Marian's LayerNormalizationImpl: sequential sums, then alpha * ((x - mean) / sigma) + beta.</summary>
	public static void LayerNorm(Span<float> row, ReadOnlySpan<float> scale, ReadOnlySpan<float> bias, float epsilon)
	{
		float sum = 0f;
		for (int i = 0; i < row.Length; i++)
			sum += row[i];
		float mean = sum / row.Length;
		float sqSum = 0f;
		for (int i = 0; i < row.Length; i++)
		{
			float e = row[i] - mean;
			sqSum += e * e;
		}
		float sigma = MathF.Sqrt(sqSum / row.Length + epsilon);

		ref float r = ref MemoryMarshal.GetReference(row);
		ref float s = ref MemoryMarshal.GetReference(scale);
		ref float b = ref MemoryMarshal.GetReference(bias);
		var meanVector = Vector128.Create(mean);
		var sigmaVector = Vector128.Create(sigma);
		int i4 = 0;
		for (; i4 + 4 <= row.Length; i4 += 4)
		{
			var t = Vector128.LoadUnsafe(ref s, (nuint)i4) * ((Vector128.LoadUnsafe(ref r, (nuint)i4) - meanVector) / sigmaVector);
			(t + Vector128.LoadUnsafe(ref b, (nuint)i4)).StoreUnsafe(ref r, (nuint)i4);
		}
		for (; i4 < row.Length; i4++)
			row[i4] = scale[i4] * ((row[i4] - mean) / sigma) + bias[i4];
	}

	/// <summary>
	/// SSRU cell update as Marian's float32x4 Element kernels compute it:
	/// s = e / (1 + e) with e = exp_ps(f); c = s * c + (1 - s) * x; h = relu(c), written to x.
	/// </summary>
	public static void Ssru(Span<float> cell, Span<float> x, ReadOnlySpan<float> f)
	{
		ref float rc = ref MemoryMarshal.GetReference(cell);
		ref float rx = ref MemoryMarshal.GetReference(x);
		ref float rf = ref MemoryMarshal.GetReference(f);
		int i = 0;
		for (; i + 4 <= cell.Length; i += 4)
		{
			var e = ExpPs(Vector128.LoadUnsafe(ref rf, (nuint)i));
			var s = e / (One + e);
			var c = s * Vector128.LoadUnsafe(ref rc, (nuint)i) + (One - s) * Vector128.LoadUnsafe(ref rx, (nuint)i);
			c.StoreUnsafe(ref rc, (nuint)i);
			Vector128.Max(Vector128<float>.Zero, c).StoreUnsafe(ref rx, (nuint)i);
		}
		for (; i < cell.Length; i++)
		{
			float e = ExpPs(f[i]);
			float s = e / (1f + e);
			cell[i] = s * cell[i] + (1f - s) * x[i];
			x[i] = MathF.Max(0f, cell[i]);
		}
	}

#if TARJAMOT_PLATFORM_MATH
	// The platform's libm: faster to build and smaller, but its last bits differ from Firefox.
	static float Expf(float x) => MathF.Exp(x);

	static float Sinf(float x) => MathF.Sin(x);

	static float Cosf(float x) => MathF.Cos(x);
#else
	static float Expf(float x) => MuslMath.Expf(x);

	static float Sinf(float x) => MuslMath.Sinf(x);

	static float Cosf(float x) => MuslMath.Cosf(x);
#endif

	/// <summary>
	/// Marian's sinusoidalPositionEmbeddings for one position, with musl's logf/expf/sinf/cosf.
	/// </summary>
	public static float[] PositionEmbedding(int position, int dim)
	{
		var result = new float[dim];
		float timescales = dim / 2;
		float logIncrement = (float)Math.Log(10000.0) / (timescales - 1f);
		for (int i = 0; i < (int)timescales; i++)
		{
			float v = position * Expf(i * -logIncrement);
			result[i] = Sinf(v);
			result[(int)timescales + i] = Cosf(v);
		}
		return result;
	}
}
