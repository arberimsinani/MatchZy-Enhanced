using System;
using System.Collections.Generic;

namespace MatchZy;

/// <summary>
/// Pure damage-report arithmetic, free of CounterStrikeSharp so it is unit tested.
///
/// CS2's player_hurt event carries the damage the weapon dealt, not the health the victim lost:
/// an AWP body shot on a player at 40 HP reports 100+ damage while the victim lost 40. Summing
/// the reported value is how a damage report ends up saying 140 in 1 hit. What a report should
/// count is the health that was actually taken, which can never exceed what the victim had.
/// </summary>
public static class DamageLogic
{
    /// <summary>A player's health at spawn, and so the most any one hit can take.</summary>
    public const int MaxHealth = 100;

    /// <summary>
    /// The health a hit actually removed: the reported damage, capped at the health the victim
    /// had before it, which is itself never above <see cref="MaxHealth"/>.
    /// </summary>
    public static int Dealt(int reportedDamage, int healthBefore)
    {
        if (reportedDamage <= 0) return 0;
        int had = Math.Clamp(healthBefore, 0, MaxHealth);
        return Math.Min(reportedDamage, had);
    }
}

/// <summary>
/// Remembers each victim's health between hits within a round, because player_hurt says what a
/// victim has left but not what they had, and the two differ by the real damage only while the
/// hit did not overkill. Reset every round; an unknown victim is taken to be at full health.
/// </summary>
public sealed class RoundHealthTracker
{
    private readonly Dictionary<int, int> healthByPlayer = new();

    /// <summary>Forget everything: every player is back at full health.</summary>
    public void Reset() => healthByPlayer.Clear();

    /// <summary>The health a player is known to have, full when nothing has hit them yet.</summary>
    public int HealthOf(int playerId) =>
        healthByPlayer.TryGetValue(playerId, out int health) ? health : DamageLogic.MaxHealth;

    /// <summary>
    /// Records a hit and returns the health it actually removed. <paramref name="healthAfter"/> is
    /// what the event says the victim has left, and becomes what the next hit is measured against.
    /// </summary>
    public int Hurt(int victimId, int reportedDamage, int healthAfter)
    {
        int dealt = DamageLogic.Dealt(reportedDamage, HealthOf(victimId));
        healthByPlayer[victimId] = Math.Clamp(healthAfter, 0, DamageLogic.MaxHealth);
        return dealt;
    }
}
