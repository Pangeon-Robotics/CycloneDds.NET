using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CycloneDDS.Core;
using Xunit;

namespace CycloneDDS.Runtime.Tests.Ros
{
    /// <summary>
    /// Runs only where a ROS 2 installation with rmw_cyclonedds_cpp is present: under
    /// <c>$ROS_SETUP</c> if set, else the first <c>/opt/ros/*/setup.bash</c>.
    /// </summary>
    public sealed class RosFactAttribute : FactAttribute
    {
        public RosFactAttribute()
        {
            if (RosCli.SetupScript == null) Skip = "No ROS 2 installation found (set ROS_SETUP to its setup.bash).";
        }
    }

    /// <summary>Runs <c>ros2</c> commands in a shell with the ROS environment sourced.</summary>
    internal static class RosCli
    {
        // A domain of its own keeps these tests away from other DDS traffic on the machine
        public const uint Domain = 73;

        public static readonly string? SetupScript = FindSetupScript();

        private static string? FindSetupScript()
        {
            if (Environment.GetEnvironmentVariable("ROS_SETUP") is { Length: > 0 } explicitSetup)
                return File.Exists(explicitSetup) ? explicitSetup : null;

            if (!Directory.Exists("/opt/ros")) return null;
            return Directory.GetDirectories("/opt/ros")
                .Select(d => Path.Combine(d, "setup.bash"))
                .Where(File.Exists)
                .OrderDescending(StringComparer.Ordinal)
                .FirstOrDefault();
        }

        public static Process Start(string command)
        {
            var info = new ProcessStartInfo("bash")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add($"source '{SetupScript}' && {command}");
            info.Environment["RMW_IMPLEMENTATION"] = "rmw_cyclonedds_cpp";
            info.Environment["ROS_DOMAIN_ID"] = Domain.ToString();
            return Process.Start(info)!;
        }

        public static void Stop(Process process)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit();
            process.Dispose();
        }
    }

    /// <summary>
    /// Wire compatibility with ROS 2 nodes under rmw_cyclonedds_cpp, in both directions, using
    /// the ros2 CLI as the ROS-side endpoint.
    /// </summary>
    public class RosInteropTests : IDisposable
    {
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

        private readonly DdsParticipant _participant = new(RosCli.Domain);

        public void Dispose() => _participant.Dispose();

        /// <summary>Publishes <paramref name="yaml"/> from ROS and returns the first sample C# receives.</summary>
        private T ReceiveFromRos<T>(string topic, string rosType, string yaml) where T : IDdsType<T>
        {
            using var reader = new DdsReader<T>(_participant, $"rt/{topic}", DdsQos.Reliable);
            var ros = RosCli.Start($"ros2 topic pub -r 5 /{topic} {rosType} \"{yaml}\"");
            try
            {
                var stopwatch = Stopwatch.StartNew();
                while (stopwatch.Elapsed < Deadline)
                {
                    using var loan = reader.Take();
                    foreach (var sample in loan)
                    {
                        if (sample.IsValid) return sample.Data;
                    }
                    Thread.Sleep(100);
                }
                throw new TimeoutException($"No {rosType} arrived from ROS within {Deadline.TotalSeconds} s.");
            }
            finally
            {
                RosCli.Stop(ros);
            }
        }

        /// <summary>Publishes <paramref name="sample"/> from C# and returns what <c>ros2 topic echo</c> prints.</summary>
        private string SendToRos<T>(string topic, string rosType, T sample) where T : IDdsType<T>
        {
            using var writer = new DdsWriter<T>(_participant, $"rt/{topic}", DdsQos.Reliable);
            using var stop = new CancellationTokenSource();
            var publishing = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    writer.Write(sample);
                    Thread.Sleep(200);
                }
            });

            var ros = RosCli.Start($"timeout {Deadline.TotalSeconds} ros2 topic echo --once --full-length /{topic} {rosType}");
            try
            {
                string output = ros.StandardOutput.ReadToEnd();
                Assert.True(output.Length > 0, $"ros2 topic echo received nothing: {ros.StandardError.ReadToEnd()}");
                return output;
            }
            finally
            {
                RosCli.Stop(ros);
                stop.Cancel();
                publishing.Wait();
            }
        }

        [RosFact]
        public void String_FromRos()
        {
            var got = ReceiveFromRos<std_msgs.msg.dds_.String_>("interop_string_in", "std_msgs/msg/String", "{data: 'from ros é'}");
            Assert.Equal("from ros é", got.data);
        }

        [RosFact]
        public void String_ToRos()
        {
            string output = SendToRos("interop_string_out", "std_msgs/msg/String", new std_msgs.msg.dds_.String_ { data = "from C# é" });
            Assert.Contains("data: from C# é", output);
        }

        [RosFact]
        public void JointState_FromRos()
        {
            var got = ReceiveFromRos<sensor_msgs.msg.dds_.JointState_>("interop_joints_in", "sensor_msgs/msg/JointState",
                "{header: {stamp: {sec: 3, nanosec: 4}, frame_id: 'arm'}, name: ['a', 'b'], position: [0.5, 1.5], velocity: [], effort: [2.0]}");

            Assert.Equal((3, 4u, "arm"), (got.header.stamp.sec, got.header.stamp.nanosec, got.header.frame_id));
            Assert.Equal(["a", "b"], got.name);
            Assert.Equal([0.5, 1.5], got.position);
            Assert.Empty(got.velocity);
            Assert.Equal([2.0], got.effort);
        }

        [RosFact]
        public void JointState_ToRos()
        {
            string output = SendToRos("interop_joints_out", "sensor_msgs/msg/JointState", new sensor_msgs.msg.dds_.JointState_
            {
                header = new() { stamp = new() { sec = 5, nanosec = 6 }, frame_id = "base" },
                name = ["j1", "j2"],
                position = [1.25, 2.5],
                velocity = [],
                effort = [7],
            });

            Assert.Contains("sec: 5", output);
            Assert.Contains("nanosec: 6", output);
            Assert.Contains("frame_id: base", output);
            Assert.Contains("- j2", output);
            Assert.Contains("- 2.5", output);
            Assert.Contains("velocity: []", output);
        }

        [RosFact]
        public void Imu_FromRos()
        {
            var got = ReceiveFromRos<sensor_msgs.msg.dds_.Imu_>("interop_imu_in", "sensor_msgs/msg/Imu",
                "{header: {frame_id: 'imu'}, orientation: {w: 1.0}, angular_velocity: {x: 0.1, y: 0.2, z: 0.3}, " +
                "orientation_covariance: [0,1,2,3,4,5,6,7,8], angular_velocity_covariance: [10,11,12,13,14,15,16,17,18], " +
                "linear_acceleration_covariance: [1,2,3,4,5,6,7,8,9]}");

            Assert.Equal("imu", got.header.frame_id);
            Assert.Equal(1.0, got.orientation.w);
            Assert.Equal(0.3, got.angular_velocity.z);
            for (int i = 0; i < 9; i++)
            {
                Assert.Equal(i, got.orientation_covariance[i]);
                Assert.Equal(10 + i, got.angular_velocity_covariance[i]);
                Assert.Equal(1 + i, got.linear_acceleration_covariance[i]);
            }
        }

        [RosFact]
        public void Imu_ToRos()
        {
            var imu = new sensor_msgs.msg.dds_.Imu_
            {
                header = new() { frame_id = "imu" },
                linear_acceleration = new() { z = 9.81 },
                linear_acceleration_covariance = [1, 2, 3, 4, 5, 6, 7, 8, 9],
            };
            for (int i = 0; i < 9; i++) imu.angular_velocity_covariance[i] = 10 + i;

            string output = SendToRos("interop_imu_out", "sensor_msgs/msg/Imu", imu);
            Assert.Contains("z: 9.81", output);
            Assert.Contains("- 18.0", output);
            Assert.Contains("- 9.0", output);
        }

        [RosFact]
        public void BoundedSequence_FromRos()
        {
            var got = ReceiveFromRos<shape_msgs.msg.dds_.SolidPrimitive_>("interop_shape_in", "shape_msgs/msg/SolidPrimitive",
                "{type: 1, dimensions: [1.0, 2.0, 3.0], polygon: {points: [{x: 1.0, y: 2.0, z: 3.0}]}}");

            Assert.Equal(1, got.type);
            Assert.Equal([1.0, 2.0, 3.0], got.dimensions);
            Assert.Equal(new geometry_msgs.msg.dds_.Point32_ { x = 1, y = 2, z = 3 }, Assert.Single(got.polygon.points));
        }

        [RosFact]
        public void BoundedSequence_ToRos()
        {
            string output = SendToRos("interop_shape_out", "shape_msgs/msg/SolidPrimitive", new shape_msgs.msg.dds_.SolidPrimitive_
            {
                type = 2,
                dimensions = [0.5],
                polygon = new() { points = [new() { x = 4, y = 5, z = 6 }] },
            });

            Assert.Contains("type: 2", output);
            Assert.Contains("- 0.5", output);
            Assert.Contains("z: 6.0", output);
        }
    }
}
