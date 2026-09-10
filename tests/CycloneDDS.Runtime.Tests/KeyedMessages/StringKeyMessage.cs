using CycloneDDS.Schema;

namespace CycloneDDS.Runtime.Tests.KeyedMessages
{
    [DdsStruct]
    public partial struct StringKeyMessage
    {
        [DdsKey]
        [DdsManaged]
        public string KeyId { get; set; }
        
        [DdsManaged]
        public string Message { get; set; }
    }
}
