using System;
using System.Reflection;
using System.Runtime.InteropServices;
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

        struct TestKeyStruct
        {
            [DdsKey]
            public int Id;
        }

        [DdsUnion]
        struct TestUnion { }

        [DdsManaged]
        class TestManagedClass { }

        struct TestManagedField
        {
            [DdsManaged]
            public string Name;
        }

        [Fact]
        public void DdsStruct_CanBeAppliedToStruct()
        {
            var attr = typeof(TestStruct).GetCustomAttribute<DdsStructAttribute>();
            Assert.NotNull(attr);
        }

        [Fact]
        public void DdsStruct_CanBeAppliedToClass()
        {
            var attr = typeof(TestClass).GetCustomAttribute<DdsStructAttribute>();
            Assert.NotNull(attr);
        }

        [Fact]
        public void DdsKey_CanBeAppliedToField()
        {
            var field = typeof(TestKeyStruct).GetField("Id");
            var attr = field.GetCustomAttribute<DdsKeyAttribute>();
            Assert.NotNull(attr);
        }

        [Fact]
        public void DdsUnion_CanBeAppliedToStruct()
        {
            var attr = typeof(TestUnion).GetCustomAttribute<DdsUnionAttribute>();
            Assert.NotNull(attr);
        }

        [Fact]
        public void DdsManaged_CanBeAppliedToClassAndField()
        {
            var classAttr = typeof(TestManagedClass).GetCustomAttribute<DdsManagedAttribute>();
            Assert.NotNull(classAttr);

            var field = typeof(TestManagedField).GetField("Name");
            var fieldAttr = field.GetCustomAttribute<DdsManagedAttribute>();
            Assert.NotNull(fieldAttr);
        }

        [Fact]
        public void FixedString32_HasCorrectSize()
        {
            Assert.Equal(32, Marshal.SizeOf<FixedString32>());
        }

        [Fact]
        public void FixedString64_HasCorrectSize()
        {
            Assert.Equal(64, Marshal.SizeOf<FixedString64>());
        }

        [Fact]
        public void BoundedSeq_EnforcesMaxLength()
        {
            var seq = new BoundedSeq<int>(3);
            seq.Add(1);
            seq.Add(2);
            seq.Add(3);

            Assert.Throws<InvalidOperationException>(() => seq.Add(4));
            Assert.Equal(3, seq.Count);
        }
    }
}
