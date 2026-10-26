using CycloneDDS.Schema;

namespace CycloneDDS.Runtime.Tests
{
    [DdsStruct]
    public partial struct StringMessage
    {
        public int Id;
        public string Msg;
    }
}