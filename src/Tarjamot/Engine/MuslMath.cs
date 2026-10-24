#if !TARJAMOT_PLATFORM_MATH
using System.Runtime.Intrinsics;

namespace Tarjamot.Engine;

/// <summary>
/// Ports of the musl libm single-precision functions that Firefox's Bergamot WASM build links
/// (emscripten's libc). They compute in double without fused multiply-add, so the same operations
/// in .NET give bit-identical results; the platform's expf/sinf/cosf differ in the last bit.
/// Constants are given by their bit patterns, as in the C sources' hex literals.
/// </summary>
internal static class MuslMath
{
	// expf.c / exp2f_data.c (EXP2F_TABLE_BITS = 5, no TOINT_INTRINSICS on WASM).
	const int TableBits = 5;
	const int N = 1 << TableBits;
	static readonly double InvLn2N = BitConverter.Int64BitsToDouble(0x40471547652b82fe);
	static readonly double Shift = BitConverter.Int64BitsToDouble(0x4338000000000000);
	static readonly double ExpC0 = BitConverter.Int64BitsToDouble(0x3ebc6af84b912394);
	static readonly double ExpC1 = BitConverter.Int64BitsToDouble(0x3f2ebfce50fac4f3);
	static readonly double ExpC2 = BitConverter.Int64BitsToDouble(0x3f962e42ff0c52d6);
	static readonly ulong[] ExpTable =
	[
		0x3ff0000000000000, 0x3fefd9b0d3158574, 0x3fefb5586cf9890f, 0x3fef9301d0125b51,
		0x3fef72b83c7d517b, 0x3fef54873168b9aa, 0x3fef387a6e756238, 0x3fef1e9df51fdee1,
		0x3fef06fe0a31b715, 0x3feef1a7373aa9cb, 0x3feedea64c123422, 0x3feece086061892d,
		0x3feebfdad5362a27, 0x3feeb42b569d4f82, 0x3feeab07dd485429, 0x3feea47eb03a5585,
		0x3feea09e667f3bcd, 0x3fee9f75e8ec5f74, 0x3feea11473eb0187, 0x3feea589994cce13,
		0x3feeace5422aa0db, 0x3feeb737b0cdc5e5, 0x3feec49182a3f090, 0x3feed503b23e255d,
		0x3feee89f995ad3ad, 0x3feeff76f2fb5e47, 0x3fef199bdd85529c, 0x3fef3720dcef9069,
		0x3fef5818dcfba487, 0x3fef7c97337b9b5f, 0x3fefa4afa2a490da, 0x3fefd0765b6e4540,
	];

	// __sindf.c / __cosdf.c
	static readonly double S1 = BitConverter.Int64BitsToDouble(unchecked((long)0xbfc5555554cbac77));
	static readonly double S2 = BitConverter.Int64BitsToDouble(0x3f811110896efbb2);
	static readonly double S3 = BitConverter.Int64BitsToDouble(unchecked((long)0xbf2a00f9e2cae774));
	static readonly double S4 = BitConverter.Int64BitsToDouble(0x3ec6cd878c3b46a7);
	static readonly double K0 = BitConverter.Int64BitsToDouble(unchecked((long)0xbfdffffffd0c5e81));
	static readonly double K1 = BitConverter.Int64BitsToDouble(0x3fa55553e1053a42);
	static readonly double K2 = BitConverter.Int64BitsToDouble(unchecked((long)0xbf56c087e80f1e27));
	static readonly double K3 = BitConverter.Int64BitsToDouble(0x3ef99342e0ee5069);

	// __rem_pio2f.c and the multiples of pi/2 in sincosf.c
	const double ToInt = 6755399441055744.0; // 1.5 / DBL_EPSILON
	static readonly double Pio4 = BitConverter.Int64BitsToDouble(0x3fe921fb60000000);
	static readonly double InvPio2 = BitConverter.Int64BitsToDouble(0x3fe45f306dc9c883);
	static readonly double Pio2_1 = BitConverter.Int64BitsToDouble(0x3ff921fb50000000);
	static readonly double Pio2_1t = BitConverter.Int64BitsToDouble(0x3e5110b4611a6263);
	static readonly double Pio2 = BitConverter.Int64BitsToDouble(0x3ff921fb54442d18);

	public static float Expf(float x)
	{
		double xd = x;
		uint abstop = (BitConverter.SingleToUInt32Bits(x) >> 20) & 0x7ff;
		if (abstop >= (BitConverter.SingleToUInt32Bits(88f) >> 20))
		{
			if (float.IsNegativeInfinity(x))
				return 0f;
			if (abstop >= (BitConverter.SingleToUInt32Bits(float.PositiveInfinity) >> 20))
				return x + x;
			if (x > 88.72283172607422f)
				return float.PositiveInfinity;
			if (x < -103.97207641601562f)
				return 0f;
		}
		double z = InvLn2N * xd;
		double kd = z + Shift;
		ulong ki = BitConverter.DoubleToUInt64Bits(kd);
		kd -= Shift;
		double r = z - kd;
		ulong t = ExpTable[ki % N];
		t += ki << (52 - TableBits);
		double s = BitConverter.UInt64BitsToDouble(t);
		z = ExpC0 * r + ExpC1;
		double r2 = r * r;
		double y = ExpC2 * r + 1;
		y = z * r2 + y;
		y = y * s;
		return (float)y;
	}

	/// <summary>
	/// musl's sincosf: the bits of sinf and cosf, with one range check and argument reduction.
	/// </summary>
	public static void SinCosf(float x, out float sin, out float cos)
	{
		uint ix = BitConverter.SingleToUInt32Bits(x);
		bool sign = ix >> 31 != 0;
		ix &= 0x7fffffff;
		if (ix <= 0x3f490fda)
		{
			if (ix < 0x39800000)
			{
				sin = x;
				cos = 1f;
				return;
			}
			sin = SinKernel(x);
			cos = CosKernel(x);
			return;
		}
		if (ix <= 0x407b53d1)
		{
			if (ix <= 0x4016cbe3)
			{
				if (sign)
				{
					sin = -CosKernel(x + Pio2);
					cos = SinKernel(x + Pio2);
				}
				else
				{
					sin = CosKernel(Pio2 - x);
					cos = SinKernel(Pio2 - x);
				}
				return;
			}
			sin = -SinKernel(sign ? x + 2 * Pio2 : x - 2 * Pio2);
			cos = -CosKernel(sign ? x + 2 * Pio2 : x - 2 * Pio2);
			return;
		}
		if (ix <= 0x40e231d5)
		{
			if (ix <= 0x40afeddf)
			{
				if (sign)
				{
					sin = CosKernel(x + 3 * Pio2);
					cos = -SinKernel(x + 3 * Pio2);
				}
				else
				{
					sin = -CosKernel(x - 3 * Pio2);
					cos = SinKernel(x - 3 * Pio2);
				}
				return;
			}
			sin = SinKernel(sign ? x + 4 * Pio2 : x - 4 * Pio2);
			cos = CosKernel(sign ? x + 4 * Pio2 : x - 4 * Pio2);
			return;
		}
		if (ix >= 0x7f800000)
		{
			sin = cos = x - x;
			return;
		}
		int n = RemPio2f(x, out double y);
		float s = SinKernel(y), c = CosKernel(y);
		(sin, cos) = (n & 3) switch
		{
			0 => (s, c),
			1 => (c, -s),
			2 => (-s, -c),
			_ => (-c, s),
		};
	}

	/// <summary>
	/// <see cref="SinCosf(float, out float, out float)"/> on four non-negative values below
	/// 2^28 * pi/2 (where __rem_pio2f takes its large-argument path), with the same bits. Each lane takes musl's reduction for its
	/// range: none below pi/4, x - k * pi/2 in one step up to 9pi/4, __rem_pio2f above. Both kernels
	/// run on the reduced value, and the quadrant picks and negates the results.
	/// </summary>
	public static void SinCosf(Vector128<float> x, out Vector128<float> sin, out Vector128<float> cos)
	{
		var ix = x.AsUInt32();
		// Also rejects negative values and NaNs, whose sign or exponent bits are above the limit.
		if (Vector128.GreaterThanOrEqualAny(ix, Vector128.Create(0x4dc90fdbu)))
			throw new ArgumentOutOfRangeException(nameof(x), "Negative or large arguments are not supported.");
		// The multiple of pi/2 that the mid-range branches subtract (0 for |x| <= pi/4).
		var k = (Vector128.GreaterThan(ix, Vector128.Create(0x3f490fdau)).AsSingle() & Vector128<float>.One)
			+ (Vector128.GreaterThan(ix, Vector128.Create(0x4016cbe3u)).AsSingle() & Vector128<float>.One)
			+ (Vector128.GreaterThan(ix, Vector128.Create(0x407b53d1u)).AsSingle() & Vector128<float>.One)
			+ (Vector128.GreaterThan(ix, Vector128.Create(0x40afeddfu)).AsSingle() & Vector128<float>.One);
		var large = Vector128.GreaterThan(ix, Vector128.Create(0x40e231d5u)).AsInt32();
		var (xLo, xHi) = Vector128.Widen(x);
		var (kLo, kHi) = Vector128.Widen(k);
		var (largeLo, largeHi) = Vector128.Widen(large);
		var (yLo, nLo) = Reduce(xLo, kLo, largeLo.AsDouble());
		var (yHi, nHi) = Reduce(xHi, kHi, largeHi.AsDouble());
		var s = Vector128.Narrow(SinKernel(yLo), SinKernel(yHi));
		var c = Vector128.Narrow(CosKernel(yLo), CosKernel(yHi));
		var n = Vector128.Narrow(nLo, nHi).AsUInt32();
		var swap = Vector128.Equals(n & Vector128<uint>.One, Vector128<uint>.One).AsSingle();
		// Negate sin in quadrants 2 and 3, cos in quadrants 1 and 2.
		var sinSign = (n & Vector128.Create(2u)) << 30;
		var cosSign = ((n + Vector128<uint>.One) & Vector128.Create(2u)) << 30;
		var tiny = Vector128.LessThan(ix, Vector128.Create(0x39800000u)).AsSingle();
		sin = Vector128.ConditionalSelect(tiny, x, Vector128.ConditionalSelect(swap, c, s) ^ sinSign.AsSingle());
		cos = Vector128.ConditionalSelect(tiny, Vector128<float>.One, Vector128.ConditionalSelect(swap, s, c) ^ cosSign.AsSingle());
	}

	// The reduced argument and the quadrant (in the low bits) of two lanes.
	static (Vector128<double> Y, Vector128<long> N) Reduce(Vector128<double> x, Vector128<double> k, Vector128<double> large)
	{
		var yMid = x - k * Vector128.Create(Pio2);
		// __rem_pio2f, with its correction by one when y is off by more than pi/4.
		var fn = x * Vector128.Create(InvPio2) + Vector128.Create(ToInt) - Vector128.Create(ToInt);
		var yLarge = x - fn * Vector128.Create(Pio2_1) - fn * Vector128.Create(Pio2_1t);
		fn = fn + (Vector128.GreaterThan(yLarge, Vector128.Create(Pio4)) & Vector128<double>.One)
			- (Vector128.LessThan(yLarge, Vector128.Create(-Pio4)) & Vector128<double>.One);
		yLarge = x - fn * Vector128.Create(Pio2_1) - fn * Vector128.Create(Pio2_1t);
		// Adding 1.5 * 2^52 puts the integer in the low mantissa bits.
		var n = (Vector128.ConditionalSelect(large, fn, k) + Vector128.Create(ToInt)).AsInt64();
		return (Vector128.ConditionalSelect(large, yLarge, yMid), n);
	}

	static float SinKernel(double x)
	{
		double z = x * x;
		double w = z * z;
		double r = S3 + z * S4;
		double s = z * x;
		return (float)((x + s * (S1 + z * S2)) + s * w * r);
	}

	static float CosKernel(double x)
	{
		double z = x * x;
		double w = z * z;
		double r = K2 + z * K3;
		return (float)(((1.0 + z * K0) + w * K1) + (w * z) * r);
	}

	static Vector128<double> SinKernel(Vector128<double> x)
	{
		var z = x * x;
		var w = z * z;
		var r = Vector128.Create(S3) + z * Vector128.Create(S4);
		var s = z * x;
		return (x + s * (Vector128.Create(S1) + z * Vector128.Create(S2))) + s * w * r;
	}

	static Vector128<double> CosKernel(Vector128<double> x)
	{
		var z = x * x;
		var w = z * z;
		var r = Vector128.Create(K2) + z * Vector128.Create(K3);
		return ((Vector128<double>.One + z * Vector128.Create(K0)) + w * Vector128.Create(K1)) + (w * z) * r;
	}

	// Medium-size argument reduction; positions in a sentence never need the large-argument path.
	static int RemPio2f(float x, out double y)
	{
		uint ix = BitConverter.SingleToUInt32Bits(x) & 0x7fffffff;
		if (ix >= 0x4dc90fdb)
			throw new ArgumentOutOfRangeException(nameof(x), "Large arguments are not supported.");
		double fn = (double)x * InvPio2 + ToInt - ToInt;
		int n = (int)fn;
		y = x - fn * Pio2_1 - fn * Pio2_1t;
		if (y < -Pio4)
		{
			n--;
			fn--;
			y = x - fn * Pio2_1 - fn * Pio2_1t;
		}
		else if (y > Pio4)
		{
			n++;
			fn++;
			y = x - fn * Pio2_1 - fn * Pio2_1t;
		}
		return n;
	}
}
#endif
