using System;
using CycloneDDS.Runtime.Interop;
using CycloneDDS.Schema;

namespace CycloneDDS.Runtime
{

	/// <summary>
	/// A declarative QoS profile. Immutable; build variants with <c>with</c> expressions:
	/// <code>
	/// var qos = DdsQos.BestEffort with { Deadline = 0.1, LivelinessLease = 2.0 };
	/// </code>
	/// All duration properties are in <b>seconds</b>.
	/// <para>
	/// A <c>null</c> policy is not written to the native QoS at all, so Cyclone applies its
	/// own default for it. That default is not always the same on both ends: a reader
	/// defaults to BestEffort while a writer defaults to Reliable, for instance. Start from
	/// <see cref="SystemDefault"/> to opt into Cyclone's defaults wholesale, or from
	/// <see cref="Default"/> to get this library's explicit reliable profile.
	/// </para>
	/// For the duration policies a null means DDS_INFINITY (i.e. "no limit"), which is the
	/// DDS default for every one of them.
	/// </summary>
	public sealed record DdsQos
	{
		/// <summary>
		/// Gets the reliability QoS policy. <c>null</c> leaves Cyclone's own default, which is
		/// BestEffort on a reader and Reliable on a writer.
		/// </summary>
		public DdsReliability? Reliability { get; init; } = null;

		/// <summary>
		/// How long a reliable write may block when the writer's history is full.
		/// Ignored for BestEffort, and ignored entirely when <see cref="Reliability"/> is
		/// <c>null</c>. The default matches Cyclone's own 100 ms.
		/// </summary>
		public double MaxBlockingSeconds { get; init; } = 0.1;

		/// <summary>
		/// Gets the durability QoS policy. <c>null</c> leaves Cyclone's own default (Volatile).
		/// </summary>
		public DdsDurability? Durability { get; init; } = null;

		/// <summary>
		/// Gets the history kind QoS policy. <c>null</c> leaves Cyclone's own default
		/// (KeepLast), unless <see cref="HistoryDepth"/> is set, in which case KeepLast is
		/// implied.
		/// </summary>
		public DdsHistoryKind? HistoryKind { get; init; } = null;

		/// <summary>
		/// How liveliness is asserted. <see cref="DdsLiveliness.Automatic"/> lets Cyclone assert
		/// it for the lifetime of the process; <see cref="DdsLiveliness.ManualByTopic"/> counts
		/// only writes on the writer itself. Automatic is also Cyclone's own default, so it is
		/// only written to the native QoS when it deviates or a lease is set.
		/// </summary>
		public DdsLiveliness Liveliness { get; init; } = DdsLiveliness.Automatic;

		/// <summary>
		/// Gets the history depth. Only used when <see cref="HistoryKind"/> is KeepLast.
		/// <c>null</c> leaves Cyclone's own default (a depth of 1).
		/// </summary>
		public int? HistoryDepth { get; init; } = null;

		/// <summary>
		/// DEADLINE period, seconds. A writer commits to publishing at least this often; a reader
		/// declares it needs samples at least this often. Null means no deadline. A reader's
		/// deadline must be &gt;= the writer's or the two will not match.
		/// </summary>
		public double? Deadline { get; init; } = null;

		/// <summary>
		/// LIVELINESS lease duration, seconds. A writer that does not assert liveliness within
		/// this window is declared not-alive to its readers. Null means infinite. A reader's
		/// lease must be &gt;= the writer's or the two will not match.
		/// </summary>
		public double? LivelinessLease { get; init; } = null;

		/// <summary>
		/// LIFESPAN, seconds. Samples older than this are discarded and never delivered. Null
		/// means samples never expire. Writer-side only; DDS ignores it on a reader.
		/// </summary>
		public double? Lifespan { get; init; } = null;

		// ---- Presets ---------------------------------------------------------------

		// Every preset below is spelled out from this one profile rather than from another
		// static field, because static field initialisers run in declaration order: building
		// one preset out of another would silently yield an all-null (system default) profile
		// if the fields were ever reordered. A property is re-evaluated on each access and so
		// has no such ordering dependency.
		private static DdsQos Explicit => new()
		{
			Reliability = DdsReliability.Reliable,
			Durability = DdsDurability.Volatile,
			HistoryKind = DdsHistoryKind.KeepLast,
			HistoryDepth = 1,
		};

		/// <summary>
		/// Sets no policy at all, leaving every one of them at Cyclone's own default: volatile,
		/// keep-last 1, and a reliability that differs by entity — BestEffort on a reader,
		/// Reliable on a writer.
		/// </summary>
		/// <remarks>
		/// This is what a topic with no <see cref="DdsQosAttribute"/> gets, and it is the
		/// profile a raw <c>dds_create_qos()</c> would have produced. Use it as the base when
		/// you want to override one policy and leave the rest to Cyclone:
		/// <code>
		/// var qos = DdsQos.SystemDefault with { Deadline = 0.5 };
		/// </code>
		/// </remarks>
		public static readonly DdsQos SystemDefault = new();

		/// <summary>Reliable, volatile, keep-last 1. Reliable on a reader too, unlike <see cref="SystemDefault"/>.</summary>
		public static readonly DdsQos Default = Explicit;

		/// <summary>Reliable, volatile, keep-last 10.</summary>
		public static readonly DdsQos Reliable = Explicit with
		{
			HistoryDepth = 10
		};

		/// <summary>Best-effort, volatile, keep-last 1. Newest value only, drops freely.</summary>
		public static readonly DdsQos BestEffort = Explicit with
		{
			Reliability = DdsReliability.BestEffort,
			HistoryDepth = 1,
		};

		/// <summary>Reliable, volatile, keep-all. Nothing is dropped by history.</summary>
		public static readonly DdsQos KeepAll = Explicit with
		{
			HistoryKind = DdsHistoryKind.KeepAll,
		};

		/// <summary>
		/// Reliable, transient-local, keep-last 1. A late-joining reader immediately gets the last value.
		/// </summary>
		public static readonly DdsQos Latched = Explicit with
		{
			Durability = DdsDurability.TransientLocal,
		};

		// ---- Bridge Attribute Style ------------------------------------------------

		/// <summary>
		/// Projects a type's <see cref="DdsQosAttribute"/> onto an equivalent profile, so a
		/// declaratively decorated topic and an explicitly passed profile take the same path.
		/// </summary>
		/// <param name="attribute">The attribute found on the topic type.</param>
		internal static DdsQos FromAttribute(DdsQosAttribute attribute)
		{
			return new DdsQos()
			{
				Reliability = attribute.Reliability,
				MaxBlockingSeconds = attribute.MaxBlockingSeconds,
				Durability = attribute.Durability,
				HistoryKind = attribute.HistoryKind,
				Liveliness = attribute.Liveliness,
				HistoryDepth = attribute.HistoryDepth,
				Deadline = attribute.Deadline,
				LivelinessLease = attribute.LivelinessLease,
				Lifespan = attribute.Lifespan
			};
		}

		// ---- Native construction ---------------------------------------------------

		/// <summary>
		/// Builds a native dds_qos_t. The caller owns the result and must pass it to
		/// <see cref="DdsApi.dds_delete_qos"/> once the entity has been created (Cyclone
		/// copies the QoS into the entity, so it is safe to delete immediately after).
		/// </summary>
		internal nint CreateNative()
		{
			IntPtr qos = DdsApi.dds_create_qos();
			if (qos == IntPtr.Zero)
			{
				throw new InvalidOperationException("dds_create_qos returned null");
			}

			try
			{
				// Every policy is applied only when it is actually set, so a profile that
				// leaves one null produces a QoS indistinguishable from the empty one Cyclone
				// builds on its own and Cyclone's default for that policy stands.

				if (Reliability.HasValue)
				{
					DdsApi.dds_qset_reliability(qos, (int)Reliability.Value, DdsApi.Duration(MaxBlockingSeconds));
				}

				if (Durability.HasValue)
				{
					DdsApi.dds_qset_durability(qos, (int)Durability.Value);
				}

				// A depth on its own implies KeepLast, which is also Cyclone's default kind.
				if (HistoryKind.HasValue || HistoryDepth.HasValue)
				{
					DdsHistoryKind kind = HistoryKind ?? DdsHistoryKind.KeepLast;
					int depth = HistoryDepth ?? 1;
					if (kind == DdsHistoryKind.KeepAll)
					{
						depth = -1; // DDS_LENGTH_UNLIMITED
						DdsApi.dds_qset_resource_limits(qos, -1, -1, -1);
					}
					DdsApi.dds_qset_history(qos, (int)kind, depth);
				}

				if (Deadline.HasValue)
				{
					DdsApi.dds_qset_deadline(qos, DdsApi.Duration(Deadline));
				}

				if (Lifespan.HasValue)
				{
					DdsApi.dds_qset_lifespan(qos, DdsApi.Duration(Lifespan));
				}

				if (Liveliness != DdsLiveliness.Automatic || LivelinessLease.HasValue)
				{
					DdsApi.dds_qset_liveliness(qos, (int)Liveliness, DdsApi.Duration(LivelinessLease));
				}

				return qos;
			}
			catch
			{
				DdsApi.dds_delete_qos(qos);
				throw;
			}
		}
	}
}