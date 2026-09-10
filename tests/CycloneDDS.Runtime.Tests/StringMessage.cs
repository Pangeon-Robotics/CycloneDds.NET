using CycloneDDS.Schema;

namespace CycloneDDS.Runtime.Tests
{
    [DdsStruct]
    [DdsExtensibility(DdsExtensibilityKind.Appendable)]
    public partial struct StringMessage
    {
        public int Id;
        [DdsManaged]
        public string Msg;
    }
}