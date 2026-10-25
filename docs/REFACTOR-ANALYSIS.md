# CycloneDDS.NET — Project Structure & Refactor Analysis

Snapshot date: 2026-10-01. Branch: `feature/codegen-sequences` (HEAD `b7f8463`).
Purpose: reference for future refactoring work. Primary goal of the project owner is
**interoperation with ROS 2 under `rmw_cyclonedds_cpp`, topics only**; broader Cyclone DDS
compatibility is secondary.

Baseline at snapshot: `dotnet test CycloneDDS.NET.Core.slnf -c Release` on Linux, .NET SDK 10.0.112 —
all 6 test assemblies passed (500 tests, 1 skipped). No ROS 2 interop tests exist in the repo.

---

## Part 1 — Project Structure

### 1.1 Top-level layout

```
CycloneDds.NET/
├── cyclonedds/                 git submodule: fork of Eclipse Cyclone DDS (see 1.6)
├── src/                        shipped managed libraries
│   ├── CycloneDDS.Core/        low-level native memory helpers (no DDS calls)
│   ├── CycloneDDS.Schema/      attributes + wrapper types used to author DDS types in C#
│   └── CycloneDDS.Runtime/     P/Invoke layer + participant/reader/writer/waitset/Hub API
├── tools/                      build-time tools (shipped inside the NuGet package under tools/)
│   ├── CycloneDDS.CodeGen/            C# → IDL → idlc → generated C# (runs on every build)
│   ├── CycloneDDS.Compiler.Common/    idlc process runner + idlc JSON output parser
│   ├── CycloneDDS.IdlImporter/        IDL → C# DSL converter (CLI)
│   ├── CycloneDDS.Compiler.Common.Tests/
│   └── CycloneDDS.IdlImporter.Tests/
├── tests/
│   ├── CycloneDDS.Runtime.Tests/      integration tests against native ddsc (largest suite)
│   ├── CycloneDDS.CodeGen.Tests/      generator unit tests (string output + compile-and-run)
│   ├── CycloneDDS.Core.Tests/
│   ├── CycloneDDS.Schema.Tests/
│   ├── CycloneDDS.Native.Verify/      csproj only, no source files (empty project)
│   ├── IdlJson.Tests/                 C harness (cJSON) verifying the idljson plugin
│   └── e2e/                           two .idl files
├── examples/
│   ├── HelloWorld/                    NuGet-consumer sample
│   ├── IdlImportDemo/                 CommonLib/AppLib/App: cross-assembly IDL import demo
│   └── PackageSmokeTest/              package verification app
├── build/
│   ├── native-linux.sh / native-win.ps1   build cyclonedds submodule → artifacts/native/<rid>/
│   ├── build-and-test.ps1, pack.ps1, test-package.{ps1,sh}
│   ├── props/CycloneDDS.props         NuGet build/ asset: enables codegen for DIRECT references only
│   └── targets/CycloneDDS.targets     NuGet buildTransitive/ asset: the codegen MSBuild pipeline
├── docs/                       historical design/task-tracker docs (MARSHAL-*, ZERO-COPY-*, nugetizing/)
├── .archive/docs/              older design docs
├── .github/workflows/ci.yml    Linux CI: native build, managed build, Runtime.Tests only, pack
├── debug_tool/                 DebugOffsets.csproj only (no sources tracked)
├── dump.bat, *.dumpfilter      LLM code-dump helpers (Windows paths)
├── Directory.Build.props       package metadata, NBGV, SourceLink
├── CycloneDDS.NET.sln / CycloneDDS.NET.Core.slnf
└── README.md, DetailedOverview.md, IDL-GENERATION.md, IDL-IMPORT.md, TODO.md, CHANGELOG.md, ...
```

`artifacts/` (gitignored) holds the native build output: `libddsc.so`, `idlc`,
`libcycloneddsidl*.so`, `libcycloneddsidljson.so` (Linux) / the `.dll`/`.exe` equivalents (Windows).

### 1.2 Packaging

Only one NuGet package is produced: **`CycloneDDS.NET`** (from `src/CycloneDDS.Runtime/CycloneDDS.Runtime.csproj`).
It bundles:

- `lib/net8.0/`: `CycloneDDS.Runtime.dll` + manually packed `CycloneDDS.Core.dll`, `CycloneDDS.Schema.dll` (+ xml/pdb).
- `runtimes/{linux-x64,win-x64}/native/`: `ddsc`, DDS Security plugins, OpenSSL (Windows only).
- `tools/`: `idlc` + its libraries, `CycloneDDS.CodeGen.dll` + Roslyn, `CycloneDDS.IdlImporter.dll`.
- `buildTransitive/CycloneDDS.NET.targets` (= `build/targets/CycloneDDS.targets`).
- `build/CycloneDDS.NET.props` (= `build/props/CycloneDDS.props`).
- `native_version.json` (fork + upstream base commit, computed at pack time via git).

Target framework: `net8.0`; LangVersion 12 in csproj, but README states .NET 10 SDK is required to build.

### 1.3 `src/CycloneDDS.Core` (not packaged separately)

| File | Contents |
|---|---|
| `NativeArena.cs` | `ref struct NativeArena` — bump allocator over a pinned `byte[]`. Head struct first, then strings/sequence buffers appended. `CreateString`, `CreateSequence<T>`, `AllocateArray<TNative>`. |
| `DdsNativeTypes.cs` | `DdsSequenceNative` — mirrors C `dds_sequence_t` `{uint32 _maximum; uint32 _length; T* _buffer; bool _release}`. |
| `DdsTextEncoding.cs` | UTF-8 helpers: size, native→string, span from `char*`. |

The csproj also copies all native artifacts to the output directory.

### 1.4 `src/CycloneDDS.Schema` (authoring surface)

- **Type-level attributes**: `[DdsStruct]`, `[DdsUnion]`, `[DdsExtensibility(Final|Appendable|Mutable)]`
  (default when absent = **Appendable**), `[DdsIdlFile]`, `[DdsIdlModule]`, `[DdsIdlMapping]`
  (assembly-level, emitted by codegen for cross-assembly IDL resolution), `[DdsTypeName]` (**unused**),
  `[DdsTypeFormat]` (DDS-monitor leftover).
- **Field-level**: `[DdsKey]`, `[DdsId]`, `[DdsOptional]`, `[DdsBound]`, `[MaxLength]`, `[ArrayLength]`.
- **Union**: `[DdsDiscriminator]`, `[DdsCase]`, `[DdsDefaultCase]`.
- `[DdsManaged]` — opt-in to GC types (`string`, `List<T>`).
- **Wrapper types**: `FixedString32/64/128/256` (+ `*View`), `BoundedSeq<T>`.
- `TypeMap/DdsTypeMapAttribute`, `DdsWire` — **unused**.
- `Formatting/FormattedToken`, `TokenType` — DDS-monitor leftover.
- `Polyfills.cs` — `#if !NET5_0_OR_GREATER`; dead on net8.

### 1.5 `src/CycloneDDS.Runtime`

| Area | Files | Notes |
|---|---|---|
| Interop | `Interop/DdsApi.cs` (713 lines) | All `[DllImport("ddsc")]`, status structs, `DdsSampleInfo`, `DdsBuiltinTopicEndpoint`, `DdsGuid` (marked `[DdsStruct]` → runtime itself runs codegen). Many declared-but-unused imports (`*cdr*`, `serdata*`, `take_with_collector`, `iovec`). |
| | `Interop/DdsEntityHandle.cs` | Owns an entity; `dds_delete` on Dispose **and in a finalizer**. |
| Participant | `DdsParticipant.cs` | Creates participant; ref-counted topic cache keyed by (name, CLR type); builds native `dds_topic_descriptor_t` from generated statics via **reflection**; sender-tracking hooks. |
| Type metadata | `DdsTypeSupport.cs`, `DdsKeyDescriptor.cs` | Reflection lookup of `GetDescriptorOps`, `GetKeyDescriptors`, `MarshalFromNative`; `GetTypeName<T>()` = CLR full name with `.`→`::`. |
| Writer | `DdsWriter.cs` | Static ctor binds generated `GetNativeSize`/`MarshalToNative` (+ key variants) via reflection. Write = rent `byte[]` → marshal to C layout → `dds_write`. Dispose/Unregister/LookupInstance, `PublicationMatched` event, `WaitForReaderAsync`, status getters. |
| Reader | `DdsReader.cs` | `Read/Take` (loans, `dds_*_mask`), instance read/take, `WaitDataAsync`, `StreamAsync`, `SetFilter(Predicate<T>)`, `SubscriptionMatched`, status getters, sender tracking. |
| Loans | `DdsLoan.cs`, `DdsSample.cs` | `ref struct` loan over pooled `IntPtr[]` + `DdsSampleInfo[]`; `DdsSample.Data` unmarshals via `DdsTypeSupport.FromNative` on every access. |
| QoS | `DdsQos.cs` (record with presets `SystemDefault/Default/Reliable/BestEffort/KeepAll/Latched`), `DdsReliability`, `DdsDurability`, `DdsHistoryKind`, `DdsLiveliness`, `DdsQosPolicy` (ids 0–22 only) | Null policy = not set → Cyclone default. |
| WaitSet | `DdsWaitSet.cs`, `IDdsReader.cs` | Native waitset + read conditions + guard condition for cancellation. |
| Hub | `Hub/DdsHub.cs`, `DdsPublisher.cs`, `DdsSubscription.cs`, `IDdsChannel.cs` | Single-threaded pump-based facade (Godot-oriented), per-(type,topic) channels, rate limiting, heartbeats, status counters. Written by the project owner; ROS-oriented examples (`rt/chatter`, `String_`). |
| Tracking | `Tracking/SenderIdentity*.cs`, `SenderRegistry.cs`, `CycloneDdsXmlConfig.cs` | Proprietary sender-identity side topic `__FcdcSenderIdentity`. Not ROS-relevant. |
| Misc | `DdsException.cs`, `DdsInstanceHandle.cs`, `DdsStateEnums.cs`, `Memory/Arena.cs` (thin `ArrayPool<byte>` wrapper), `AssemblyInfo.cs` | |

### 1.6 Native submodule `cyclonedds/`

- URL `https://github.com/pjanec/cyclonedds.git`, branch `pj/idlcjson`, `project(CycloneDDS VERSION 0.11.0)`.
- Delta from upstream = 3 commits by pjanec adding the **`idljson` idlc plugin** (`idlc -l json`):
  emits a JSON file with types, members, and the full topic descriptor (ops array, keys, flagset, size, align).
- Built with security + SSL enabled (`build/native-linux.sh`, `build/native-win.ps1`).
- ROS 2 Jazzy ships Cyclone 0.10.x; wire interop with this 0.11 build was verified (Part 2).

### 1.7 Build-time code generation pipeline

MSBuild (`build/targets/CycloneDDS.targets`, in-repo shim `tools/CycloneDDS.CodeGen/CycloneDDS.targets`):

1. `CopyReferencedIdlFiles` — copies `*.idl` from referenced assemblies' bin dirs and sibling projects' `obj/Generated` into this project's generated dir.
2. `BuildCycloneDDSCodeGen` — in-repo only: builds the generator from source.
3. `CycloneDdsGenerate` (BeforeTargets=CoreCompile, stamp-file incremental) — a regex task `CycloneDdsDetectSchema` checks for `[Dds*]`/enums; if present, runs
   `dotnet CycloneDDS.CodeGen.dll <projectDir> <generatedDir> <references;...>`. Copies generated `.idl` to OutputPath.
4. `IncludeCycloneDdsGeneratedFiles` — adds `<generatedDir>/**/*.cs` to `Compile`.
5. Windows-only native staging target; clean target.

`CycloneDDS.CodeGen` internals (`CodeGenerator.Generate`):

| Phase | Component | Action |
|---|---|---|
| Discover | `SchemaDiscovery` (477) | Globs `*.cs` under project dir (excluding `obj`, `bin`, any `Generated` folder), builds its **own** Roslyn compilation with passed references, extracts `TypeInfo`/`FieldInfo` (field type kept as **string** `TypeName`). |
| Validate | `SchemaValidator` (200), `ManagedTypeValidator` (71) | Type support / `[DdsManaged]` rules. |
| Register | `GlobalTypeRegistry` (95), `IdlTypeDefinition` | Local types → IDL file (per source file name) + module (namespace with `::`); external types resolved via `[assembly: DdsIdlMapping]`. |
| Emit IDL | `IdlEmitter` (819) | One `.idl` per source file; `#include` for cross-file deps; modules **ordered alphabetically**. |
| Assembly metadata | `CodeGenerator.EmitAssemblyMetadata` | `CycloneDDS.IdlMap.g.cs` with `[assembly: DdsIdlMapping(...)]`. |
| Descriptors | `IdlcRunner` + `IdlJsonParser` (Compiler.Common) | Runs `idlc -l json`, parses ops/keys/flagset/size/align per type. |
| Emit C# | `SerializerEmitter` (1582), `ViewEmitter` (1109), `ViewExtensionsEmitter` (99), `TypeMapper` (87) | One `<FullName>.g.cs` per struct/union containing: partial type with `GetNativeSize`, `GetNativeHeadSize`, `MarshalToNative`, key variants, `MarshalFromNative`, `FromNative`, `_ops`, `GetDescriptorOps/KeyDescriptors/Flagset/Size/Align`; a `<Name>_Native` C-layout "ghost" struct; a `<Name>View` ref struct; `<Name>Extensions` (`AsView`, `ReadCopied`). |
| Cleanup | | Deletes stale `.cs` files in output dir. |

### 1.8 Runtime data flow

**Topic creation** (`DdsParticipant.RegisterTopic<T>`): reflection → ops `uint[]` (pinned), key descriptors,
flagset/size/align → `Marshal.AllocHGlobal` a `dds_topic_descriptor_t` (type_information/type_mapping = null,
`restrict_data_representation` = 0) → `dds_create_topic(participant, desc, name, NULL qos, NULL)`.
Matching with remote endpoints therefore relies on **type name only** (no XTypes TypeInformation).

**Write**: `T` (managed) → `GetNativeSize` → rent `byte[]` → pin → `MarshalToNative` writes `<T>_Native`
head + strings/sequences into the arena → `dds_write(writer, ptr)` → Cyclone serializes from C layout using `_ops`.

**Read**: `dds_take_mask/dds_read_mask` with null sample pointers (loan) → `DdsLoan<T>` →
`DdsSample<T>.Data` → `MarshalFromNative` copies C layout → managed `T`; or `AsView()` → zero-copy `ref struct`.
`DdsLoan.Dispose` → `dds_return_loan`.

Consequence: every type exists in three forms that must agree — managed C# type, generated `_Native`
C-layout struct (layout computed by the C# generator), and the ops array whose member offsets were computed by
idlc for the C struct. **Nothing verifies that the `_Native` layout matches the ops offsets.**

### 1.9 IdlImporter

`Importer` (465) walks the include graph from a master IDL (`--source-root`), runs `idlc -l json` per file,
and `CSharpEmitter` (356) + `TypeMapper` (220) write one C# file per IDL file under
`<output-root>/<relative path>.cs`, preserving modules as namespaces and emitting `[DdsExtensibility]` from the IDL
(defaults to Final, matching idlc).

### 1.10 Tests & CI

- `CycloneDDS.Runtime.Tests` — uses the in-repo codegen shim; covers read/take, keyed topics, QoS behavior,
  waitset, async, Hub, sequences, fixed struct arrays, sender tracking. Contains debug leftovers
  (`OffsetDebug.cs`, `CheckSizes.cs`, `KeyedMessages/temp_c`, `temp_test` C files).
- `CycloneDDS.CodeGen.Tests` — emitter output and compile-and-execute tests.
- CI (`.github/workflows/ci.yml`) runs **only** `CycloneDDS.Runtime.Tests` on Linux; codegen/importer tests are not run in CI.

---

## Part 2 — ROS 2 Interop Verification

Environment: ROS 2 Jazzy at `/opt/ros/jazzy`, `RMW_IMPLEMENTATION=rmw_cyclonedds_cpp`, domain 0.
Probe projects were throwaway (scratchpad), referencing `src/*` projects and importing
`tools/CycloneDDS.CodeGen/CycloneDDS.targets`, same as `tests/CycloneDDS.Runtime.Tests.csproj`.

| Case | ROS → C# | C# → ROS |
|---|---|---|
| `std_msgs/String`, no extensibility attribute (= `@appendable`) | OK | **FAIL** — writer `OfferedIncompatibleQos`, `LastPolicyId = 25` (DATA_REPRESENTATION) |
| `std_msgs/String` with `[DdsExtensibility(Final)]` | OK | OK |
| `sensor_msgs/JointState` (nested `Header_`/`Time_`, `List<string>`, `List<double>`), Final | OK | OK — **only after** splitting each namespace into its own `.cs` file (bug B2) |
| `sensor_msgs/Imu` with `[InlineArray(9)]` covariance, Final | OK | OK (all values verified) |
| `sensor_msgs/Imu` with `[ArrayLength(9)] double[]` | — | **generated code does not compile** (bug C1) |

Naming used to match `rmw_cyclonedds`: topic `rt/<name>`, type `<pkg>::msg::dds_::<Name>_`, i.e. C# namespace
`<pkg>.msg.dds_` and type name `<Name>_` (because the runtime derives the DDS type name from the CLR full name).

### Reproduction recipe

`probe.csproj` (Exe, net8.0, `AllowUnsafeBlocks`, LangVersion 12) with:
```xml
<ProjectReference Include="<repo>/src/CycloneDDS.Runtime/CycloneDDS.Runtime.csproj" />
<ProjectReference Include="<repo>/src/CycloneDDS.Core/CycloneDDS.Core.csproj" />
<ProjectReference Include="<repo>/src/CycloneDDS.Schema/CycloneDDS.Schema.csproj" />
<None Include="<repo>/artifacts/native/linux-x64/**/*" CopyToOutputDirectory="PreserveNewest" Link="%(Filename)%(Extension)" />
<Import Project="<repo>/tools/CycloneDDS.CodeGen/CycloneDDS.targets" />
```
Type (one namespace per file):
```csharp
namespace std_msgs.msg.dds_
{
    [DdsStruct, DdsExtensibility(DdsExtensibilityKind.Final)]   // remove Final to reproduce failure
    public partial struct String_ { [DdsManaged] public string data; }
}
```
Program: `DdsWriter<String_>(p, "rt/probe_out", DdsQos.Reliable)`, `DdsReader<String_>(p, "rt/probe_in", DdsQos.Reliable)`,
loop write/take every 250 ms, then print `w.OfferedIncompatibleQosStatus`.
ROS side:
```bash
source /opt/ros/jazzy/setup.bash; export RMW_IMPLEMENTATION=rmw_cyclonedds_cpp
ros2 topic echo --once /probe_out std_msgs/msg/String
ros2 topic pub -r 4 /probe_in std_msgs/msg/String "{data: from_ros}"
```
Note: `ros2 topic echo` can take several seconds to start; allow ≥10 s before concluding failure.

IdlImporter on ROS IDL (works, but names don't match rmw):
```bash
dotnet tools/CycloneDDS.IdlImporter/bin/Release/net8.0/CycloneDDS.IdlImporter.dll \
  /opt/ros/jazzy/share/sensor_msgs/msg/JointState.idl --source-root /opt/ros/jazzy/share \
  --output-root <out> --idlc-path artifacts/native/linux-x64/idlc
```
Output: `sensor_msgs.msg.JointState`, `std_msgs.msg.Header`, `builtin_interfaces.msg.Time`, all `Final`.

---

## Part 3 — Findings

### A. ROS 2 blockers

**A1. Default extensibility is Appendable** — `tools/CycloneDDS.CodeGen/TypeInfo.cs:7`, `SchemaDiscovery.cs:247`,
also documented default in `DdsExtensibilityAttribute`. Cyclone strips XCDR1 from allowed representations when the
ops contain DLC/PLC (`cyclonedds/src/core/ddsc/src/dds_topic.c:681`), so writers offer XCDR2 only. ROS 2 types
are final/XCDR1 → no match (verified, policy 25). Fix: default to `Final` (also idlc's default). Breaking for
users relying on appendable.

**A2. IDL module ordering** — `IdlEmitter.cs:54` `moduleGroups.OrderBy(g => g.Key)`. Types from multiple
namespaces in one source file are emitted alphabetically by module, not topologically → idlc error
`Scoped name '...' cannot be resolved`. Workaround: one namespace per `.cs` file. Fix: topological sort across
modules (reopen modules as needed — IDL allows reopening).

**A3. DDS type name not overridable** — `DdsTypeSupport.GetTypeName<T>()` (`DdsTypeSupport.cs:121`) =
CLR full name. Cyclone requires equal type names for matching when no TypeInformation is present
(`cyclonedds/src/core/ddsi/src/ddsi_qosmatch.c:235`). `[DdsTypeName]` exists but nothing reads it. Fix: honor it
in both IDL emission and `GetTypeName` (only top-level topic type name matters on the wire), or add a ROS naming
convention helper.

**A4. IdlImporter lacks rmw naming** — importing ROS `share/<pkg>/msg/*.idl` works but produces
`<pkg>::msg::<Name>`, not `<pkg>::msg::dds_::<Name>_`. A `--ros2` mode (or A3) makes "generate from ROS IDL"
a complete workflow.

### B. Additional ROS 2 nice-to-haves

- ROS topic-name helpers (`rt/` for topics; `rq/`/`rr/` if services are ever needed).
- QoS presets mirroring ROS profiles (default = reliable/volatile/keep-last 10 — `DdsQos.Reliable` already
  matches; `SensorData` = best-effort keep-last 5; `/tf_static`-style transient-local).
- Optional: publish `ros_discovery_info` (`rmw_dds_common::msg::ParticipantEntitiesInfo`) so the .NET process
  appears in `ros2 node list`. Topics already show up in `ros2 topic list` without it.
- Jazzy `rmw_cyclonedds` puts `typehash=RIHS01_...` in endpoint USER_DATA; not required for matching.
- A ROS 2 interop test suite (skippable when ROS isn't installed), covering the cases in Part 2.

### C. Correctness bugs

**C1. `[ArrayLength(N)] T[]` layout mismatch** — IDL emits `double x[9]` (inline), `SerializerEmitter` emits a
`DdsSequenceNative` field and sequence marshalling, `ViewEmitter` treats it as a `T*` fixed array → compile error
`CS0029 DdsSequenceNative → double*`. If the view had compiled, the native layout would disagree with the ops.
Documented feature; broken.

**C2. No `_Native` layout vs ops-offset verification** — generator computes C layout independently of idlc.
Cheap mitigation: at codegen (or first topic registration), walk ops and compare each `DDS_OP_ADR` offset with
`Unsafe`/`Marshal.OffsetOf` of the corresponding `_Native` field; fail loudly.

**C3. Dispose order race** — `DdsWriter.Dispose` (`DdsWriter.cs:453`) and `DdsReader.Dispose`
(`DdsReader.cs:384`) free the listener's `GCHandle` before deleting the entity. `dds_set_listener` copies the
listener into the entity, so callbacks can still fire with a freed handle until `dds_delete`. Fix: delete entity
first (dds_delete waits for in-progress listeners), then free handle/listener.

**C4. `WaitDataAsync` mutates sample state** — `HasData()` (`DdsReader.cs:263`) does `Read(1)`, which marks the
sample READ; subsequent NOT_READ-masked reads miss it. Use `dds_get_status_changes`/read condition or
`dds_read_mask` with peek semantics instead.

**C5. Filter semantics** — `DdsLoan.Enumerator.MoveNext` (`DdsLoan.cs:101`) fully deserializes to evaluate the
`Predicate<T>`, then `Current.Data` deserializes again. README claims zero-copy filtering on a view.
`this[int]` (`DdsLoan.cs:53`) and `Count` ignore the filter.

**C6. `DdsQosPolicy` incomplete** — ends at `DurabilityService = 22` (`DdsQosPolicy.cs:32`); missing
Property 23, TypeConsistencyEnforcement 24, DataRepresentation 25 (the ROS failure above surfaces as raw 25).

**C7. Key offset fallback is semantically wrong** — `DdsParticipant.cs:365` substitutes a byte offset
(`GetRecursiveOffset`) where `dds_key_descriptor_t.m_offset` is an **index into the ops array** (KOF position).
Only triggers if codegen didn't find the key's KOF; probably never hit, but wrong.

**C8. Inconsistent instance lookup** — reader `LookupInstance` marshals the full sample
(`DdsReader.cs:417`); writer uses the key-only marshaller.

**C9. WaitSet read condition mask** — `DdsWaitSet.cs:54` uses `uint.MaxValue` → triggers on already-read samples;
busy-loops if consumer uses `Read` instead of `Take`.

**C10. Misc** — `DdsEntityHandle` deletes entities from the finalizer thread; listener `GCHandle`s are strong,
so undisposed readers/writers leak rather than finalize; library writes to `Console` (5 sites in `src/`);
reflection failures swallowed (`catch {}`); `where T : new()` on reader but not writer; `MarshalDescriptor`
falls back to `Marshal.SizeOf<T>()` / 4096 for size when generated methods are missing.

### D. Structural complexity / refactor candidates

**D1. No shared type IR in the generator.** `FieldInfo.TypeName` is a string; each emitter re-classifies
(sequence? fixed array? inline array? string? bounded? nested? enum? union?) with its own logic
(≈200 / 119 / 92 conditional branches in Serializer / View / IDL emitters). C1 is the direct result.
Refactor: resolve every field once into a typed shape model (Primitive, Enum(bitbound), String(bound),
Sequence(elem, bound), Array(elem, dims), StructRef, UnionRef, Optional) consumed by all emitters.

**D2. Triple representation (managed / `_Native` / ops).** Options:
- (a) Keep design; add C2 verification; shrink emitters via D1.
- (b) Generate a direct XCDR1 (and optionally XCDR2) serializer in C#, write via `dds_writecdr`/serdata and read
  via `dds_takecdr` (P/Invokes already declared). Removes `_Native` ghost structs, `NativeArena`, the view emitter's
  native-pointer logic, and the C-layout ABI risk. For ROS (final, XCDR1) the encoding is simple. Large change;
  loses "Cyclone's own serializer" guarantee; keys/keyhash need handling for keyed types.

**D3. Reflection binding of generated members** (`DdsWriter` static ctor, `DdsReader` static ctor,
`DdsParticipant.MarshalDescriptor`, `DdsTypeSupport`). Replace with an interface using static abstract members
(net8 / C# 11), e.g. `interface IDdsType<TSelf> { static abstract ...; }` and `where T : IDdsType<T>` →
compile-time binding, no `GetMethod`, no swallowed failures, trimming/AOT friendly.

**D4. Out-of-band source discovery.** The generator re-parses `*.cs` from disk into its own compilation
(misses conditional compilation symbols, linked files, other generators' output, any folder named `Generated`).
Alternatives: pass `@(Compile)` items explicitly from MSBuild; or split into a Roslyn incremental source
generator for C# emission plus a pre-step for idlc (source generators cannot run processes).

**D5. Cyclone fork.** Fork = upstream 0.11.0 + 3 idljson commits. idlc loads generator plugins via `-l <lib>`,
so `idljson` could be built out-of-tree against upstream Cyclone, removing the fork. Alternatively generate ops in
C# (large) or parse idlc's standard C output (fragile).

**D6. Runtime package depends on Roslyn** — `src/CycloneDDS.Runtime/CycloneDDS.Runtime.csproj:140-145`
`Microsoft.CodeAnalysis.*` PackageReferences flow to every consumer (observed in probe reference list). Runtime
doesn't use them; the CodeGen DLLs are packed separately under `tools/`. Mark `PrivateAssets="all"` or remove.

**D7. Feature surface not aligned with ROS goal** — sender tracking, view structs + `ReadCopied` extensions,
`FixedStringN` + views, `BoundedSeq<T>`, unions/mutable/optional support, `StreamAsync`. Candidates to remove or
isolate if the scope narrows to ROS topics.

### E. Dead code / cruft

- Attributes/types never consumed: `DdsTypeName` (unless wired for A3), `DdsTypeMap`/`DdsWire`,
  `DdsTypeFormat`/`FormattedToken`/`TokenType`.
- `DdsApi`: unused `dds_writecdr`, `dds_readcdr`, `dds_takecdr*`, `*_serdata*`, `dds_take_with_collector`,
  `dds_sample_info_size`, `dds_get_topic_sertype`, `ddsrt_iovec_t`, plain `dds_read`/`dds_take`
  (keep if D2(b) chosen). Unused delegate fields `DdsWriter.cs:20-22`. Unused `using System.Reflection.Emit`.
- `CodeGenerator.GenerateDescriptorCodeFromJson` (unused), commented-out `DumpCppAst` in `Program.cs`.
- `src/CycloneDDS.Schema/Polyfills.cs` (dead on net8).
- `tests/CycloneDDS.Native.Verify` (no sources), `debug_tool/` (csproj only), `dump.bat`, `*.dumpfilter`,
  `tests/CycloneDDS.Runtime.Tests/{OffsetDebug.cs,CheckSizes.cs,KeyedMessages/temp_c,KeyedMessages/temp_test}`,
  `tools/*Tests/UnitTest1.cs` placeholders, `examples/FeatureDemo` (bin/obj only, untracked), `.archive/`.

### F. Documentation drift

- README, `DetailedOverview.md`, `IDL-GENERATION.md` reference `[DdsTopic]` / `[DdsQos]`, removed in `7b373e0`.
- README claims filter runs zero-copy on a view (false, see C5); claims "100% wire compliance" without noting
  the appendable/XCDR2 default.
- README links `docs/IDL-IMPORT.md`; file is at repo root (`IDL-IMPORT.md`).
- `TODO.md` lists DDS Monitor / ECS panel items for removed features; mentions upgrading to Cyclone 11.
- `docs/` MARSHAL-*/ZERO-COPY-*/nugetizing task trackers are historical LLM work logs.
- CI runs only Runtime tests; codegen and importer tests are not exercised in CI.

---

## Part 4 — Suggested ordering (not yet agreed)

1. ROS blockers A1–A4 + ROS interop test project (small, high value).
2. Correctness C1 (or remove `[ArrayLength]` on arrays), C3, C4, C6; add C2 verification.
3. Remove cruft (E) and fix docs (F); D6 dependency fix.
4. D3 static-abstract interface (mechanical, removes reflection).
5. Decide D2 (harden native-layout path vs direct CDR). If (a): D1 type IR. If (b): new serializer emitter,
   delete `_Native`/view emitters.
6. D5 fork removal, D4 discovery changes.

## Part 5 — Open questions for the owner

1. Primary type workflow: C#-first authoring, or generate from ROS `.idl` (IdlImporter-first)?
2. Serialization path: harden native-layout design (D2a) or move to direct XCDR1/CDR (D2b)?
3. Breaking changes acceptable? (drop sender tracking / views / unused attributes / appendable default)
4. Is the Cyclone fork and packaging (D5) in scope, or managed code only?
