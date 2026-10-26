# FastCycloneDDS C# Bindings

[![CI](https://github.com/pjanec/CycloneDds.NET/actions/workflows/ci.yml/badge.svg)](https://github.com/pjanec/CycloneDds.NET/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/CycloneDDS.NET.svg)](https://www.nuget.org/packages/CycloneDDS.NET/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

A .NET binding for Eclipse Cyclone DDS with an idiomatic C# API, built for wire compatibility with ROS 2 (`rmw_cyclonedds_cpp`).

See [detailed technical overview](DetailedOverview.md).

## Installation

### Using the NuGet Package (Recommended)

Install the CycloneDDS.NET package from NuGet:

```bash
dotnet add package CycloneDDS.NET
```

This single package includes:

- **Runtime Library:** High-performance managed bindings (targets `net8.0`; runs on .NET 8 and later).
- **Native Assets:** Pre-compiled native runtime and IDL compiler for both **Windows x64** (`ddsc.dll`, `idlc.exe`) and **Linux x64** (`libddsc.so`, `idlc`). NuGet selects the correct runtime asset per platform automatically.
- **Build Tools:** Automatic C# code generation during build.

### Supported platforms

| Platform | Native runtime | Target requirement |
| :--- | :--- | :--- |
| Windows x64 | `ddsc.dll` | [Visual C++ Redistributable for Visual Studio 2022](https://aka.ms/vs/17/release/vc_redist.x64.exe) installed on the target machine. OpenSSL is bundled. |
| Linux x64 | `libddsc.so` | A glibc-based distribution (e.g. Ubuntu/Debian) with OpenSSL 3.x (`libssl3`), which is preinstalled on all current mainstream distributions. |

> **On OpenSSL.** The native runtime is built with DDS Security and TCP+TLS enabled, so
> `ddsc.dll` / `libddsc.so` import `libssl` and `libcrypto` directly and will not load
> without them. Windows has no system-provided OpenSSL, so the package bundles
> `libcrypto-3-x64.dll` and `libssl-3-x64.dll` (Apache-2.0 — see `ThirdPartyNotices.txt`)
> and places them next to your application automatically. On Linux they come from the
> distribution and nothing is bundled.
>
> The DDS Security plugins (`dds_security_auth`, `dds_security_ac`,
> `dds_security_crypto`) ship alongside them, and are loaded only when your domain
> configuration contains a `<Security>` section.

### Working with Source Code

If you want to build the project from source or contribute:

1. **Clone the repository** (recursively, to get the native submodule):

    ```bash
    git clone --recursive https://github.com/pjanec/CycloneDds.NET.git
    cd CycloneDds.NET
    ```

2. **Build the native libraries** for your platform:
    - **Windows** (PowerShell): `.\build\native-win.ps1`
    - **Linux** (bash): `build/native-linux.sh Release`

    Then build and test the managed solution:

    ```bash
    dotnet build CycloneDDS.NET.Core.slnf -c Release
    dotnet test  CycloneDDS.NET.Core.slnf -c Release
    ```

    On Windows you can instead run the one-stop script `.\build\build-and-test.ps1`, which builds the native artifacts (if missing), builds the solution, and runs the tests.

3. **Requirements:**
    - **.NET SDK:** the solution targets `net8.0`, but building requires the **.NET 10 SDK** (the code uses C# 13 language features). Produced binaries still run on .NET 8+.
    - **CMake 3.16+** on your `PATH`.
    - **Windows:** Visual Studio 2022 with the *C++ Desktop Development* workload (for native compilation), plus the OpenSSL 3.x development files — `winget install ShiningLight.OpenSSL.Prod`. `build\native-win.ps1` looks in `C:\Program Files\OpenSSL`, `C:\Program Files\OpenSSL-Win64` and `C:\OpenSSL-Win64`; set `OPENSSL_ROOT_DIR` to override.
    - **Linux:** a C toolchain, `patchelf` and the OpenSSL headers — e.g. `sudo apt-get install -y cmake build-essential patchelf libssl-dev`.

## Key Features

### Schema & Interoperability

- **Code-first types:** declare `[DdsStruct]` partial structs or classes in C#. The build generates their IDL, an XCDR1 serializer, and the Cyclone topic descriptor.
- **ROS 2 compatible:** types are IDL `@final` and travel as XCDR1, the representation `rmw_cyclonedds_cpp` uses. Strings, bounded and unbounded sequences, fixed arrays, nested messages and enums all interoperate (see below).
- **Validated writes:** Cyclone checks every serialized sample against the idlc-generated descriptor before sending it, so a sample that does not match the IDL is rejected instead of reaching the wire.
- **IDL output:** the generated `.idl` files are standard OMG IDL, usable by other DDS implementations and tools, and are shared between assemblies automatically.

### Developer Experience

- **Compile-time binding:** generated types implement `IDdsType<T>`; readers and writers are constrained to it, so a missing or invalid type is a build error, not a runtime one.
- **Build-time diagnostics:** unsupported members are reported as compiler errors at the offending line.
- **Async/Await:** `WaitDataAsync` and `StreamAsync` for task-based consumers.
- **Client-side filtering:** a `Predicate<T>` per reader, applied while iterating.
- **Sender Tracking:** identify the source application (computer, PID, custom app id) of every message.
- **QoS profiles:** `DdsQos` presets (`Reliable`, `BestEffort`, `KeepAll`, `Latched`, ...) adjustable with `with` expressions.

### Partitioning & Monitoring

- **Partition Support:** set a partition on a participant once and every reader/writer inherits it, or override it per reader/writer.
- **Zero-Allocation WaitSet:** monitor 100+ readers on a single OS thread. `DdsWaitSet.Wait(Span<IDdsReader>, timeout, ct)` never allocates in the hot path and supports `CancellationToken`.

---

## 1. Defining Data (The Schema)

Declare a `partial` struct or class with `[DdsStruct]`. Its instance fields and auto-properties, in declaration order, are the members of the IDL struct.

```csharp
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CycloneDDS.Schema;

namespace Factory.Monitoring;

[DdsStruct]
public partial struct GeoPoint { public double Lat; public double Lon; }

[DdsStruct]
public partial struct SensorData
{
    public int SensorId;
    public double Value;
    public SensorStatus Status;          // byte-backed enum: @bit_bound(8)
    [DdsBound(32)] public string Location; // string<32>
    public FloatBuffer8 Measurements;    // float[8], stored inline
    public GeoPoint Origin;              // nested struct
    public List<double> History;         // sequence<double>
}

[InlineArray(8)]
public struct FloatBuffer8 { private float _element0; }

public enum SensorStatus : byte { Offline, Online, Error }
```

The full C# → IDL mapping, the attributes that refine it, and the rules the generator enforces are described in [Code Generation](IDL-GENERATION.md).

### Configuration & QoS

QoS is chosen per reader and writer with a `DdsQos` profile. A null profile leaves every policy at Cyclone's default.

```csharp
var qos = DdsQos.Reliable with { Durability = DdsDurability.TransientLocal, HistoryDepth = 1 };
using var writer = new DdsWriter<SensorData>(participant, "MachineState", qos);
```

---

## 2. Basic Usage

### Publishing

```csharp
using var participant = new DdsParticipant();
using var writer = new DdsWriter<SensorData>(participant, "SensorData", DdsQos.Reliable);

var data = new SensorData { SensorId = 1, Value = 25.5, Location = "Factory_A", Status = SensorStatus.Online };
data.Measurements[0] = 1.0f;

writer.Write(data);
```

`Write` serializes into a pooled buffer. It throws `ArgumentException` if a member breaks its bound or fixed length, and `DdsException` if Cyclone rejects the sample.

### Subscribing (Polling)

A read or take returns a loan, which holds Cyclone's references to the samples until it is disposed.

```csharp
using var reader = new DdsReader<SensorData>(participant, "SensorData", DdsQos.Reliable);

using var loan = reader.Take(maxSamples: 10);
foreach (var sample in loan)
{
    if (sample.IsValid)
    {
        SensorData data = sample.Data; // deserialized from Cyclone's buffer on access
        Console.WriteLine($"Received: {data.SensorId} = {data.Value}");
    }
    else
    {
        // An instance state change without data, e.g. the last writer went away
        Console.WriteLine($"State: {sample.Info.InstanceState}");
    }
}
```

`sample.Data` deserializes on each access and throws for samples without data.

---

## 3. Async/Await (Modern Loop)

```csharp
while (await reader.WaitDataAsync())
{
    using var scope = reader.Take();
    foreach (var sample in scope)
    {
        if (sample.IsValid) Process(sample.Data);
    }
}

// Or as a stream of samples
await foreach (var data in reader.StreamAsync(cancellationToken))
{
    Process(data);
}
```

---

## 4. Filtering

A reader's filter is applied while iterating a loan; samples it rejects are skipped. Each sample is deserialized once, and the value the filter saw is the one `sample.Data` returns.

```csharp
reader.SetFilter(data => data.Value > 100.0 && data.Location == "Lab_1");

using var scope = reader.Take();
foreach (var highValueSample in scope)
{
    // Only samples above 100.0 from Lab_1, plus samples without data
}

reader.SetFilter(null); // Clear filter
```

---

## 5. ROS 2

Readers and writers interoperate with ROS 2 nodes running `rmw_cyclonedds_cpp`. Three things must line up with ROS's conventions:

| ROS 2 | C# |
| :--- | :--- |
| Topic `/chatter` | Topic name `"rt/chatter"` |
| Type `std_msgs/msg/String` | DDS type name `std_msgs::msg::dds_::String_`: C# type `String_` in namespace `std_msgs.msg.dds_`, or any C# name with `[DdsIdlModule("std_msgs::msg::dds_")]` on a type named `String_` |
| Default QoS (reliable, volatile, keep last 10) | `DdsQos.Reliable` |

Field types follow the ROS interface definition:

```csharp
namespace builtin_interfaces.msg.dds_
{
    [DdsStruct] public partial struct Time_ { public int sec; public uint nanosec; }
}

namespace std_msgs.msg.dds_
{
    [DdsStruct] public partial struct Header_ { public builtin_interfaces.msg.dds_.Time_ stamp; public string frame_id; }
}

namespace sensor_msgs.msg.dds_
{
    [DdsStruct]
    public partial class JointState_
    {
        public std_msgs.msg.dds_.Header_ header;
        public List<string> name;
        public List<double> position;
        public List<double> velocity;
        public List<double> effort;
    }
}
```

```csharp
using var reader = new DdsReader<sensor_msgs.msg.dds_.JointState_>(participant, "rt/joint_states", DdsQos.Reliable);
```

ROS 2 topics are keyless, so a reader's history depth is shared by all writers of a topic.

`wstring` fields are not supported with ROS 2: `[DdsWideString]` uses Cyclone's encoding, which `rmw_cyclonedds_cpp` does not. The test suite exercises each supported shape against the `ros2` CLI in both directions (`tests/CycloneDDS.Runtime.Tests/Ros`); those tests run when a ROS 2 installation is found.

---

## 6. Sender Tracking (Identity)

Identify exactly which application instance sent a message. Essential for multi-process debugging.

### Sender Configuration

```csharp
var config = new SenderIdentityConfig 
{ 
    AppDomainId = 1, 
    AppInstanceId = 100 
};

// Enable tracking BEFORE creating writers
participant.EnableSenderTracking(config);

// Now, every writer created by this participant automatically broadcasts identity
using var writer = new DdsWriter<SensorData>(participant, "SensorData");
```

### Receiver Usage

```csharp
// Enable tracking on the reader
reader.EnableSenderTracking(participant.SenderRegistry);

using var scope = reader.Take();
for (int i = 0; i < scope.Count; i++)
{
    // Returns: ComputerName, ProcessName, ProcessId, AppDomainId, etc.
    var sender = scope.GetSender(i); 
    var msg = scope[i];

    if (sender != null)
    {
        Console.WriteLine($"[{sender.Value.ComputerName} : PID {sender.Value.ProcessId}] sent {msg.Value}");
    }
}
```

---

## 7. Status & Discovery

Know when peers connect or disconnect using standard C# Events.

```csharp
// Writer Side
writer.PublicationMatched += (s, status) => 
{
    if (status.CurrentCountChange > 0)
        Console.WriteLine($"Subscriber connected! Total: {status.CurrentCount}");
    else
        Console.WriteLine("Subscriber lost.");
};

// Reliable Startup (Wait for Discovery)
// Solves the "Lost First Message" problem
await writer.WaitForReaderAsync(TimeSpan.FromSeconds(5));
writer.Write(new SensorData { SensorId = 1 }); // Guaranteed to have a route
```

---

## 8. Partitions

DDS partitions let you divide a domain into named logical channels. Readers and writers only communicate within the same partition, making it easy to run multiple isolated subsystems on the same DDS domain (e.g. separate a monitoring plane from a control plane, or multiplex tenants).

### Set a partition on the participant (inherited by all readers/writers)

```csharp
// All readers and writers created from this participant will use "monitoring" automatically.
using var participant = new DdsParticipant(domainId: 0, defaultPartition: "monitoring");

using var reader = new DdsReader<SensorData>(participant, "SensorData");
using var writer = new DdsWriter<SensorData>(participant, "SensorData");
```

### Override the partition per reader / writer

```csharp
using var participant = new DdsParticipant(0, defaultPartition: "*"); // wildcard default

// This writer specifically targets the "control" partition.
using var controlWriter = new DdsWriter<SensorData>(
    participant, "SensorData", partition: "control");

// This reader stays on the default "*" partition — sees everything.
using var broadcastReader = new DdsReader<SensorData>(participant, "SensorData");
```

### Resolution order

```
per-reader / per-writer partition  →  participant.DefaultPartition  →  (no partition)
```

---

## 9. WaitSet — Efficient Multi-Reader Monitoring

`DdsWaitSet` provides a native-backed mechanism for sleeping on many readers simultaneously on **a single OS thread**. This is ideal for monitoring applications that track 100+ topics and do not want the overhead of spawning a background `Task` per reader.

### Basic usage

```csharp
using var participant = new DdsParticipant(0, defaultPartition: "*");

// Create readers for every topic you want to monitor
using var tempReader    = new DdsReader<TemperatureEvent>(participant, "Temperature");
using var pressReader   = new DdsReader<PressureEvent>(participant, "Pressure");
using var statusReader  = new DdsReader<MachineStatus>(participant, "MachineStatus");

// Create WaitSet and attach all readers
using var waitset = new DdsWaitSet(participant);
waitset.Attach(tempReader);
waitset.Attach(pressReader);
waitset.Attach(statusReader);

// Pre-allocate result buffer once — no allocation inside the loop
IDdsReader[] triggered = new IDdsReader[16];

var cts = new CancellationTokenSource();

while (!cts.IsCancellationRequested)
{
    // Blocks until at least one reader has data, or the timeout expires, or ct is cancelled.
    // Zero allocation in this hot path.
    int count = waitset.Wait(triggered.AsSpan(), timeout: TimeSpan.FromSeconds(1), cts.Token);

    for (int i = 0; i < count; i++)
    {
        switch (triggered[i])
        {
            case DdsReader<TemperatureEvent> r:
                using (var loan = r.Take()) { /* handle temp */ }
                break;

            case DdsReader<PressureEvent> r:
                using (var loan = r.Take()) { /* handle pressure */ }
                break;

            case DdsReader<MachineStatus> r:
                using (var loan = r.Take()) { /* handle status */ }
                break;
        }
    }
}
```

### Attach / Detach at runtime

Readers can be added or removed while the WaitSet is not waiting, making the monitored set dynamic:

```csharp
// Start watching a new topic at runtime
var newReader = new DdsReader<AlarmEvent>(participant, "Alarms");
waitset.Attach(newReader);

// Stop watching (and dispose the reader when no longer needed)
waitset.Detach(newReader);
newReader.Dispose();
```

### CancellationToken

Pass a `CancellationToken` to `Wait` to interrupt the blocking native call safely from any thread:

```csharp
cts.Cancel(); // triggers the native guard condition, unblocks Wait() instantly
```

### Performance characteristics

| Operation | Allocation | Notes |
| :--- | :--- | :--- |
| `Wait(...)` hot path | **0 Bytes** | `ArrayPool` rent inside; result written into caller's `Span` |
| `Attach` / `Detach` | Small (one-time) | `GCHandle` + dictionary entry per reader |
| Cancellation callback | **0 Bytes** | Triggers native guard condition via P/Invoke |

---

## Examples

### Hello World

A complete "Hello World" example that demonstrates creating a topic, publishing, and subscribing in a single application can be found in [`examples/HelloWorld`](examples/HelloWorld).

This example is designed to verify the NuGet package installation and basic functionality using the locally built package.

To run it:

1. Build the packages: `.\build\pack.ps1`
2. Run the example:

   ```bash
   cd examples/HelloWorld
   dotnet run
   ```

---

## Dependencies

The `CycloneDDS.NET` package bundles these internal components:

- **Managed Libraries:** `CycloneDDS.Core` (CDR encoding), `CycloneDDS.Schema` (attributes), `CycloneDDS.Runtime`
- **Build Tools:** `CycloneDDS.CodeGen` (code generator)
- **Native Assets (Windows x64):** `ddsc.dll` (Cyclone DDS), `idlc.exe` (IDL Compiler), `cycloneddsidljson.dll` (IDL JSON plugin)
- **Native Assets (Linux x64):** `libddsc.so` (Cyclone DDS), `idlc` (IDL Compiler), `libcycloneddsidljson.so` (IDL JSON plugin)

## Performance Characteristics

| Operation | Allocation | Note |
| :--- | :--- | :--- |
| **Write** | Payload buffer is pooled | Serialized once into a pooled buffer, copied once by Cyclone |
| **Read / Take** | Pooled arrays | Samples stay in Cyclone's buffers until accessed |
| **`sample.Data`** | Allocates reference-type members | Deserialized directly from Cyclone's buffer |
| **Sender Lookup** | **0 Bytes** | O(1) Dictionary Lookup |
| **Async Wait** | ~80 Bytes | One Task per `await` cycle |
| **WaitSet.Wait** | **0 Bytes** | Span output + ArrayPool rent; no heap in hot path |
