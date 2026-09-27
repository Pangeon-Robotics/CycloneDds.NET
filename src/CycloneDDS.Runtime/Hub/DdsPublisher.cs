using System;
using System.Threading.Tasks;

namespace CycloneDDS.Runtime.Hub;

/// <summary>
/// A writer for one topic, with optional publish-side throttling.
///
/// <para>Throttling is latest-value-wins: call <see cref="Set"/> as often as you like
/// (every frame, every input event) and at most <see cref="RateHz"/> writes per second
/// go on the wire, each carrying the most recent value. Nothing queues up, so the
/// receiver never sees a stale backlog.</para>
///
/// <para><see cref="HeartbeatSeconds"/> re-sends the last value when nothing has changed,
/// which is what you need to satisfy a subscriber's DEADLINE QoS or a ManualByTopic
/// liveliness lease.</para>
///
/// <para>Throttled and heartbeat writes are issued from <see cref="DdsHub.Pump"/>, so the
/// effective ceiling is the rate the host pumps at. Use <see cref="PublishNow"/> to bypass
/// the throttle entirely.</para>
/// </summary>
public sealed class DdsPublisher<T> : IDdsChannel
{
	readonly DdsHub _hub;
	readonly DdsWriter<T> _writer;
	readonly DdsQos _qos;

	T _pending = default!;
	bool _hasPending;

	T _last = default!;
	bool _hasLast;

	double _lastWriteTime = double.NegativeInfinity;
	double _nextMatchPoll;
	int _matchedReaders = -1;

	bool _disposed;

	internal DdsPublisher(DdsHub hub, DdsWriter<T> writer, string topic, DdsQos qos)
	{
		_hub = hub;
		_writer = writer;
		_qos = qos;
		Topic = topic;
	}

	public string Topic { get; }

	public Type DataType => typeof(T);

	/// <summary>
	/// The QoS profile this writer was created with; <see cref="DdsQos.SystemDefault"/> when
	/// none was given.
	/// </summary>
	public DdsQos Qos => _qos;

	/// <summary>
	/// Maximum writes per second for <see cref="Set"/>. 0 (the default) disables
	/// throttling and makes <see cref="Set"/> write immediately.
	/// </summary>
	public double RateHz { get; set; }

	/// <summary>
	/// Re-send the last written value if this many seconds pass with no write.
	/// 0 (the default) disables the heartbeat.
	/// </summary>
	public double HeartbeatSeconds { get; set; }

	/// <summary>Total samples handed to the DDS writer, throttling included.</summary>
	public long WriteCount { get; private set; }

	/// <summary>Calls to <see cref="Set"/> that were superseded before being sent.</summary>
	public long DroppedCount { get; private set; }

	/// <summary>Number of readers currently matched to this writer.</summary>
	public int MatchedReaders
	{
		get
		{
			if (_disposed)
			{
				return 0;
			}

			return (int)_writer.CurrentStatus.CurrentCount;
		}
	}

	/// <summary>
	/// Raised from <see cref="DdsHub.Pump"/> when the matched reader count changes.
	/// Polled roughly four times a second.
	/// </summary>
	public event Action<int>? MatchedReadersChanged;

	/// <summary>
	/// Offers a value for publication. Writes immediately when <see cref="RateHz"/> is 0,
	/// otherwise stores it as the pending value and lets the pump send it.
	/// </summary>
	public void Set(in T sample)
	{
		if (_disposed)
		{
			return;
		}

		if (RateHz <= 0.0)
		{
			Write(in sample, _hub.Now);
			return;
		}

		if (_hasPending)
		{
			DroppedCount++;
		}

		_pending = sample;
		_hasPending = true;
	}

	/// <summary>Writes immediately, ignoring <see cref="RateHz"/>, and resets the throttle window.</summary>
	public void PublishNow(in T sample)
	{
		if (_disposed)
		{
			return;
		}

		_hasPending = false;
		Write(in sample, _hub.Now);
	}

	/// <summary>Sends the pending value right now, if there is one.</summary>
	public void Flush()
	{
		if (_disposed || !_hasPending)
		{
			return;
		}

		_hasPending = false;
		Write(in _pending, _hub.Now);
	}

	/// <summary>Discards the pending value without sending it.</summary>
	public void Cancel()
	{
		_hasPending = false;
	}

	/// <summary>
	/// Marks the instance identified by the key fields of <paramref name="sample"/> as
	/// disposed for all readers.
	/// </summary>
	public void DisposeInstance(in T sample)
	{
		if (!_disposed)
		{
			_writer.DisposeInstance(in sample);
		}
	}

	/// <summary>Waits until at least one reader has matched this writer.</summary>
	public Task<bool> WaitForReaderAsync(TimeSpan timeout)
		=> _writer.WaitForReaderAsync(timeout);

	void IDdsChannel.Pump(double now)
	{
		if (_disposed)
		{
			return;
		}

		if (_hasPending && (RateHz <= 0.0 || now - _lastWriteTime >= 1.0 / RateHz))
		{
			_hasPending = false;
			Write(in _pending, now);
		}
		else if (HeartbeatSeconds > 0.0 && _hasLast && now - _lastWriteTime >= HeartbeatSeconds)
		{
			Write(in _last, now);
		}

		PollMatched(now);
	}

	void Write(in T sample, double now)
	{
		try
		{
			_writer.Write(in sample);
			WriteCount++;
		}
		catch (Exception e)
		{
			_hub.LogError($"DDS write failed on '{Topic}': {e.Message}");
			return;
		}

		_last = sample;
		_hasLast = true;
		_lastWriteTime = now;
	}

	void PollMatched(double now)
	{
		if (MatchedReadersChanged is null || now < _nextMatchPoll)
		{
			return;
		}

		_nextMatchPoll = now + 0.25;

		var count = (int)_writer.CurrentStatus.CurrentCount;
		if (count == _matchedReaders)
		{
			return;
		}

		_matchedReaders = count;
		try
		{
			MatchedReadersChanged?.Invoke(count);
		}
		catch (Exception e)
		{
			_hub.LogError($"DDS MatchedReadersChanged handler threw on '{Topic}': {e}");
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_hasPending = false;
		_writer.Dispose();
	}
}
