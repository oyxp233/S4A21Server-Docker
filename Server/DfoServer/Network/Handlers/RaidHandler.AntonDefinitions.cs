using System.Collections.Generic;

namespace DfoServer.Network.Handlers;

public sealed partial class RaidHandler
{
	private const uint AttackSeconds = 2400u;
	private const uint AttackTimerType = 0u;
	private const uint AttackTimerDungeonId = 0u;

	internal const uint AntonFirstDungeonHpSymbolId = 50u;

	internal const uint AntonSmokePassiveCreateSymbolId = 1u;

	internal const uint AntonMeteoPassiveCreateSymbolId = 2u;

	internal const uint AntonSmokeOpenSymbolId = 120u;

	internal const uint AntonSmokeOutMovieSymbolId = 122u;

	internal const uint AntonNavigunOnMovieSymbolId = 123u;

	internal const uint AntonNavigunSuccessMovieSymbolId = 124u;

	internal const uint AntonNavigunFailMovieSymbolId = 125u;

	internal const uint AntonPhaseOneSuccessSymbolId = 105u;

	internal const uint AntonPhaseOneFailSymbolId = 104u;

	internal const uint AntonBlackVolcanoBarrierMaximum = 10000u;

	internal const uint AntonPhaseTwoInitSymbolId = 106u;

	internal const uint AntonBlackVolcanoBarrierSymbolId = 110u;

	internal const uint AntonBlackVolcanoOpenStateSymbolId = 111u;

	internal const uint AntonBarrierBreakMovieSymbolId = 126u;

	internal const uint AntonBarrierRecoverMovieSymbolId = 127u;

	internal const uint AntonHatcheryOpenMovieSymbolId = 128u;

	internal const uint AntonInfectionExistsSymbolId = 7u;

	internal const uint AntonInfectionDungeonIndexSymbolId = 8u;

	internal static readonly KeyValuePair<uint, uint>[] AntonFirstPhaseInitialDungeonStates = new KeyValuePair<uint, uint>[7]
	{
		new KeyValuePair<uint, uint>(210u, 0u),
		new KeyValuePair<uint, uint>(211u, 2u),
		new KeyValuePair<uint, uint>(212u, 2u),
		new KeyValuePair<uint, uint>(213u, 2u),
		new KeyValuePair<uint, uint>(214u, 2u),
		new KeyValuePair<uint, uint>(215u, 2u),
		new KeyValuePair<uint, uint>(216u, 2u)
	};

	internal static readonly KeyValuePair<uint, uint>[] AntonFirstPhaseInitialSymbols = new KeyValuePair<uint, uint>[10]
	{
		new KeyValuePair<uint, uint>(50u, 0u),
		new KeyValuePair<uint, uint>(51u, 0u),
		new KeyValuePair<uint, uint>(52u, 0u),
		new KeyValuePair<uint, uint>(53u, 0u),
		new KeyValuePair<uint, uint>(54u, 0u),
		new KeyValuePair<uint, uint>(55u, 0u),
		new KeyValuePair<uint, uint>(56u, 0u),
		new KeyValuePair<uint, uint>(1u, 0u),
		new KeyValuePair<uint, uint>(2u, 0u),
		new KeyValuePair<uint, uint>(120u, 0u)
	};

	internal static readonly KeyValuePair<uint, uint>[] AntonFirstPhaseSmokeClearedStates = new KeyValuePair<uint, uint>[6]
	{
		new KeyValuePair<uint, uint>(210u, 2u),
		new KeyValuePair<uint, uint>(211u, 3u),
		new KeyValuePair<uint, uint>(212u, 0u),
		new KeyValuePair<uint, uint>(213u, 0u),
		new KeyValuePair<uint, uint>(214u, 0u),
		new KeyValuePair<uint, uint>(215u, 0u)
	};

	internal static readonly KeyValuePair<uint, uint>[] AntonFirstPhaseResetSymbols = new KeyValuePair<uint, uint>[17]
	{
		new KeyValuePair<uint, uint>(50u, 0u),
		new KeyValuePair<uint, uint>(51u, 0u),
		new KeyValuePair<uint, uint>(52u, 0u),
		new KeyValuePair<uint, uint>(53u, 0u),
		new KeyValuePair<uint, uint>(54u, 0u),
		new KeyValuePair<uint, uint>(55u, 0u),
		new KeyValuePair<uint, uint>(56u, 0u),
		new KeyValuePair<uint, uint>(1u, 0u),
		new KeyValuePair<uint, uint>(2u, 0u),
		new KeyValuePair<uint, uint>(120u, 0u),
		new KeyValuePair<uint, uint>(122u, 0u),
		new KeyValuePair<uint, uint>(123u, 0u),
		new KeyValuePair<uint, uint>(124u, 0u),
		new KeyValuePair<uint, uint>(125u, 0u),
		new KeyValuePair<uint, uint>(104u, 0u),
		new KeyValuePair<uint, uint>(102u, 0u),
		new KeyValuePair<uint, uint>(121u, 1u)
	};

	internal static readonly KeyValuePair<uint, uint>[] AntonSecondPhaseInitialDungeonStates = new KeyValuePair<uint, uint>[7]
	{
		new KeyValuePair<uint, uint>(218u, 0u),
		new KeyValuePair<uint, uint>(219u, 0u),
		new KeyValuePair<uint, uint>(220u, 2u),
		new KeyValuePair<uint, uint>(221u, 2u),
		new KeyValuePair<uint, uint>(222u, 2u),
		new KeyValuePair<uint, uint>(223u, 2u),
		new KeyValuePair<uint, uint>(224u, 2u)
	};

	internal static readonly KeyValuePair<uint, uint>[] AntonSecondPhaseInitialSymbols = new KeyValuePair<uint, uint>[20]
	{
		new KeyValuePair<uint, uint>(57u, 0u),
		new KeyValuePair<uint, uint>(58u, 0u),
		new KeyValuePair<uint, uint>(59u, 0u),
		new KeyValuePair<uint, uint>(60u, 0u),
		new KeyValuePair<uint, uint>(61u, 0u),
		new KeyValuePair<uint, uint>(62u, 0u),
		new KeyValuePair<uint, uint>(63u, 0u),
		new KeyValuePair<uint, uint>(3u, 0u),
		new KeyValuePair<uint, uint>(4u, 0u),
		new KeyValuePair<uint, uint>(5u, 0u),
		new KeyValuePair<uint, uint>(6u, 0u),
		new KeyValuePair<uint, uint>(9u, 0u),
		new KeyValuePair<uint, uint>(10u, 0u),
		new KeyValuePair<uint, uint>(11u, 0u),
		new KeyValuePair<uint, uint>(12u, 0u),
		new KeyValuePair<uint, uint>(7u, 0u),
		new KeyValuePair<uint, uint>(8u, 0u),
		new KeyValuePair<uint, uint>(106u, 1u),
		new KeyValuePair<uint, uint>(110u, 10000u),
		new KeyValuePair<uint, uint>(111u, 0u)
	};

	private static readonly uint[] AntonSecondPhaseDungeonIds = new uint[7] { 218u, 219u, 220u, 221u, 222u, 223u, 224u };

	private static readonly uint[] AntonFirstPhaseDungeonIds = new uint[7] { 210u, 211u, 212u, 213u, 214u, 215u, 216u };

}
