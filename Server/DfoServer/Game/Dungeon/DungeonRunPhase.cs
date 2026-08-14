namespace DfoServer.Game.Dungeon
{
    // 一局副本的显式相位, 取代原先散布在会话上的三个标志
    // (CurDungeonCleared / CurBossKilled / CurDungeonClearState)。
    // 旧标志组合与相位的对应关系(改造前四个可达组合):
    //   Cleared=false BossKilled=false ClearState=0 -> InProgress
    //   Cleared=true  BossKilled=true  ClearState=0 -> Cleared       (TryClearDungeon 之后)
    //   Cleared=true  BossKilled=false ClearState=4 -> ResultShown   (SET_PLAY_RESULT 结算之后)
    //   Cleared=true  BossKilled=false ClearState=5 -> CardsRevealed (翻牌布局已下发)
    public enum DungeonRunPhase
    {
        None = 0,          // 不在副本中
        InProgress = 1,    // 战斗中
        Cleared = 2,       // 已通关, 等待客户端上报 SET_PLAY_RESULT
        ResultShown = 3,   // 结算三连包已发, 翻牌布局待发
        CardsRevealed = 4, // 翻牌布局已发, 可翻牌/EPLP
    }
}
