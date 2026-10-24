namespace Tarjamot.Engine;

/// <summary>
/// Request-owned tokens from one vocabulary. The list is freshly encoded, never borrowed from
/// a caller; only the engine can access it. Empty filtering uses the count before EOS is added.
/// </summary>
internal readonly struct TokenizedSentence
{
	readonly List<int> tokens;

	public TokenizedSentence(List<int> tokens, int eosId)
	{
		HasTokens = tokens.Count != 0;
		tokens.Add(eosId);
		this.tokens = tokens;
	}

	public bool HasTokens { get; }

	internal List<int> Tokens => tokens;
}
