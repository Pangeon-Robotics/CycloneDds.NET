using System;
using System.Reflection;
using Xunit;
using CycloneDDS.Schema;

namespace CycloneDDS.Schema.Tests
{
    public class SchemaAttributeTests
    {
        [DdsStruct]
        struct TestStruct { }

        [DdsStruct]
        class TestClass { }

        [Fact]
        public void DdsStruct_AppliesToStructsAndClasses()
        {
            Assert.NotNull(typeof(TestStruct).GetCustomAttribute<DdsStructAttribute>());
            Assert.NotNull(typeof(TestClass).GetCustomAttribute<DdsStructAttribute>());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void LengthAttributes_RejectNonPositiveValues(int value)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new DdsBoundAttribute(value));
            Assert.Throws<ArgumentOutOfRangeException>(() => new DdsElementBoundAttribute(value));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ArrayLengthAttribute(value));
        }

        [Fact]
        public void LengthAttributes_KeepTheirValue()
        {
            Assert.Equal(5, new DdsBoundAttribute(5).Max);
            Assert.Equal(6, new DdsElementBoundAttribute(6).Max);
            Assert.Equal(9, new ArrayLengthAttribute(9).Length);
        }

        [Theory]
        [InlineData("")]
        [InlineData("Types.idl")]
        [InlineData("dir/Types")]
        public void DdsIdlFile_RejectsPathsAndExtensions(string fileName)
        {
            Assert.Throws<ArgumentException>(() => new DdsIdlFileAttribute(fileName));
        }

        [Fact]
        public void DdsIdlModule_RejectsEmpty()
        {
            Assert.Throws<ArgumentException>(() => new DdsIdlModuleAttribute(" "));
            Assert.Equal("std_msgs::msg::dds_", new DdsIdlModuleAttribute("std_msgs::msg::dds_").ModulePath);
        }
    }
}
