#if !TARJAMOT_PLATFORM_MATH
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

	// __rem_pio2f.c and the multiples of pi/2 in sinf.c / cosf.c
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

	public static float Sinf(float x)
	{
		uint ix = BitConverter.SingleToUInt32Bits(x);
		bool sign = ix >> 31 != 0;
		ix &= 0x7fffffff;
		if (ix <= 0x3f490fda)
			return ix < 0x39800000 ? x : SinKernel(x);
		if (ix <= 0x407b53d1)
		{
			if (ix <= 0x4016cbe3)
				return sign ? -CosKernel(x + Pio2) : CosKernel(x - Pio2);
			return SinKernel(sign ? -(x + 2 * Pio2) : -(x - 2 * Pio2));
		}
		if (ix <= 0x40e231d5)
		{
			if (ix <= 0x40afeddf)
				return sign ? CosKernel(x + 3 * Pio2) : -CosKernel(x - 3 * Pio2);
			return SinKernel(sign ? x + 4 * Pio2 : x - 4 * Pio2);
		}
		if (ix >= 0x7f800000)
			return x - x;
		int n = RemPio2f(x, out double y);
		return (n & 3) switch
		{
			0 => SinKernel(y),
			1 => CosKernel(y),
			2 => SinKernel(-y),
			_ => -CosKernel(y),
		};
	}

	public static float Cosf(float x)
	{
		uint ix = BitConverter.SingleToUInt32Bits(x);
		bool sign = ix >> 31 != 0;
		ix &= 0x7fffffff;
		if (ix <= 0x3f490fda)
			return ix < 0x39800000 ? 1f : CosKernel(x);
		if (ix <= 0x407b53d1)
		{
			if (ix > 0x4016cbe3)
				return -CosKernel(sign ? x + 2 * Pio2 : x - 2 * Pio2);
			return sign ? SinKernel(x + Pio2) : SinKernel(Pio2 - x);
		}
		if (ix <= 0x40e231d5)
		{
			if (ix > 0x40afeddf)
				return CosKernel(sign ? x + 4 * Pio2 : x - 4 * Pio2);
			return sign ? SinKernel(-x - 3 * Pio2) : SinKernel(x - 3 * Pio2);
		}
		if (ix >= 0x7f800000)
			return x - x;
		int n = RemPio2f(x, out double y);
		return (n & 3) switch
		{
			0 => CosKernel(y),
			1 => SinKernel(-y),
			2 => -CosKernel(y),
			_ => SinKernel(y),
		};
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
