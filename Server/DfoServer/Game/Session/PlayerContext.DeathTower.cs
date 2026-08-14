namespace DfoServer.Game.Session
{
    public partial class PlayerContext
    {
        // 塔状态便捷只读入口: 真相在 CurrentRun.Tower(塔是一局副本的变体)。
        public DeathTower.DeathTowerSession DeathTowerState => CurrentRun?.Tower;

        public bool IsInDeathTower => CurrentRun?.Tower != null;
    }
}
