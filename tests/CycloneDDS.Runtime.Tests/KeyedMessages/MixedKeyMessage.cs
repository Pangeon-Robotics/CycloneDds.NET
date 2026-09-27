using CycloneDDS.Schema;

namespace CycloneDDS.Runtime.Tests.KeyedMessages
{
    [DdsStruct]
    public partial struct MixedKeyMessage
    {
        [DdsKey]
        public int Id { get; set; }

        [DdsKey]
        [DdsManaged]
        public string Name { get; set; }

        [DdsManaged]
        public string Data { get; set; }
    }
}
