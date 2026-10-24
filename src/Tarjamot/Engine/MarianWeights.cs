using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Tarjamot.Engine;

/// <summary>
/// The parameters of a loaded model, shared by all <see cref="MarianTranslator"/> workers. Nothing
/// changes after construction except the position table, which grows under a lock.
/// </summary>
internal sealed partial class MarianWeights
{
	internal sealed record AttentionLayer(QuantizedMatrix Wq, Tensor Bq, QuantizedMatrix Wk, Tensor Bk, QuantizedMatrix Wv, Tensor Bv, QuantizedMatrix Wo, Tensor Bo, Tensor LnScale, Tensor LnBias);

	internal sealed record FeedForwardLayer(QuantizedMatrix W1, Tensor B1, QuantizedMatrix W2, Tensor B2, Tensor LnScale, Tensor LnBias);

	internal sealed record EncoderLayer(AttentionLayer Self, FeedForwardLayer FeedForward);

	internal sealed record DecoderLayer(QuantizedMatrix RnnW, QuantizedMatrix RnnWf, Tensor RnnBf, Tensor RnnLnScale, Tensor RnnLnBias, AttentionLayer Context, FeedForwardLayer FeedForward);

	readonly Dictionary<QuantizedMatrix, float[]> preparedBiases = new(ReferenceEqualityComparer.Instance);
	readonly Lock positionLock = new();
	volatile float[][] positions = [];

	public MarianWeights(MarianModel model, Int8Kernel kernel, bool useShortlist)
	{
		bool shared = model.HasMatrix("Wemb");
		SourceEmbeddings = model.Matrix(shared ? "Wemb" : "encoder_Wemb");
		TargetEmbeddings = model.Matrix(shared ? "Wemb" : "decoder_Wemb");
		OutputBias = model["decoder_ff_logit_out_b"];
		// Marian's output layer fetches the activation alpha by the weight node's name (the target
		// embeddings) for the full vocabulary, the unnamed ("none") shortlist selection otherwise.
		// A lexical shortlist gathers scattered weight rows, which are faster in row-major
		// layout. Keep only the layout used by this engine, rather than caching both copies.
		OutputWeights = CreateOutputWeights(TargetEmbeddings, TargetEmbeddings.Alpha, useShortlist ? 0 : Int8Gemm.PanelWidth(kernel), Int8Gemm.UsesSaturationMetadata(kernel));
		OutputWeightsShortlist = OutputWeights with { Alpha = model.UnnamedAlpha };
		Dim = TargetEmbeddings.Cols;
		var headsMatch = HeadsRegex().Match(model.Config);
		Heads = headsMatch.Success ? int.Parse(headsMatch.Groups[1].Value) : 8;

		EncoderLayers = Enumerable.Range(1, model.CountLayers("encoder"))
			.Select(l => new EncoderLayer(LoadAttention(model, $"encoder_l{l}_self"), LoadFeedForward(model, $"encoder_l{l}_ffn")))
			.ToArray();
		DecoderLayers = Enumerable.Range(1, model.CountLayers("decoder"))
			.Select(l => new DecoderLayer(
				model.Matrix($"decoder_l{l}_rnn_W"),
				model.Matrix($"decoder_l{l}_rnn_Wf"),
				model[$"decoder_l{l}_rnn_bf"],
				model[$"decoder_l{l}_rnn_ffn_ln_scale"],
				model[$"decoder_l{l}_rnn_ffn_ln_bias"],
				LoadAttention(model, $"decoder_l{l}_context"),
				LoadFeedForward(model, $"decoder_l{l}_ffn")))
			.ToArray();

		// Prepared biases of the products with a precomputed alpha, computed once so that workers
		// only read them.
		foreach (var layer in EncoderLayers)
		{
			Prepare(layer.Self);
			Prepare(layer.FeedForward);
		}
		foreach (var layer in DecoderLayers)
		{
			Prepare(layer.RnnW, null);
			Prepare(layer.RnnWf, layer.RnnBf);
			Prepare(layer.Context);
			Prepare(layer.FeedForward);
		}
		// Only the output layer this engine uses: the full vocabulary or the shortlist.
		Prepare(useShortlist ? OutputWeightsShortlist : OutputWeights, OutputBias);
	}

	public QuantizedMatrix SourceEmbeddings { get; }
	public QuantizedMatrix TargetEmbeddings { get; }
	public QuantizedMatrix OutputWeights { get; }
	public QuantizedMatrix OutputWeightsShortlist { get; }
	public Tensor OutputBias { get; }
	public EncoderLayer[] EncoderLayers { get; }
	public DecoderLayer[] DecoderLayers { get; }
	public int Dim { get; }
	public int Heads { get; }

	/// <summary>The prepared bias of a product whose activation alpha is precomputed.</summary>
	public float[] GetPreparedBias(QuantizedMatrix weights) => preparedBiases[weights];

	/// <summary>The sinusoidal position embedding of a position.</summary>
	public float[] GetPosition(int position)
	{
		var table = positions;
		if (position < table.Length)
			return table[position];
		lock (positionLock)
		{
			table = positions;
			if (position >= table.Length)
			{
				var grown = new float[Math.Max(position + 1, table.Length * 2)][];
				table.CopyTo(grown, 0);
				for (int p = table.Length; p < grown.Length; p++)
					grown[p] = MarianMath.PositionEmbedding(p, Dim);
				positions = table = grown;
			}
			return table[position];
		}
	}

	void Prepare(AttentionLayer layer)
	{
		Prepare(layer.Wq, layer.Bq);
		Prepare(layer.Wk, layer.Bk);
		Prepare(layer.Wv, layer.Bv);
		Prepare(layer.Wo, layer.Bo);
	}

	void Prepare(FeedForwardLayer layer)
	{
		Prepare(layer.W1, layer.B1);
		Prepare(layer.W2, layer.B2);
	}

	void Prepare(QuantizedMatrix weights, Tensor? bias)
	{
		if (weights.Alpha != 0f)
			preparedBiases[weights] = Int8Gemm.PrepareBias(weights, weights.Alpha, bias?.Data);
	}

	AttentionLayer LoadAttention(MarianModel model, string prefix) => new(
		model.Matrix(prefix + "_Wq"), model[prefix + "_bq"],
		model.Matrix(prefix + "_Wk"), model[prefix + "_bk"],
		model.Matrix(prefix + "_Wv"), model[prefix + "_bv"],
		model.Matrix(prefix + "_Wo"), model[prefix + "_bo"],
		model[prefix + "_Wo_ln_scale"], model[prefix + "_Wo_ln_bias"]);

	FeedForwardLayer LoadFeedForward(MarianModel model, string prefix) => new(
		model.Matrix(prefix + "_W1"), model[prefix + "_b1"],
		model.Matrix(prefix + "_W2"), model[prefix + "_b2"],
		model[prefix + "_ffn_ln_scale"], model[prefix + "_ffn_ln_bias"]);

	// Marian dequantizes the embeddings to float at load time and, for the output layer, quantizes it again
	// with 127 / max|W| (an unnamed PrepareB node, hence the "none" alpha for the activations).
	internal static QuantizedMatrix CreateOutputWeights(QuantizedMatrix embeddings, float alpha, int panelWidth = 0, bool saturation = true)
	{
		// Preserve the float multiplication and rounding of the original dequantize/requantize
		// path. Only 256 distinct int8 values exist, so a bounded lookup replaces the full
		// vocabulary-sized float temporary (and the second pass reads only the int8 weights).
		Span<float> dequantized = stackalloc float[256];
		float inverse = 1f / embeddings.QuantMult;
		for (int i = 0; i < dequantized.Length; i++)
			dequantized[i] = (i - 128) * inverse;
		// Multiplication by a finite scalar is monotonic in magnitude. SIMD extrema on
		// the int8 source give the same signed maximum without scanning a float array.
		float maximum = MathF.MaxMagnitude(
			dequantized[TensorPrimitives.Min<sbyte>(embeddings.Data) + 128],
			dequantized[TensorPrimitives.Max<sbyte>(embeddings.Data) + 128]);
		if (!float.IsFinite(inverse))
		{
			foreach (var value in embeddings.Data)
				maximum = MathF.MaxMagnitude(maximum, dequantized[value + 128]);
		}
		float quantMult = 127f / maximum;
		var lookup = new sbyte[256];
		for (int i = 0; i < lookup.Length; i++)
			lookup[i] = (sbyte)Math.Clamp(MathF.Round(dequantized[i] * quantMult), -127f, 127f);
		return QuantizedMatrix.CreateRows(embeddings.Rows, embeddings.Cols, quantMult, alpha, panelWidth, ReadRow, saturation);

		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
		void ReadRow(Span<sbyte> row, int rowIndex)
		{
			var source = embeddings.Row(rowIndex);
			for (int i = 0; i < row.Length; i++)
				row[i] = lookup[source[i] + 128];
		}
	}

	[GeneratedRegex(@"transformer-heads:\s*(\d+)")]
	private static partial Regex HeadsRegex();
}
