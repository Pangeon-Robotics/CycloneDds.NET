using System;
namespace CycloneDDS.Schema;

/// <summary>
/// Specifies the Quality of Service (QoS) settings for a DDS topic.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public sealed class DdsQosAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the reliability QoS policy.
    /// </summary>
    public DdsReliability Reliability { get; set; } = DdsReliability.Reliable;

    /// <summary>
    /// How long a reliable write may block when the writer's history is full.
    /// Ignored for BestEffort.
    /// </summary>
    public double MaxBlockingSeconds { get; init; } = 0.1;

    /// <summary>
    /// Gets or sets the durability QoS policy.
    /// </summary>
    public DdsDurability Durability { get; set; } = DdsDurability.Volatile;

    /// <summary>
    /// Gets or sets the history kind QoS policy.
    /// </summary>
    public DdsHistoryKind HistoryKind { get; set; } = DdsHistoryKind.KeepLast;

    /// <summary>
    /// Gets or sets how liveliness is asserted. <see cref="DdsLiveliness.Automatic"/> lets
    /// Cyclone assert it for the lifetime of the process; <see cref="DdsLiveliness.ManualByTopic"/>
    /// counts only writes on the writer itself.
    /// </summary>
    public DdsLiveliness Liveliness { get; set; } = DdsLiveliness.Automatic;

    /// <summary>
    /// Gets or sets the history depth. Only used when HistoryKind is KeepLast.
    /// </summary>
    public int HistoryDepth { get; set; } = 1;

    /// <summary>
    /// Gets or sets the DEADLINE period in seconds. A writer commits to publishing at least
    /// this often; a reader declares it needs samples at least this often. <c>null</c> means
    /// no deadline. A reader's deadline must be &gt;= the writer's or the two will not match.
    /// </summary>
    public double Deadline { get; set; } = -1;

    /// <summary>
    /// Gets or sets the LIVELINESS lease duration in seconds. A writer that does not assert
    /// liveliness within this window is declared not-alive to its readers. <c>null</c> means
    /// infinite. A reader's lease must be &gt;= the writer's or the two will not match.
    /// </summary>
    public double LivelinessLease { get; set; } = -1;

    /// <summary>
    /// Gets or sets the LIFESPAN in seconds. Samples older than this are discarded and never
    /// delivered. <c>null</c> means samples never expire. Writer-side only; DDS ignores it on
    /// a reader.
    /// </summary>
    public double Lifespan { get; set; } = -1;
}
