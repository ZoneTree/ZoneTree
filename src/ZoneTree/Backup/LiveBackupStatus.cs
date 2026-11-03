namespace ZoneTree.Backup;

public enum LiveBackupAttemptState
{
  None,
  Running,
  Succeeded,
  Failed,
  Cancelled
}

/// <summary>An in-memory snapshot of one coordinator's latest accepted attempt.</summary>
/// <remarks>
/// This reports the coordinator's observed outcome, not authoritative storage state.
/// A failed or cancelled operation may have published before reporting its outcome.
/// Timestamps are numeric Unix seconds; zero means unset. Wall-clock timestamps
/// are not monotonic durations. Rejected requests do not change attempt status.
/// </remarks>
/// <param name="Started">Start has enabled automatic operation. Manual attempts can run while false.</param>
/// <param name="AttemptState">State of the latest accepted attempt.</param>
/// <param name="AttemptGenerationId">Latest attempt's validated generation ID; zero until allocation succeeds.</param>
/// <param name="LastSuccessfulGenerationId">Latest generation completed successfully by this coordinator; preserved across failures.</param>
/// <param name="LastStartedAtUnixSeconds">When the latest attempt was accepted, before capture and allocation.</param>
/// <param name="LastFinishedAtUnixSeconds">When the latest attempt finished; reset to zero when another is accepted.</param>
/// <param name="LastCompletedAtUnixSeconds">When the latest successful attempt finished; preserved across failures.</param>
/// <param name="LastError">Failure message, including the underlying cause when wrapped; otherwise empty.</param>
public readonly record struct LiveBackupStatus(
    bool Started,
    LiveBackupAttemptState AttemptState,
    long AttemptGenerationId,
    long LastSuccessfulGenerationId,
    long LastStartedAtUnixSeconds,
    long LastFinishedAtUnixSeconds,
    long LastCompletedAtUnixSeconds,
    string LastError);
