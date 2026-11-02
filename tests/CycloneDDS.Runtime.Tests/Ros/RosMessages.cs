using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CycloneDDS.Schema;

// ROS 2 interface definitions, named as rmw_cyclonedds names them on the wire:
// namespace <package>.msg.dds_ and type <Name>_.

namespace builtin_interfaces.msg.dds_
{
    [DdsStruct] public partial struct Time_ { public int sec; public uint nanosec; }
}

namespace std_msgs.msg.dds_
{
    [DdsStruct] public partial struct Header_ { public builtin_interfaces.msg.dds_.Time_ stamp; public string frame_id; }

    [DdsStruct] public partial struct String_ { public string data; }
}

namespace geometry_msgs.msg.dds_
{
    [DdsStruct] public partial struct Vector3_ { public double x, y, z; }

    [DdsStruct] public partial struct Quaternion_ { public double x, y, z, w; }

    [DdsStruct] public partial struct Point32_ { public float x, y, z; }

    [DdsStruct] public partial struct Polygon_ { public List<Point32_> points; }
}

namespace sensor_msgs.msg.dds_
{
    [InlineArray(9)] public struct Covariance9 { private double _element; }

    [DdsStruct]
    public partial class JointState_
    {
        public std_msgs.msg.dds_.Header_ header;
        public List<string> name;
        public List<double> position;
        public List<double> velocity;
        public List<double> effort;
    }

    // float64[9] declared both ways the generator supports
    [DdsStruct]
    public partial struct Imu_
    {
        public std_msgs.msg.dds_.Header_ header;
        public geometry_msgs.msg.dds_.Quaternion_ orientation;
        public Covariance9 orientation_covariance;
        public geometry_msgs.msg.dds_.Vector3_ angular_velocity;
        public Covariance9 angular_velocity_covariance;
        public geometry_msgs.msg.dds_.Vector3_ linear_acceleration;
        [ArrayLength(9)] public double[] linear_acceleration_covariance;
    }
}

namespace shape_msgs.msg.dds_
{
    [DdsStruct]
    public partial struct SolidPrimitive_
    {
        public byte type;
        [DdsBound(3)] public List<double> dimensions;
        public geometry_msgs.msg.dds_.Polygon_ polygon;
    }
}
