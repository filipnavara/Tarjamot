using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;
using Tarjamot.Engine;
using NUnit.Framework;

namespace Tarjamot.Tests;

[TestFixture]
public class ModelLoadingTest
{
	[Test]
	public void RequantizationMatchesOriginalFloatPath([Values(3, 16, 512)] int cols, [Values(1f, 12.345f, 3000f)] float multiplier, [Values(0, 4, 16)] int panelWidth, [Values("all", "negative", "ties", "zero")] string distribution)
	{
		var data = new sbyte[257 * cols];
		for (int i = 0; i < data.Length; i++)
			data[i] = distribution switch { "negative" => (sbyte)(-1 - i % 127), "ties" => (sbyte)(i % 2 == 0 ? -127 : 127), "zero" => 0, _ => (sbyte)(i % 256 - 128) };
		var embeddings = QuantizedMatrix.Create(data, 257, cols, multiplier, 10f);
		var values = new float[data.Length];
		TensorPrimitives.ConvertChecked<sbyte, float>(data, values);
		TensorPrimitives.Multiply(values, 1f / multiplier, values);
		float quantMult = 127f / TensorPrimitives.MaxMagnitude<float>(values);
		var expected = QuantizedMatrix.Create(values.Select(value => (sbyte)Math.Clamp(MathF.Round(value * quantMult), -127f, 127f)).ToArray(), 257, cols, quantMult, 10f);
		AssertMatrix(expected.Pack(panelWidth), MarianWeights.CreateOutputWeights(embeddings, 10f, panelWidth));
	}

	// The layouts of the portable (row-major), ARM (4) and x86 (16) kernels, with and without the
	// saturating pairs, which only the x86 kernel can omit.
	[Test]
	public void StreamLoaderMatchesRawContractAndPacksOnlyDenseMatrices([Values(0, 4, 16)] int panelWidth, [Values] bool saturation)
	{
		var values = Enumerable.Range(0, 5 * 16).Select(i => (sbyte)(i * 37 % 256 - 128)).ToArray();
		using var stream = new MemoryStream();
		using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
		{
			writer.Write(1UL);
			writer.Write(4UL);
			foreach (var name in new[] { "Wemb", "encoder_l1_self_Wq", "encoder_l1_self_Wq_QuantMultA", "none_QuantMultA" })
			{
				writer.Write((ulong)name.Length + 1);
				writer.Write(name.EndsWith("QuantMultA") ? 0x0404UL : 0x4101UL);
				writer.Write(2UL);
				writer.Write(name.EndsWith("QuantMultA") ? 4UL : (ulong)values.Length + 4);
			}
			foreach (var name in new[] { "Wemb", "encoder_l1_self_Wq", "encoder_l1_self_Wq_QuantMultA", "none_QuantMultA" })
				writer.Write(Encoding.UTF8.GetBytes(name + '\0'));
			foreach (var shape in new[] { (5, 16), (16, 5), (1, 1), (1, 1) })
			{
				writer.Write(shape.Item1);
				writer.Write(shape.Item2);
			}
			writer.Write(7UL);
			writer.Write(new byte[7]);
			for (int i = 0; i < 2; i++)
			{
				writer.Write(MemoryMarshal.AsBytes(values.AsSpan()));
				writer.Write(123.45f);
			}
			writer.Write(67.89f);
			writer.Write(19.5f);
		}
		var raw = new MarianModel(stream.ToArray());
		stream.Position = 0;
		var loaded = new MarianModel(stream, panelWidth, saturation);
		Assert.That(stream.CanRead, Is.True);
		Assert.That(loaded.UnnamedAlpha, Is.EqualTo(raw.UnnamedAlpha));
		// Embeddings are only looked up, so they have neither row sums nor saturation metadata.
		AssertMatrix(raw.Matrix("Wemb"), loaded.Matrix("Wemb"), saturation: false, rowSums: false);
		var dense = raw.Matrix("encoder_l1_self_Wq");
		AssertMatrix(dense.Pack(panelWidth), loaded.Matrix("encoder_l1_self_Wq"), saturation);
		// Without a layout, the loader uses this machine's kernel.
		stream.Position = 0;
		AssertMatrix(dense.Pack(Int8Gemm.PanelWidth), new MarianModel(stream).Matrix("encoder_l1_self_Wq"), Int8Gemm.UsesSaturationMetadata);
	}

	[Test]
	public void OutputWeightsWithoutSaturationMetadataKeepTheRest([Values(0, 16)] int panelWidth)
	{
		var data = Enumerable.Range(0, 33 * 64).Select(i => (sbyte)(i * 37 % 256 - 128)).ToArray();
		var embeddings = QuantizedMatrix.Create(data, 33, 64, 12.5f, 10f);
		AssertMatrix(MarianWeights.CreateOutputWeights(embeddings, 10f, panelWidth), MarianWeights.CreateOutputWeights(embeddings, 10f, panelWidth, saturation: false), saturation: false);
	}

	[Test]
	public void X86ProductsDoNotNeedSaturationMetadata([Values(0, 16)] int panelWidth, [Values] bool selected)
	{
		if (!Avx2.IsSupported)
			Assert.Ignore("x86 AVX2 instructions are unavailable.");
		if (Int8Gemm.UsesSaturationMetadata)
			Assert.Ignore("With AVX-VNNI and without AVX-512, the x86 kernel computes exact sums and corrects the saturating pairs.");
		if (selected && panelWidth != 0)
			Assert.Ignore("Selected rows use row-major weights.");
		// Large same-sign weights make pairs saturate.
		var data = Enumerable.Range(0, 37 * 64).Select(i => (sbyte)(i % 5 == 0 ? 127 : i * 37 % 256 - 128)).ToArray();
		var embeddings = QuantizedMatrix.Create(data, 37, 64, 12.5f, 10f);
		var full = MarianWeights.CreateOutputWeights(embeddings, 60f, panelWidth);
		var bare = MarianWeights.CreateOutputWeights(embeddings, 60f, panelWidth, saturation: false);
		Assert.That(full.SaturationPairs, Is.Not.Empty);
		var random = new Random(3);
		var x = Enumerable.Range(0, 5 * 64).Select(_ => (float)(random.NextDouble() * 4 - 2)).ToArray();
		var activations = new QuantizedActivations();
		Int8Gemm.Quantize(x, 60f, activations);
		var bias = Int8Gemm.PrepareBias(full, 60f, null);
		int[]? rows = selected ? [36, 0, 5, 5, 20] : null;
		int outputs = rows?.Length ?? 37;
		var expected = new float[5 * outputs];
		var actual = new float[5 * outputs];
		Int8Gemm.Multiply(activations, 5, full, rows, 1f / 750f, bias, expected, false);
		Int8Gemm.Multiply(activations, 5, bare, rows, 1f / 750f, bias, actual, false);
		Assert.That(actual.Select(BitConverter.SingleToInt32Bits), Is.EqualTo(expected.Select(BitConverter.SingleToInt32Bits)));
		if (!selected)
		{
			var winners = Int8Gemm.MultiplyArgMax(activations, 5, full, 1f / 750f, bias, -1, new ArgMaxScratch(), false).ToArray();
			Assert.That(Int8Gemm.MultiplyArgMax(activations, 5, bare, 1f / 750f, bias, -1, new ArgMaxScratch(), false).ToArray(), Is.EqualTo(winners));
		}
	}

	[Test]
	public void ProductsRejectLookupOnlyMatrices()
	{
		var data = new sbyte[20 * 32];
		var embeddings = QuantizedMatrix.CreateRows(20, 32, 1f, 1f, 0, (row, r) => data.AsSpan(r * 32, 32).CopyTo(row), saturation: false, rowSums: false);
		var activations = new QuantizedActivations();
		Int8Gemm.Quantize(new float[32], 1f, activations);
		Assert.Throws<NotSupportedException>(() => Int8Gemm.PrepareBias(embeddings, 1f, null));
		Assert.Throws<NotSupportedException>(() => Int8Gemm.Multiply(activations, 1, embeddings, null, 1f, new float[20], new float[20], false));
		Assert.Throws<NotSupportedException>(() => Int8Gemm.MultiplyArgMax(activations, 1, embeddings, 1f, new float[20], -1, new ArgMaxScratch(), false));
		// Building the output layer from the embeddings needs only their values.
		Assert.That(MarianWeights.CreateOutputWeights(embeddings, 1f).HasRowSums, Is.True);
	}

	[Test]
	public void KernelsThatCorrectSaturationRejectMatricesWithoutMetadata()
	{
		if (!Int8Gemm.UsesSaturationMetadata)
			Assert.Ignore("The x86 kernel saturates the pairs itself.");
		var embeddings = QuantizedMatrix.Create(new sbyte[20 * 32], 20, 32, 1f, 1f);
		var weights = MarianWeights.CreateOutputWeights(embeddings, 1f, Int8Gemm.PanelWidth, saturation: false);
		var activations = new QuantizedActivations();
		Int8Gemm.Quantize(new float[32], 1f, activations);
		var bias = Int8Gemm.PrepareBias(weights, 1f, null);
		Assert.Throws<NotSupportedException>(() => Int8Gemm.Multiply(activations, 1, weights, null, 1f, bias, new float[20], false));
		Assert.Throws<NotSupportedException>(() => Int8Gemm.MultiplyArgMax(activations, 1, weights, 1f, bias, -1, new ArgMaxScratch(), false));
	}

	[Test]
	public void StreamLoaderRejectsTruncatedPayloadAndUndersizedTensor()
	{
		using var stream = new MemoryStream();
		using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
		{
			writer.Write(1UL);
			writer.Write(1UL);
			writer.Write(5UL);
			writer.Write(0x4101UL);
			writer.Write(2UL);
			writer.Write(20UL);
			writer.Write(Encoding.UTF8.GetBytes("Wemb\0"));
			writer.Write(1);
			writer.Write(16);
			writer.Write(0UL);
			writer.Write(new byte[16]);
			writer.Write(1f);
		}
		var data = stream.ToArray();
		Assert.Throws<InvalidDataException>(() => new MarianModel(new MemoryStream(data[..^1])));
		BitConverter.GetBytes(19UL).CopyTo(data, 40);
		Assert.Throws<InvalidDataException>(() => new MarianModel(new MemoryStream(data)));
		BitConverter.GetBytes(0x0404UL).CopyTo(data, 24);
		Assert.Throws<InvalidDataException>(() => new MarianModel(new MemoryStream(data)));
		BitConverter.GetBytes(20UL).CopyTo(data, 40);
		Assert.Throws<InvalidDataException>(() => new MarianModel(new MemoryStream(data)));
	}

	static void AssertMatrix(QuantizedMatrix expected, QuantizedMatrix actual, bool saturation = true, bool rowSums = true)
	{
		Assert.Multiple(() =>
		{
			Assert.That(actual.Data, Is.EqualTo(expected.Data));
			Assert.That(actual.Rows, Is.EqualTo(expected.Rows));
			Assert.That(actual.Cols, Is.EqualTo(expected.Cols));
			Assert.That(BitConverter.SingleToInt32Bits(actual.QuantMult), Is.EqualTo(BitConverter.SingleToInt32Bits(expected.QuantMult)));
			Assert.That(actual.Alpha, Is.EqualTo(expected.Alpha));
			Assert.That(actual.PanelWidth, Is.EqualTo(expected.PanelWidth));
			Assert.That(actual.RowSums, rowSums ? Is.EqualTo(expected.RowSums) : Is.Empty);
			Assert.That(actual.HasRowSums, Is.EqualTo(rowSums));
			Assert.That(actual.SaturationOffsets, saturation ? Is.EqualTo(expected.SaturationOffsets) : Is.Empty);
			Assert.That(actual.SaturationPairs, saturation ? Is.EqualTo(expected.SaturationPairs) : Is.Empty);
			Assert.That(actual.HasSaturationMetadata, Is.EqualTo(saturation));
		});
	}
}
