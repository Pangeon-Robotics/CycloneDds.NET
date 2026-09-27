using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace CycloneDDS.Runtime.Hub;

/// <summary>Severity of a message reported through a <see cref="DdsHub"/>'s log callback.</summary>
public enum DdsHubLogLevel
{
	Warning,
	Error,
}

/// <summary>
/// Owns a participant and every reader and writer created through it, and services them
/// from <see cref="Pump"/> so all sample handling happens on the thread that pumps.
///
/// <para>The hub is not thread-safe. Create channels, call <see cref="Pump"/> and dispose
/// the hub from one thread, typically the host's main loop.</para>
///
/// <para>Failures to create or service a channel are reported through the log callback
/// rather than thrown; a creating call that fails returns <c>null</c>.</para>
///
/// <para>Channels are shared per (type, topic). A <c>null</c> QoS on a call that finds an
/// existing channel means "whatever it was created with"; on a call that creates the
/// channel it means <see cref="DdsQos.SystemDefault"/>. An explicit profile that differs
/// from the existing channel's is logged as a warning and the existing channel is kept.</para>
/// </summary>
/// <example>
/// Subscribe with a callback:
/// <code>
/// hub.Subscribe&lt;String_&gt;("rt/chatter", msg =&gt; Console.WriteLine(msg.data));
/// </code>
/// Subscribe by polling, no callback:
/// <code>
/// if (hub.TryLatest&lt;Twist_&gt;("rt/cmd_vel", out var t)) speed = t.Linear.X;
/// </code>
/// Publish once:
/// <code>
/// hub.Publish("rt/chatter", new String_ { data = "hello" });
/// </code>
/// Publish continuously, rate limited, with a keep-alive:
/// <code>
/// var cmd = hub.Publisher&lt;Twist_&gt;(
///     "rt/cmd_vel",
///     DdsQos.Reliable with { Deadline = 0.5 },
///     rateHz: 20,
///     heartbeatSeconds: 0.25);
///
/// cmd.Set(twist); // safe to call every frame
/// </code>
/// Hosting in a Godot autoload:
/// <code>
/// public partial class Dds : Node
/// {
///     public static DdsHub Hub { get; private set; }
///
///     public override void _Ready()
///     {
///         ProcessPriority = -1000; // pump before every other node
///         Hub = new DdsHub(
///             log: (level, msg) =&gt;
///             {
///                 if (level == DdsHubLogLevel.Error) GD.PushError(msg);
///                 else GD.PushWarning(msg);
///             },
///             clock: () =&gt; Time.GetTicksUsec() / 1_000_000.0);
///     }
///
///     public override void _Process(double delta) =&gt; Hub.Pump();
///
///     public override void _ExitTree() =&gt; Hub.Dispose();
/// }
/// </code>
/// </example>
public sealed class DdsHub : IDisposable
{
	readonly Action<DdsHubLogLevel, string> _log;
	readonly Func<double> _clock;

	readonly Dictionary<ChannelKey, IDdsChannel> _publishers = [];
	readonly Dictionary<ChannelKey, IDdsChannel> _subscriptions = [];

	// Readers are pumped before writers; see Pump. The flat snapshot keeps the pump
	// stable if a handler creates or closes a channel mid-pump.
	readonly List<IDdsChannel> _readerPump = [];
	readonly List<IDdsChannel> _writerPump = [];
	IDdsChannel[] _pumpSnapshot = [];
	bool _pumpDirty;

	// Cyclone's topic entities are cached per participant by name only, so a name can
	// only ever carry one type. Catch the mismatch here instead of letting it corrupt
	// deserialisation at runtime. A name is bound while at least one channel uses it.
	readonly Dictionary<string, Type> _topicTypes = [];

	/// <summary>
	/// Creates the hub and the participant it owns.
	/// </summary>
	/// <param name="domainId">DDS domain id. For ROS 2 interop this must match ROS_DOMAIN_ID.</param>
	/// <param name="defaultPartition">
	/// Partition applied to every endpoint. <c>null</c> leaves the DDS default partition, which
	/// is where ROS 2 lives.
	/// </param>
	/// <param name="log">Receives warnings and errors. Defaults to standard error.</param>
	/// <param name="clock">
	/// Monotonic time in seconds, used for throttling, heartbeats and sample age. Defaults to
	/// <see cref="Stopwatch"/>; pass the host's own clock to keep timing consistent with it.
	/// </param>
	public DdsHub(
		uint domainId = 0,
		string? defaultPartition = null,
		Action<DdsHubLogLevel, string>? log = null,
		Func<double>? clock = null)
	{
		_log = log ?? DefaultLog;
		_clock = clock ?? DefaultClock;
		Participant = new DdsParticipant(domainId, defaultPartition);
	}

	/// <summary>The participant every channel is created on. <c>null</c> once the hub is disposed.</summary>
	public DdsParticipant? Participant { get; private set; }

	/// <summary>Current time from the hub's clock, in seconds.</summary>
	public double Now => _clock();

	/// <summary>
	/// Services every channel: drains readers and runs their handlers, then sends throttled
	/// writes and heartbeats. Call once per host tick, from the thread that owns the hub.
	/// </summary>
	public void Pump()
	{
		if (Participant is null)
		{
			return;
		}

		var now = _clock();

		if (_pumpDirty)
		{
			RebuildPump();
		}

		// Readers first, so a handler that calls Set() on a publisher still makes this
		// pump's flush.
		var pump = _pumpSnapshot;
		for (var i = 0; i < pump.Length; i++)
		{
			pump[i].Pump(now);
		}
	}

	/// <summary>
	/// Tears down every endpoint and then the participant. Deleting the participant is
	/// what puts an SPDP dispose on the wire; without it remote peers keep this participant
	/// matched and alive until its lease expires, which measures ~10 s against Cyclone's
	/// defaults. Safe to call twice.
	/// </summary>
	public void Dispose()
	{
		if (Participant is null)
		{
			return;
		}

		// The participant delete below is the part that matters, so a throwing channel
		// must not be able to skip it.
		foreach (var channel in _readerPump)
		{
			try
			{
				channel.Dispose();
			}
			catch (Exception e)
			{
				LogError($"DDS reader dispose failed on '{channel.Topic}': {e.Message}");
			}
		}

		foreach (var channel in _writerPump)
		{
			try
			{
				channel.Dispose();
			}
			catch (Exception e)
			{
				LogError($"DDS writer dispose failed on '{channel.Topic}': {e.Message}");
			}
		}

		_readerPump.Clear();
		_writerPump.Clear();
		_pumpSnapshot = Array.Empty<IDdsChannel>();
		_publishers.Clear();
		_subscriptions.Clear();
		_topicTypes.Clear();

		// This is the call that puts the SPDP dispose on the wire. Dispose commonly runs on
		// shutdown paths where a thrown exception goes unseen, so a failure is logged by name.
		var participant = Participant;
		Participant = null;

		try
		{
			participant.Dispose();
		}
		catch (Exception e)
		{
			LogError($"DDS participant dispose FAILED, peers will hold it alive until the lease expires: {e}");
		}
	}

	// ---- Subscribing -----------------------------------------------------------

	/// <summary>
	/// Registers <paramref name="handler"/> for <paramref name="topic"/>, creating the
	/// reader on first use. The handler runs from <see cref="Pump"/>. Dispose the returned
	/// token to unsubscribe.
	/// </summary>
	public IDisposable? Subscribe<T>(string topic, Action<T> handler, DdsQos? qos = null) where T : new()
		=> Subscription<T>(topic, qos)?.Add(handler);

	/// <summary>
	/// Gets or creates the shared subscription for <paramref name="topic"/>. Use this when
	/// you want the reader's statistics, matched-writer count or events rather than just
	/// a callback.
	/// </summary>
	public DdsSubscription<T>? Subscription<T>(string topic, DdsQos? qos = null) where T : new()
	{
		if (!RequireParticipant(out var participant))
		{
			return null;
		}

		var key = new ChannelKey(typeof(T), topic);
		if (_subscriptions.TryGetValue(key, out var existing))
		{
			var found = (DdsSubscription<T>)existing;
			WarnOnQosConflict(topic, "subscription", found.Qos, qos);
			return found;
		}

		if (!CanBindTopic(topic, typeof(T)))
		{
			return null;
		}

		var profile = qos ?? DdsQos.SystemDefault;

		DdsSubscription<T> subscription;
		try
		{
			var reader = new DdsReader<T>(participant, topic, profile);
			subscription = new DdsSubscription<T>(this, reader, topic, profile);
		}
		catch (Exception e)
		{
			LogError($"DDS failed to create reader for '{topic}' ({typeof(T).Name}): {e.Message}");
			return null;
		}

		_topicTypes[topic] = typeof(T);
		_subscriptions[key] = subscription;
		_readerPump.Add(subscription);
		_pumpDirty = true;

		return subscription;
	}

	/// <summary>
	/// Reads the newest sample seen on <paramref name="topic"/>. Creates the reader on
	/// first call, so the first call always returns false and data appears from a later
	/// <see cref="Pump"/> onward.
	/// </summary>
	public bool TryLatest<T>(string topic, [MaybeNullWhen(false)] out T value, DdsQos? qos = null) where T : new()
	{
		var subscription = Subscription<T>(topic, qos);
		if (subscription is null || !subscription.HasValue)
		{
			value = default;
			return false;
		}

		value = subscription.Latest!;
		return true;
	}

	/// <summary>
	/// Newest sample on <paramref name="topic"/>, or <c>default</c> if nothing has
	/// arrived yet. See <see cref="TryLatest{T}"/> for the first-call caveat.
	/// </summary>
	public T? Latest<T>(string topic, DdsQos? qos = null) where T : new()
		=> TryLatest<T>(topic, out var value, qos) ? value : default;

	// ---- Publishing ------------------------------------------------------------

	/// <summary>
	/// Gets or creates the writer for <paramref name="topic"/>.
	/// <paramref name="rateHz"/> and <paramref name="heartbeatSeconds"/> only apply when
	/// the publisher is created; they are ignored on later calls for the same topic. Set
	/// <see cref="DdsPublisher{T}.RateHz"/> directly to change them afterwards.
	/// </summary>
	/// <param name="topic">Topic name. One name can only carry one type per hub.</param>
	/// <param name="qos">
	/// QoS profile for the writer. <c>null</c> keeps an existing writer's profile, or uses
	/// <see cref="DdsQos.SystemDefault"/> when the writer is created.
	/// </param>
	/// <param name="rateHz">
	/// Maximum writes per second for <see cref="DdsPublisher{T}.Set"/>, latest value wins.
	/// 0 means unthrottled.
	/// </param>
	/// <param name="heartbeatSeconds">
	/// Re-send the last value after this much idle time. 0 disables it.
	/// </param>
	public DdsPublisher<T>? Publisher<T>(
		string topic,
		DdsQos? qos = null,
		double rateHz = 0.0,
		double heartbeatSeconds = 0.0)
	{
		if (!RequireParticipant(out var participant))
		{
			return null;
		}

		var key = new ChannelKey(typeof(T), topic);
		if (_publishers.TryGetValue(key, out var existing))
		{
			var found = (DdsPublisher<T>)existing;
			WarnOnQosConflict(topic, "publisher", found.Qos, qos);
			return found;
		}

		if (!CanBindTopic(topic, typeof(T)))
		{
			return null;
		}

		var profile = qos ?? DdsQos.SystemDefault;

		DdsPublisher<T> publisher;
		try
		{
			var writer = new DdsWriter<T>(participant, topic, profile);
			publisher = new DdsPublisher<T>(this, writer, topic, profile)
			{
				RateHz = rateHz,
				HeartbeatSeconds = heartbeatSeconds,
			};
		}
		catch (Exception e)
		{
			LogError($"DDS failed to create writer for '{topic}' ({typeof(T).Name}): {e.Message}");
			return null;
		}

		_topicTypes[topic] = typeof(T);
		_publishers[key] = publisher;
		_writerPump.Add(publisher);
		_pumpDirty = true;

		return publisher;
	}

	/// <summary>
	/// Writes one sample immediately, creating and caching the writer on first use.
	/// Bypasses any throttle configured on the publisher.
	/// </summary>
	public void Publish<T>(string topic, in T sample, DdsQos? qos = null)
		=> Publisher<T>(topic, qos)?.PublishNow(in sample);

	// ---- Teardown --------------------------------------------------------------

	/// <summary>
	/// Destroys the reader and/or writer for <paramref name="topic"/>. Any outstanding
	/// subscription tokens become inert, and the topic name is free to carry another type.
	/// </summary>
	public void Close<T>(string topic)
	{
		var key = new ChannelKey(typeof(T), topic);
		CloseChannel(_publishers, _writerPump, key);
		CloseChannel(_subscriptions, _readerPump, key);

		// Both channels a name can have are keyed by the one type bound to it, so once
		// they are closed nothing uses the name any more.
		if (_topicTypes.TryGetValue(topic, out var bound) && bound == typeof(T))
		{
			_topicTypes.Remove(topic);
		}
	}

	void CloseChannel(Dictionary<ChannelKey, IDdsChannel> map, List<IDdsChannel> pump, ChannelKey key)
	{
		if (!map.Remove(key, out var channel))
		{
			return;
		}

		pump.Remove(channel);
		_pumpDirty = true;
		channel.Dispose();
	}

	void RebuildPump()
	{
		_pumpDirty = false;

		var snapshot = new IDdsChannel[_readerPump.Count + _writerPump.Count];
		_readerPump.CopyTo(snapshot, 0);
		_writerPump.CopyTo(snapshot, _readerPump.Count);
		_pumpSnapshot = snapshot;
	}

	// ---- Internals -------------------------------------------------------------

	internal void LogWarning(string message) => _log(DdsHubLogLevel.Warning, message);

	internal void LogError(string message) => _log(DdsHubLogLevel.Error, message);

	bool RequireParticipant([NotNullWhen(true)] out DdsParticipant? participant)
	{
		participant = Participant;
		if (participant is null)
		{
			LogError("DdsHub is disposed; cannot create DDS entities.");
			return false;
		}

		return true;
	}

	/// <summary>
	/// Checks that <paramref name="topic"/> is unbound or already bound to
	/// <paramref name="type"/>. Binding happens only once an endpoint has actually been
	/// created, so a failed creation leaves the name free.
	/// </summary>
	bool CanBindTopic(string topic, Type type)
	{
		if (!_topicTypes.TryGetValue(topic, out var bound) || bound == type)
		{
			return true;
		}

		LogError(
			$"DDS topic '{topic}' is already bound to {bound.Name}; " +
			$"cannot also use it for {type.Name}. Cyclone caches topic entities by " +
			"name, so one name can only carry one type.");
		return false;
	}

	void WarnOnQosConflict(string topic, string role, DdsQos active, DdsQos? requested)
	{
		// null asks for the existing channel whatever its profile.
		if (requested is null || requested == active)
		{
			return;
		}

		LogWarning(
			$"DDS {role} for '{topic}' already exists with a different QoS profile; " +
			"the existing one is kept. Use Close<T>() first if you need to change it.");
	}

	static void DefaultLog(DdsHubLogLevel level, string message)
		=> Console.Error.WriteLine($"[DdsHub] {level}: {message}");

	static double DefaultClock() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

	readonly record struct ChannelKey(Type Type, string Topic);
}
