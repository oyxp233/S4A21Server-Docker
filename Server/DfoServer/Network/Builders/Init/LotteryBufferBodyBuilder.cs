using System;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Lottery;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Builders
{
    // NOTI 984 (0x03D8) 增率抽奖数据。固定发 204 字节空态(新角色既有基线)，
    // 活动增率数据对单机服务端无意义。
    public sealed class LotteryBufferBodyBuilder : IInitPacketBuilder
    {
        private readonly IncreaseChanceLotteryProgressRepository _progressRepository;

        public LotteryBufferBodyBuilder()
            : this(GameDatabase.CreateDefault())
        {
        }

        public LotteryBufferBodyBuilder(IGameDatabase database)
        {
            if (database == null)
                throw new ArgumentNullException(nameof(database));

            _progressRepository = new IncreaseChanceLotteryProgressRepository(
                database.ConnectionString);
        }

        public ushort NotiType => 0x03D8;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var accountId = snapshot?.CharacterRecord?.AccountId ?? 0;
            var progress = accountId > 0
                ? _progressRepository.LoadAll(accountId)
                : System.Array.Empty<LotteryProgressSnapshot>();
            body = IncreaseChanceLotteryPacketBuilder.BuildAllState(progress);
            return true;
        }
    }
}
