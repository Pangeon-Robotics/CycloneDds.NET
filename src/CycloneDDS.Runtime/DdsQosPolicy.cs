namespace CycloneDDS.Runtime;

/// <summary>
/// DDS QoS policy ids, as reported by REQUESTED_INCOMPATIBLE_QOS. The full standard set
/// is listed because the reporting endpoint is the remote writer, not us: a peer can be
/// incompatible on a policy <see cref="DdsQos"/> never sets.
/// </summary>
public enum DdsQosPolicy
{
    Invalid = 0,
    UserData = 1,
    Durability = 2,
    Presentation = 3,
    Deadline = 4,
    LatencyBudget = 5,
    Ownership = 6,
    OwnershipStrength = 7,
    Liveliness = 8,
    TimeBasedFilter = 9,
    Partition = 10,
    Reliability = 11,
    DestinationOrder = 12,
    History = 13,
    ResourceLimits = 14,
    EntityFactory = 15,
    WriterDataLifecycle = 16,
    ReaderDataLifecycle = 17,
    TopicData = 18,
    GroupData = 19,
    TransportPriority = 20,
    Lifespan = 21,
    DurabilityService = 22,
}
