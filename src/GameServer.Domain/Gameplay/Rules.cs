using GameServer.Contracts;

namespace GameServer.Domain.Gameplay;

public static class CombatRules
{
    // 距离校验保留在纯领域层，使角色、区域等入口共享同一判定而不会产生规则漂移。
    public static RuleDecision ValidateAttack(WorldPosition attacker, WorldPosition target, decimal range)
        => attacker.DistanceTo(target) <= (float)range
            ? new RuleDecision(true)
            : new RuleDecision(false, "target_out_of_range");

}

public static class QuestStateMachine
{
    // 已完成任务不可被重新接受，避免后续奖励流程因重复接取而再次满足完成条件。
    public static QuestProgress Accept(QuestProgress? current, string questId)
        => current is { IsCompleted: true } ? current : new QuestProgress(questId, current?.Progress ?? 0, true, false);

    public static QuestProgress RecordKill(QuestProgress current, QuestDefinition quest)
        => !current.IsAccepted || current.IsCompleted
            ? current
            : current with { Progress = Math.Min(quest.RequiredKills, current.Progress + 1) };

    public static bool CanComplete(QuestProgress progress, QuestDefinition quest)
        => progress.IsAccepted && !progress.IsCompleted && progress.Progress >= quest.RequiredKills;
}
