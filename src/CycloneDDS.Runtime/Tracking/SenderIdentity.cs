using CycloneDDS.Runtime.Interop;
using CycloneDDS.Schema;

namespace CycloneDDS.Runtime.Tracking
{
    /// <summary>
    /// Application-level identity_broadcast by each participant.
    /// Used to correlate DDS publication handles to user metadata.
    /// </summary>
    [DdsStruct]
    public partial struct SenderIdentity
    {
        /// <summary>
        /// Native DDS Participant GUID (16 bytes) - used as correlation key.
        /// </summary>
        public DdsGuid ParticipantGuid;

        /// <summary>
        /// User-defined application domain identifier.
        /// </summary>
        public int AppDomainId;

        /// <summary>
        /// User-defined application instance identifier.
        /// </summary>
        public int AppInstanceId;

        /// <summary>
        /// Machine name where process is running.
        /// </summary>
        public string ComputerName;

        /// <summary>
        /// Process executable name.
        /// </summary>
        public string ProcessName;

        /// <summary>
        /// OS Process ID (disambiguates multiple instances of same exe).
        /// </summary>
        public int ProcessId;

        /// <summary>
        /// IP address of the host, resolved once from the CycloneDDS XML config.
        /// </summary>
        public string ComputerIP;
    }
}

