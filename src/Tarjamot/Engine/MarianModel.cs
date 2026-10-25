using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Tarjamot.Engine;

/// <summary>
/// Loader for Marian binary models (format version 1) as shipped by Firefox Translations,
/// with intgemm8 quantized matrices and precomputed activation alphas.
/// </summary>
internal sealed class MarianModel
{
	const uint TypeFloat32 = 0x0404;
	const uint TypeIntgemm8 = 0x4101;

	readonly Dictionary<string, Tensor> tensors = new();
	readonly Dictionary<string, QuantizedMatrix> matrices = new();

	public string Config { get; private set; } = "";

	/// <summary>Alphas of unnamed graph nodes ("none"); Marian uses it for the shortlisted output layer.</summary>
	public float UnnamedAlpha { get; }

	public MarianModel(ReadOnlySpan<byte> data)
	{
		var span = data;
		ulong version = BinaryPrimitives.ReadUInt64LittleEndian(span);
		if (version != 1)
			throw new InvalidDataException($"Unsupported Marian binary version {version}.");
		int count = (int)BinaryPrimitives.ReadUInt64LittleEndian(span[8..]);
		int pos = 16;
		var headers = new (int NameLength, uint Type, int ShapeLength, int DataLength)[count];
		for (int i = 0; i < count; i++, pos += 32)
		{
			headers[i] = (
				(int)BinaryPrimitives.ReadUInt64LittleEndian(span[pos..]),
				(uint)BinaryPrimitives.ReadUInt64LittleEndian(span[(pos + 8)..]),
				(int)BinaryPrimitives.ReadUInt64LittleEndian(span[(pos + 16)..]),
				(int)BinaryPrimitives.ReadUInt64LittleEndian(span[(pos + 24)..]));
		}

		var names = new string[count];
		for (int i = 0; i < count; i++)
		{
			names[i] = Encoding.UTF8.GetString(span.Slice(pos, headers[i].NameLength - 1));
			pos += headers[i].NameLength;
		}

		var shapes = new int[count][];
		for (int i = 0; i < count; i++)
		{
			shapes[i] = new int[headers[i].ShapeLength];
			for (int j = 0; j < shapes[i].Length; j++, pos += 4)
				shapes[i][j] = BinaryPrimitives.ReadInt32LittleEndian(span[pos..]);
		}

		pos += 8 + (int)BinaryPrimitives.ReadUInt64LittleEndian(span[pos..]);

		var alphas = new Dictionary<string, float>();

		for (int i = 0; i < count; i++)
		{
			var item = span.Slice(pos, headers[i].DataLength);
			pos += headers[i].DataLength;
			var shape = shapes[i];
			int rows = shape.Length > 1 ? shape[^2] : 1;
			int cols = shape[^1];
			var name = names[i];

			if (name.StartsWith("special:"))
			{
				Config = Encoding.UTF8.GetString(item).TrimEnd('\0');
				continue;
			}
			if (name.EndsWith("_QuantMultA"))
			{
				alphas[name[..^"_QuantMultA".Length]] = ReadAlpha(item, headers[i].Type);
				continue;
			}

			switch (headers[i].Type)
			{
				case TypeFloat32 when rows == 1:
					tensors[name] = new Tensor(MemoryMarshal.Cast<byte, float>(item).ToArray());
					break;
				case TypeIntgemm8:
					matrices[name] = LoadIntgemm8(item, rows, cols, isEmbedding: name.EndsWith("Wemb"));
					break;
				default:
					throw new InvalidDataException($"Unsupported tensor type {headers[i].Type:x} with shape {rows}x{cols} for {name}.");
			}
		}

		UnnamedAlpha = alphas.GetValueOrDefault("none");
		foreach (var (name, alpha) in alphas)
		{
			if (matrices.TryGetValue(name, out var matrix))
				matrices[name] = matrix with { Alpha = alpha };
		}
	}

	public MarianModel(Stream stream)
		: this(stream, Int8Gemm.PanelWidth, Int8Gemm.UsesSaturationMetadata)
	{
	}

	/// <summary>
	/// Loads the matrices with the layout of the given panel width and, when
	/// <paramref name="saturation"/>, their saturating pairs; the other constructor uses this
	/// machine's kernel.
	/// </summary>
	internal MarianModel(Stream stream, int panelWidth, bool saturation)
	{
		if (!stream.CanRead || !stream.CanSeek)
			throw new ArgumentException("A readable, seekable model stream is required.", nameof(stream));
		using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
		ulong version = reader.ReadUInt64();
		if (version != 1)
			throw new InvalidDataException($"Unsupported Marian binary version {version}.");
		int count = checked((int)reader.ReadUInt64());
		if (count > (stream.Length - stream.Position) / 32)
			throw new InvalidDataException("Truncated Marian tensor headers.");
		var headers = new (int NameLength, uint Type, int ShapeLength, int DataLength)[count];
		for (int i = 0; i < count; i++)
		{
			headers[i] = (
				checked((int)reader.ReadUInt64()),
				(uint)reader.ReadUInt64(),
				checked((int)reader.ReadUInt64()),
				checked((int)reader.ReadUInt64()));
		}

		var names = new string[count];
		for (int i = 0; i < count; i++)
		{
			if (headers[i].NameLength < 1)
				throw new InvalidDataException("Invalid Marian tensor name length.");
			names[i] = Encoding.UTF8.GetString(ReadBytesExactly(stream, headers[i].NameLength).AsSpan(0, headers[i].NameLength - 1));
		}

		var shapes = new int[count][];
		for (int i = 0; i < count; i++)
		{
			if (headers[i].ShapeLength == 0 || headers[i].ShapeLength > (stream.Length - stream.Position) / sizeof(int))
				throw new InvalidDataException("Invalid Marian tensor shape length.");
			shapes[i] = new int[headers[i].ShapeLength];
			for (int j = 0; j < shapes[i].Length; j++)
				shapes[i][j] = reader.ReadInt32();
		}

		long padding = checked((long)reader.ReadUInt64());
		if (padding > stream.Length - stream.Position)
			throw new InvalidDataException("Truncated Marian model padding.");
		stream.Seek(padding, SeekOrigin.Current);

		var alphas = new Dictionary<string, float>();

		for (int i = 0; i < count; i++)
		{
			long itemStart = stream.Position;
			if (headers[i].DataLength < 0 || headers[i].DataLength > stream.Length - itemStart)
				throw new InvalidDataException("Truncated Marian tensor payload.");
			var shape = shapes[i];
			int rows = shape.Length > 1 ? shape[^2] : 1;
			int cols = shape[^1];
			var name = names[i];

			if (name.StartsWith("special:"))
			{
				Config = Encoding.UTF8.GetString(ReadBytesExactly(stream, headers[i].DataLength)).TrimEnd('\0');
				continue;
			}
			if (name.EndsWith("_QuantMultA"))
			{
				alphas[name[..^"_QuantMultA".Length]] = ReadAlpha(ReadBytesExactly(stream, headers[i].DataLength), headers[i].Type);
				continue;
			}

			switch (headers[i].Type)
			{
				case TypeFloat32 when rows == 1:
					if (cols < 0 || headers[i].DataLength % sizeof(float) != 0 || (long)cols * sizeof(float) > headers[i].DataLength)
						throw new InvalidDataException("Invalid Marian float tensor length.");
					var values = new float[headers[i].DataLength / sizeof(float)];
					stream.ReadExactly(MemoryMarshal.AsBytes(values.AsSpan()));
					tensors[name] = new Tensor(values);
					break;
				case TypeIntgemm8:
					if (rows < 0 || cols < 0 || checked((long)rows * cols + sizeof(float)) > headers[i].DataLength)
						throw new InvalidDataException("Invalid Marian quantized tensor length.");
					bool embedding = name.EndsWith("Wemb");
					stream.Position = itemStart + checked((long)rows * cols);
					float quantMult = reader.ReadSingle();
					stream.Position = itemStart;
					matrices[name] = QuantizedMatrix.CreateRows(embedding ? rows : cols, embedding ? cols : rows, quantMult, 0f,
						embedding ? 0 : panelWidth, (row, _) => stream.ReadExactly(MemoryMarshal.AsBytes(row)),
						saturation: !embedding && saturation, rowSums: !embedding);
					stream.Position = itemStart + headers[i].DataLength;
					break;
				default:
					throw new InvalidDataException($"Unsupported tensor type {headers[i].Type:x} with shape {rows}x{cols} for {name}.");
			}
		}

		UnnamedAlpha = alphas.GetValueOrDefault("none");
		foreach (var (name, alpha) in alphas)
		{
			if (matrices.TryGetValue(name, out var matrix))
				matrices[name] = matrix with { Alpha = alpha };
		}
	}

	public Tensor this[string name] => tensors[name];

	public QuantizedMatrix Matrix(string name) => matrices[name];

	public bool HasMatrix(string name) => matrices.ContainsKey(name);

	public int CountLayers(string prefix)
	{
		int layers = 0;
		while (matrices.Keys.Any(k => k.StartsWith($"{prefix}_l{layers + 1}_")))
			layers++;
		return layers;
	}

	// intgemm8 layout: int8 values followed by a float quantization multiplier. Linear layer
	// weights of logical shape [in, out] are stored already transposed, i.e. as [out][in].
	// The embedding matrix is stored as-is ([vocab][dim]).
	static QuantizedMatrix LoadIntgemm8(ReadOnlySpan<byte> item, int rows, int cols, bool isEmbedding)
	{
		int elements = rows * cols;
		float quantMult = BinaryPrimitives.ReadSingleLittleEndian(item[elements..]);
		var values = MemoryMarshal.Cast<byte, sbyte>(item[..elements]).ToArray();
		return isEmbedding
			? QuantizedMatrix.Create(values, rows, cols, quantMult, 0f)
			: QuantizedMatrix.Create(values, cols, rows, quantMult, 0f);
	}

	static byte[] ReadBytesExactly(Stream stream, int length)
	{
		if (length < 0 || length > stream.Length - stream.Position)
			throw new InvalidDataException("Truncated Marian model metadata.");
		var bytes = new byte[length];
		stream.ReadExactly(bytes);
		return bytes;
	}

	// Alphas are float32 scalars, except Wemb_QuantMultA which is stored as a 1x1 intgemm8
	// matrix and dequantized by Marian like the embeddings (value * 1 / quantMult).
	static float ReadAlpha(ReadOnlySpan<byte> item, uint type) => type == TypeIntgemm8
		? (sbyte)item[0] * (1f / BinaryPrimitives.ReadSingleLittleEndian(item[1..]))
		: BinaryPrimitives.ReadSingleLittleEndian(item);
}
