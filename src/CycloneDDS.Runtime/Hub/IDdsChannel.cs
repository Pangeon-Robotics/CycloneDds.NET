using System;

namespace CycloneDDS.Runtime.Hub;

/// <summary>
/// A channel owned by a <see cref="DdsHub"/> and serviced from <see cref="DdsHub.Pump"/>.
/// </summary>
internal interface IDdsChannel : IDisposable
{
	string Topic { get; }
	Type DataType { get; }
	void Pump(double now);
}
