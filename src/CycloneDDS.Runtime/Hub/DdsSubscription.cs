using System;
using System.Collections.Generic;

namespace CycloneDDS.Runtime.Hub;

/// <summary>
/// A reader for one topic. One instance is shared by every subscriber to the same
/// (type, topic) pair; <see cref="DdsHub.Subscribe{T}"/> hands out tokens against it.
///
/// <para>The reader is drained from <see cref="DdsHub.Pump"/>, so handlers run on the
/// thread that pumps the hub. The most recent sample is also cached and readable via
/// <see cref="Latest"/> for code that would rather poll.</para>
/// </summary>
public sealed class DdsSubscription<T> : IDdsChannel where T : new()
{
	readonly DdsHub _hub;
	readonly DdsReader<T> _reader;
	readonly DdsQos _qos;
	readonly List<Action<T>> _handlers = [];

	Action<T>[] _snapshot = [];
	bool _snapshotStale;

	double _nextStatusPoll;
	int _matchedWriters = -1;

	bool _disposed;

	internal DdsSubscription(DdsHub hub, DdsReader<T> reader, string topic, DdsQos qos)
	{
		_hub = hub;
		_reader = reader;
		_qos = qos;
		Topic = topic;
	}

	public string Topic { get; }

	public Type DataType => typeof(T);

	/// <summary>
	/// The QoS profile this reader was created with; <see cref="DdsQos.SystemDefault"/> when
	/// none was given.
	/// </summary>
	public DdsQos Qos => _qos;

	/// <summary>The most recently received sample. Only meaningful when <see cref="HasValue"/>.</summary>
	public T? Latest { get; private set; }

	/// <summary>True once at least one sample has arrived.</summary>
	public bool HasValue { get; private set; }

	/// <summary>Total samples delivered since this subscription was created.</summary>
	public long ReceiveCount { get; private set; }

	/// <summary>Seconds since the last sample arrived, or <see cref="double.PositiveInfinity"/>.</summary>
	public double Age => HasValue ? _hub.Now - LastReceiveTime : double.PositiveInfinity;

	/// <summary>Timestamp (hub clock seconds) of the last received sample.</summary>
	public double LastReceiveTime { get; private set; }

	/// <summary>Samples taken from the reader per drain call. Raise for very high rate topics.</summary>
	public int BatchSize { get; set; } = 32;

	/// <summary>Maximum drain rounds per pump, a backstop against a runaway publisher.</summary>
	public int MaxRoundsPerFrame { get; set; } = 8;

	/// <summary>Number of writers currently matched to this reader.</summary>
	public int MatchedWriters
	{
		get
		{
			if (_disposed)
			{
				return 0;
			}

			return (int)_reader.CurrentStatus.CurrentCount;
		}
	}

	/// <summary>Writers currently matched <b>and</b> asserting liveliness.</summary>
	public int AliveWriters { get; private set; }

	/// <summary>Matched writers that have missed their liveliness lease.</summary>
	public int NotAliveWriters { get; private set; }

	/// <summary>
	/// Total REQUESTED_INCOMPATIBLE_QOS events: writers that were rejected because their
	/// offered QoS does not satisfy <see cref="Qos"/>. Non-zero means samples are being
	/// published on this topic that we will never receive.
	/// </summary>
	public uint IncompatibleQosCount { get; private set; }

	/// <summary>The policy the most recent writer was incompatible on.</summary>
	public DdsQosPolicy LastIncompatiblePolicy { get; private set; }

	/// <summary>
	/// Total missed DEADLINEs. Only ever non-zero when <see cref="DdsQos.Deadline"/> is
	/// set, since DDS has no deadline to miss otherwise.
	/// </summary>
	public uint DeadlinesMissed { get; private set; }

	/// <summary>
	/// Raised from <see cref="DdsHub.Pump"/> when the matched writer count changes.
	/// Polled roughly four times a second.
	/// </summary>
	public event Action<int>? MatchedWritersChanged;

	/// <summary>
	/// Raised when a writer is rejected for offering QoS incompatible with ours, with the
	/// policy it clashed on. This is the failure that looks exactly like silence: the
	/// writer is publishing and we are matched to nothing.
	/// </summary>
	public event Action<DdsQosPolicy>? IncompatibleQos;

	/// <summary>
	/// Raised when deadlines lapse, with the number missed since the last poll. Fires
	/// repeatedly while a writer stays silent, once per elapsed deadline period.
	/// </summary>
	public event Action<int>? DeadlineMissed;

	/// <summary>
	/// Raised when writer liveliness changes, with (alive, notAlive) counts. A writer
	/// moving to not-alive means it is still matched but has stopped asserting itself
	/// within its lease.
	/// </summary>
	public event Action<int, int>? LivelinessChanged;

	/// <summary>
	/// Registers a handler and returns a token. Dispose the token to unsubscribe;
	/// the underlying reader stays alive for other subscribers and for <see cref="Latest"/>.
	/// </summary>
	public IDisposable Add(Action<T> handler)
	{
		ArgumentNullException.ThrowIfNull(handler);

		_handlers.Add(handler);
		_snapshotStale = true;
		return new Token(this, handler);
	}

	void Remove(Action<T> handler)
	{
		if (_handlers.Remove(handler))
		{
			_snapshotStale = true;
		}
	}

	void IDdsChannel.Pump(double now)
	{
		if (_disposed)
		{
			return;
		}

		if (_snapshotStale)
		{
			_snapshot = [.. _handlers];
			_snapshotStale = false;
		}

		for (var round = 0; round < MaxRoundsPerFrame; round++)
		{
			if (!Drain(now))
			{
				break;
			}
		}

		PollStatus(now);
	}

	/// <summary>Takes one batch. Returns true if the batch was full, meaning more may be waiting.</summary>
	bool Drain(double now)
	{
		var batch = Math.Max(1, BatchSize);

		try
		{
			using var loan = _reader.Take(batch);
			var count = loan.Count;
			if (count == 0)
			{
				return false;
			}

			foreach (var sample in loan)
			{
				// Invalid samples carry only instance state (dispose / no-writers), no payload.
				if (!sample.IsValid)
				{
					continue;
				}

				var data = sample.Data;
				Latest = data;
				HasValue = true;
				LastReceiveTime = now;
				ReceiveCount++;

				Dispatch(data);
			}

			return count >= batch;
		}
		catch (Exception e)
		{
			_hub.LogError($"DDS take failed on '{Topic}': {e.Message}");
			return false;
		}
	}

	void Dispatch(T data)
	{
		var handlers = _snapshot;
		for (var i = 0; i < handlers.Length; i++)
		{
			try
			{
				handlers[i].Invoke(data);
			}
			catch (Exception e)
			{
				// One bad handler must not stall the pump for every other subscriber.
				_hub.LogError($"DDS handler threw on '{Topic}': {e}");
			}
		}
	}

	/// <summary>
	/// Reads the reader's statuses at a fixed 4 Hz. Every DDS status getter resets that
	/// status's <c>*_change</c> counters, so this must stay the only place a status is read
	/// for its deltas; a second such caller would silently eat the deltas this one reports.
	/// Absolute counts are unaffected by the reset, which is why <see cref="MatchedWriters"/>
	/// may read <c>CurrentCount</c> directly.
	/// </summary>
	void PollStatus(double now)
	{
		if (now < _nextStatusPoll)
		{
			return;
		}

		_nextStatusPoll = now + 0.25;

		PollMatched();
		PollQosStatus();
	}

	void PollMatched()
	{
		if (MatchedWritersChanged is null)
		{
			return;
		}

		var count = (int)_reader.CurrentStatus.CurrentCount;
		if (count == _matchedWriters)
		{
			return;
		}

		_matchedWriters = count;
		Raise(() => MatchedWritersChanged?.Invoke(count), nameof(MatchedWritersChanged));
	}

	void PollQosStatus()
	{
		var incompatible = _reader.RequestedIncompatibleQosStatus;
		if (incompatible.TotalCountChange > 0)
		{
			IncompatibleQosCount = incompatible.TotalCount;

			var policy = (DdsQosPolicy)incompatible.LastPolicyId;
			LastIncompatiblePolicy = policy;
			Raise(() => IncompatibleQos?.Invoke(policy), nameof(IncompatibleQos));
		}

		var deadline = _reader.RequestedDeadlineMissedStatus;
		if (deadline.TotalCountChange > 0)
		{
			DeadlinesMissed = deadline.TotalCount;

			var missed = deadline.TotalCountChange;
			Raise(() => DeadlineMissed?.Invoke(missed), nameof(DeadlineMissed));
		}

		var liveliness = _reader.LivelinessChangedStatus;
		if (liveliness.AliveCountChange != 0 || liveliness.NotAliveCountChange != 0)
		{
			AliveWriters = (int)liveliness.AliveCount;
			NotAliveWriters = (int)liveliness.NotAliveCount;

			int alive = AliveWriters, notAlive = NotAliveWriters;
			Raise(() => LivelinessChanged?.Invoke(alive, notAlive), nameof(LivelinessChanged));
		}
	}

	/// <summary>Invokes a status handler; one bad handler must not stall the pump.</summary>
	void Raise(Action invoke, string what)
	{
		try
		{
			invoke();
		}
		catch (Exception e)
		{
			_hub.LogError($"DDS {what} handler threw on '{Topic}': {e}");
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_handlers.Clear();
		_snapshot = Array.Empty<Action<T>>();
		_reader.Dispose();
	}

	sealed class Token : IDisposable
	{
		DdsSubscription<T>? _owner;
		Action<T>? _handler;

		public Token(DdsSubscription<T> owner, Action<T> handler)
		{
			_owner = owner;
			_handler = handler;
		}

		public void Dispose()
		{
			if (_handler is not null)
			{
				_owner?.Remove(_handler);
			}

			_owner = null;
			_handler = null;
		}
	}
}
