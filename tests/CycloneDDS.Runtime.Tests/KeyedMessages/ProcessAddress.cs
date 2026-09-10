using CycloneDDS.Schema;

namespace CycloneDDS.Runtime.Tests.KeyedMessages
{
    [DdsStruct]
    [DdsIdlFile("NestedKeys")]
    [DdsExtensibility(DdsExtensibilityKind.Final)]
    public partial struct ProcessAddress
    {
        [DdsManaged]
        [DdsKey]
        public string StationId { get; set; }

        [DdsManaged]
        [DdsKey]
        public string ProcessId { get; set; }

        [DdsManaged]
        public string SomeOtherId { get; set; }
    }
}
