using GameServer.Contracts;

namespace GameServer.Domain.Gameplay;

public static class CombatRules
{
    public static RuleDecision ValidateAttack(WorldPosition attacker, WorldPosition target, decimal range)
        => attacker.DistanceTo(target) <= (float)range
            ? new RuleDecision(true)
            : new RuleDecision(false, "target_out_of_range");

    public static RuleDecision ValidateMove(WorldPosition previous, WorldPosition next, float maxDistance)
        => previous.DistanceTo(next) <= maxDistance
            ? new RuleDecision(true)
            : new RuleDecision(false, "invalid_movement");
}

public static class QuestStateMachine
{
    public static QuestProgress Accept(QuestProgress? current, string questId)
        => current is { IsCompleted: true } ? current : new QuestProgress(questId, current?.Progress ?? 0, true, false);

    public static QuestProgress RecordKill(QuestProgress current, QuestDefinition quest)
        => !current.IsAccepted || current.IsCompleted
            ? current
            : current with { Progress = Math.Min(quest.RequiredKills, current.Progress + 1) };

    public static bool CanComplete(QuestProgress progress, QuestDefinition quest)
        => progress.IsAccepted && !progress.IsCompleted && progress.Progress >= quest.RequiredKills;
}
