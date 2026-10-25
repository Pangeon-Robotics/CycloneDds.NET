using System;
using CycloneDDS.Runtime.Interop;

namespace CycloneDDS.Runtime
{
    public class DdsException(DdsApi.DdsReturnCode code, string message) : Exception($"{message} (ReturnCode: {code})")
    {
        public DdsApi.DdsReturnCode ReturnCode { get; } = code;

        public static void Check(DdsApi.DdsReturnCode code, string operation)
        {
            if (code != DdsApi.DdsReturnCode.Ok)
            {
                throw new DdsException(code, $"DDS operation '{operation}' failed");
            }
        }
    }
}
