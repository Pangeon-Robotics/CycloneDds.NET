namespace CycloneDDS.Schema;

/// <summary>
/// Specifies the liveliness QoS policy.
/// </summary>
/// <remarks>
/// The values are ordered <see cref="Automatic"/> &lt; <see cref="ManualByParticipant"/> &lt;
/// <see cref="ManualByTopic"/>, and a writer's kind must be at least the reader's or the two
/// will not match.
/// </remarks>
public enum DdsLiveliness
{
    /// <summary>Cyclone asserts liveliness for you as long as the process is alive.</summary>
    Automatic = 0,

    /// <summary>
    /// Liveliness is asserted for every such writer in the participant at once, by a write or
    /// an explicit assertion on any one of them.
    /// </summary>
    /// <remarks>
    /// Named for completeness — a remote writer can advertise this kind, so the value has to
    /// have a name for anything reading QoS back off discovery. It is deliberately not
    /// supported for authoring: MBP is uncommon and supporting it would add more complexity than it's worth.
    /// Use  <see cref="Automatic"/> or <see cref="ManualByTopic"/> instead. 
    /// </remarks>
    ManualByParticipant = 1,

    /// <summary>Liveliness is asserted only by writes on this specific writer.</summary>
    ManualByTopic = 2
}
