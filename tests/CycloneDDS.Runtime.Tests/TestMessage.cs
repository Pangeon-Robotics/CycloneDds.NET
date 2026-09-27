using CycloneDDS.Schema;

namespace CycloneDDS.Runtime.Tests
{
    [DdsStruct]
    public partial struct TestMessage
    {
        public int Id;
        public int Value;
    }
}
