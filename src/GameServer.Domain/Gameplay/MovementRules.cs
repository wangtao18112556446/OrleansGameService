using GameServer.Contracts;

namespace GameServer.Domain.Gameplay;

/// <summary>描述一次移动预算判定，以及判定后可继续使用的距离。</summary>
public readonly record struct MovementDecision(bool Allowed, string? ErrorCode, float AvailableDistance);

/// <summary>按服务器流逝时间补充距离预算，并统一判定非法位移与暂时超速。</summary>
public static class MovementRules
{
    public static float Capacity(MovementDefinition definition)
    {
        if (!float.IsFinite(definition.SpeedPerSecond) || definition.SpeedPerSecond <= 0f || definition.MaximumBudget <= TimeSpan.Zero)
            throw new InvalidOperationException("Movement speed and maximum budget must be positive finite values.");

        var capacity = definition.SpeedPerSecond * definition.MaximumBudget.TotalSeconds;
        if (!double.IsFinite(capacity) || capacity > float.MaxValue)
            throw new InvalidOperationException("Movement distance capacity must be finite.");

        return (float)capacity;
    }

    public static MovementDecision Evaluate(
        WorldPosition previous,
        WorldPosition next,
        float availableDistance,
        TimeSpan elapsed,
        MovementDefinition definition)
    {
        var capacity = Capacity(definition);
        var boundedElapsed = elapsed <= TimeSpan.Zero
            ? TimeSpan.Zero
            : elapsed > definition.MaximumBudget ? definition.MaximumBudget : elapsed;
        var safeAvailable = float.IsFinite(availableDistance) ? Math.Clamp(availableDistance, 0f, capacity) : 0f;
        var refilled = MathF.Min(capacity, safeAvailable + definition.SpeedPerSecond * (float)boundedElapsed.TotalSeconds);

        if (!float.IsFinite(previous.X) || !float.IsFinite(previous.Y) || !float.IsFinite(next.X) || !float.IsFinite(next.Y))
            return new MovementDecision(false, "invalid_movement", refilled);

        var distance = previous.DistanceTo(next);
        if (!float.IsFinite(distance) || distance > capacity)
            return new MovementDecision(false, "invalid_movement", refilled);
        // 预算不足时保守拒绝，不能用固定浮点容差放行大量极小位移并累积绕过速度限制。
        if (distance > refilled)
            return new MovementDecision(false, "movement_rate_limited", refilled);

        return new MovementDecision(true, null, MathF.Max(0f, refilled - distance));
    }
}
