namespace MatchZy;

/// <summary>What HandleMatchEnd should use for one map end.</summary>
/// <param name="RestartDelay">Seconds until the map changes / series resets (feeds the post-series kick delay).</param>
/// <param name="TvFlushDelay">Seconds to wait before stopping the demo so GOTV can flush.</param>
/// <param name="CvarToSet">New mp_match_restart_delay value, or null to leave the convar alone.</param>
public readonly record struct MatchEndDelays(int RestartDelay, int TvFlushDelay, int? CvarToSet);

/// <summary>
/// Pure rules for mp_match_restart_delay, kept free of CounterStrikeSharp so they can be unit tested.
///
/// When demos are uploaded the plugin raises mp_match_restart_delay so GOTV can finish. That
/// raise used to be permanent: after one match with a large tv_delay every later match (even
/// with tv_delay 0) inherited the huge delay, and the post-series kick waited for it too. This
/// tracker remembers the operator's value before the first raise, computes each map's delay
/// from that baseline and the current tv_delay, and hands the original back on reset.
/// </summary>
public sealed class MatchRestartDelayLogic
{
    /// <summary>Seconds on top of tv_delay for GOTV to flush the demo.</summary>
    public const int FlushMargin = 15;
    /// <summary>Extra seconds when a broadcast delay is active.</summary>
    public const int TvDelayMargin = 10;
    /// <summary>Restart delay used when demo recording is disabled.</summary>
    public const int NoDemoRestartDelay = 10;

    private int? originalDelay;
    private int? appliedDelay;

    /// <summary>The operator's mp_match_restart_delay before the plugin raised it, if raised.</summary>
    public int? OriginalDelay => originalDelay;

    public static int TvFlushDelay(int tvDelay) => Math.Max(0, tvDelay) + FlushMargin;

    public static int RequiredDelay(int tvDelay)
    {
        int delay = TvFlushDelay(tvDelay);
        return tvDelay > 0 ? delay + TvDelayMargin : delay;
    }

    public MatchEndDelays Compute(int currentCvarDelay, int tvDelay, bool demoRecordingEnabled, bool hasUploadEndpoint)
    {
        ForgetIfChangedExternally(currentCvarDelay);

        if (!demoRecordingEnabled) return Cover(NoDemoRestartDelay, 0, currentCvarDelay);

        int required = RequiredDelay(tvDelay);
        int flush = TvFlushDelay(tvDelay);
        if (!hasUploadEndpoint) return Cover(required, flush, currentCvarDelay);

        int baseline = originalDelay ?? currentCvarDelay;
        int effective = Math.Max(baseline, required);
        if (effective == currentCvarDelay) return new MatchEndDelays(effective, flush, null);

        originalDelay ??= currentCvarDelay;
        appliedDelay = effective;
        return new MatchEndDelays(effective, flush, effective);
    }

    /// <summary>
    /// The branches that do not wait for an upload: the delay is what the recording needs,
    /// and the convar is raised to cover it when it is shorter, never lowered.
    ///
    /// The game runs its own countdown on mp_match_restart_delay and acts when it expires:
    /// with mp_match_end_restart 0 it loads the next map of the map group, with 1 it restarts
    /// the map. The plugin's own map change lands at RestartDelay - 1, so the convar must
    /// never be shorter than the delay chosen here, whichever branch chose it. Recording
    /// with no upload URL waits tv_delay + 25 s against the game's 25 s default, and without
    /// this the game changed level first, with the demo still being written. The raise is
    /// tracked like the upload branch's, so <see cref="Restore"/> undoes it on reset.
    /// </summary>
    private MatchEndDelays Cover(int restartDelay, int flush, int currentCvarDelay)
    {
        if (restartDelay <= currentCvarDelay) return new MatchEndDelays(restartDelay, flush, null);

        originalDelay ??= currentCvarDelay;
        appliedDelay = restartDelay;
        return new MatchEndDelays(restartDelay, flush, restartDelay);
    }

    /// <summary>
    /// Returns the value to put mp_match_restart_delay back to, or null when nothing needs
    /// restoring. Clears the tracked state either way, so repeated calls are no-ops.
    /// </summary>
    public int? Restore(int currentCvarDelay)
    {
        ForgetIfChangedExternally(currentCvarDelay);
        int? original = originalDelay;
        originalDelay = null;
        appliedDelay = null;
        return original is int value && value != currentCvarDelay ? value : null;
    }

    // Someone (a cfg, an admin) set the convar after we raised it: their value wins.
    private void ForgetIfChangedExternally(int currentCvarDelay)
    {
        if (appliedDelay is int applied && applied != currentCvarDelay)
        {
            originalDelay = null;
            appliedDelay = null;
        }
    }
}
