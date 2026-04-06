namespace ArchimedesScrew;

/// <summary>
/// Drives how soon this intake controller is eligible for the next global water tick.
/// High cadence matches the former fast path (active conversion, adoption, drain, or no tracked sources yet).
/// </summary>
public enum ArchimedesScrewControllerSchedule
{
    HighCadence,
    LowCadence
}
