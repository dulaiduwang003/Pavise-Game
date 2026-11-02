namespace PaviseApp
{
	internal sealed class GameNvApiEvidence
	{
		internal readonly GameNvApi Api;

		internal readonly GameNvApiEvidenceKind Kind;

		internal readonly string ExecutableHash;

		internal readonly string SourceVersion;

		internal readonly string PhysicalToken;

		internal readonly bool Nvidia;

		internal GameNvApiEvidence(GameNvApi api, GameNvApiEvidenceKind kind, string executableHash, string sourceVersion, string physicalToken, bool nvidia)
		{
			Api = api;
			Kind = kind;
			ExecutableHash = executableHash;
			SourceVersion = sourceVersion;
			PhysicalToken = physicalToken;
			Nvidia = nvidia;
		}
	}
}
