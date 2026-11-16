using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace I1rr.UnityMcp.Runtime.Tests
{
    public interface IDiagnosticInput { }
    public sealed class DiagnosticInput : IDiagnosticInput
    {
        private Vector3 _destination = new Vector3(3, 0, 7);
    }
    public class DiagnosticProbeBase : MonoBehaviour
    {
#pragma warning disable CS0414 // Read only through the inherited-private-field inspection regression.
        private int _baseSecret = 27;
#pragma warning restore CS0414
    }
    public sealed class DiagnosticProbe : DiagnosticProbeBase
    {
        public int visible = 5;
        private IDiagnosticInput _input = new DiagnosticInput();
        public string Action { get; private set; } = "Flying";
        public int GetterCalls { get; private set; }
        public int Computed { get { GetterCalls++; throw new InvalidOperationException("Getter must not run"); } }
        private List<IDiagnosticInput> _actors = new List<IDiagnosticInput> { new DiagnosticInput() };
        private int[] _numbers = { 2, 4, 6 };
        private object _nothing;
        private string _longText = new string('x', 4096);
        private object _opaque = new DangerousObject();
        private IList _custom = new DangerousList();
        private List<int> _many = Enumerable.Range(0, 1000).ToList();
        private object[] _nested = { new object[] { new object[] { 17 } } };
        private int[,] _matrix = new int[2, 2];
#pragma warning disable CS0414 // Reflection-only static field: the inspector must reject it without a getter.
        private static int StaticSecret = 81;
#pragma warning restore CS0414
        private string _stringNull;
    }
    public sealed class DangerousObject
    {
        public static int Calls;
        public override string ToString() { Calls++; throw new InvalidOperationException("ToString must not run"); }
    }
    public sealed class DangerousList : IList
    {
        public static int Calls;
        private static Exception Access() { Calls++; return new InvalidOperationException("Custom collection must not run"); }
        public int Count => throw Access();
        public object this[int index] { get => throw Access(); set => throw Access(); }
        public bool IsFixedSize => throw Access();
        public bool IsReadOnly => throw Access();
        public bool IsSynchronized => throw Access();
        public object SyncRoot => throw Access();
        public int Add(object value) => throw Access();
        public void Clear() => throw Access();
        public bool Contains(object value) => throw Access();
        public int IndexOf(object value) => throw Access();
        public void Insert(int index, object value) => throw Access();
        public void Remove(object value) => throw Access();
        public void RemoveAt(int index) => throw Access();
        public void CopyTo(Array array, int index) => throw Access();
        public IEnumerator GetEnumerator() => throw Access();
    }
    public sealed class DiagnosticInspectionTests
    {
        private GameObject _go;
        private DiagnosticProbe _probe;
        [SetUp] public void SetUp()
        {
            _go = new GameObject("DiagnosticInspectionProof");
            _probe = _go.AddComponent<DiagnosticProbe>();
        }
        [TearDown] public void TearDown() => UnityEngine.Object.DestroyImmediate(_go);
        private JObject Read(JToken paths = null)
        {
            var args = new JObject { ["gameobject_path"] = _go.name, ["component_type"] = typeof(DiagnosticProbe).FullName };
            if (paths != null) args["field_paths"] = paths;
            return RuntimeComponentTools.GetComponentData(args);
        }
        [Test] public void SelectedPrivateFieldsAreActuallyReturned()
        {
            var result = Read(new JArray("_input._destination", "Action", "_baseSecret"));
            Assert.That(result["data"]["_input._destination"], Is.Not.Null);
            Assert.That((int)result["data"]["_input._destination"]["z"], Is.EqualTo(7));
            Assert.That((string)result["data"]["Action"], Is.EqualTo("Flying"));
            Assert.That((int)result["data"]["_baseSecret"], Is.EqualTo(27));
        }
        [Test] public void DefaultSerializedShapeIsUnchanged()
        {
            var result = Read();
            Assert.That((int)result["data"]["visible"], Is.EqualTo(5));
            Assert.That(result["data"]["_input"], Is.Null);
            Assert.That(result["data"]["Action"], Is.Null);
            Assert.That(result["inspection_scope"], Is.Null);
            Assert.That(_probe.GetterCalls, Is.Zero);
        }
        [Test] public void ComputedGettersAndStaticsAreRejectedWithoutInvocation()
        {
            var result = Read(new JArray("Computed", "StaticSecret", "visible"));
            Assert.That(result["field_errors"]["Computed"], Is.Not.Null);
            Assert.That(result["field_errors"]["StaticSecret"], Is.Not.Null);
            Assert.That((int)result["data"]["visible"], Is.EqualTo(5));
            Assert.That(_probe.GetterCalls, Is.Zero);
        }
        [Test] public void InterfaceFieldsAndBuiltInCollectionElementsUseActualRuntimeTypes()
        {
            var result = Read(new JArray("_actors[0]._destination", "_numbers[1]", "_numbers[9]", "_matrix[0]", "_nothing.foo", "_stringNull"));
            Assert.That((int)result["data"]["_actors[0]._destination"]["x"], Is.EqualTo(3));
            Assert.That((int)result["data"]["_numbers[1]"], Is.EqualTo(4));
            Assert.That((string)result["field_errors"]["_numbers[9]"], Is.EqualTo("index_out_of_range"));
            Assert.That((string)result["field_errors"]["_matrix[0]"], Does.StartWith("index_requires"));
            Assert.That((string)result["field_errors"]["_nothing.foo"], Is.EqualTo("null_intermediate"));
            Assert.That(result["data"]["_stringNull"].Type, Is.EqualTo(JTokenType.Null));
        }
        [Test] public void OpaqueValuesDoNotCallUserCode()
        {
            DangerousObject.Calls = DangerousList.Calls = 0;
            var result = Read(new JArray("_opaque", "_custom", "_custom[0]"));
            Assert.That((bool)result["data"]["_opaque"]["opaque"], Is.True);
            Assert.That((bool)result["data"]["_custom"]["opaque"], Is.True);
            Assert.That(result["field_errors"]["_custom[0]"], Is.Not.Null);
            Assert.That(DangerousObject.Calls, Is.Zero);
            Assert.That(DangerousList.Calls, Is.Zero);
        }
        [Test] public void OutputStringsCollectionsAndDepthAreBounded()
        {
            var result = Read(new JArray("_longText", "_many", "_nested"));
            Assert.That(((string)result["data"]["_longText"]["value"]).Length, Is.EqualTo(256));
            Assert.That((int)result["data"]["_longText"]["length"], Is.EqualTo(4096));
            Assert.That(((JArray)result["data"]["_many"]["items"]).Count, Is.EqualTo(16));
            Assert.That((bool)result["data"]["_many"]["truncated"], Is.True);
            Assert.That(((JArray)result["data"]["_nested"]["items"][0]["items"][0]["items"]).Count, Is.Zero);
            Assert.That((bool)result["data"]["_nested"]["items"][0]["items"][0]["truncated"], Is.True);
        }
        [TestCase("")]
        [TestCase("_input.._destination")]
        [TestCase("_numbers[-1]")]
        [TestCase("_numbers[0][0]")]
        [TestCase("Computed()")]
        [TestCase("a.b.c.d.e.f.g")]
        [TestCase("visible\n")]
        public void InvalidPathIsRejectedBeforeReading(string path)
        {
            Assert.That((string)Read(new JArray(path))["error_code"], Is.EqualTo(McpErrorCodes.InvalidParameter));
            Assert.That(_probe.GetterCalls, Is.Zero);
        }
        [Test] public void SelectionTypeCountLengthAndDuplicatesAreValidated()
        {
            foreach (var paths in new JToken[] { new JValue("visible"), new JArray(), new JArray(1),
                new JArray("visible", "visible"), new JArray(new string('x', 129)),
                new JArray(Enumerable.Range(0, 17).Select(i => new JValue("field" + i))) })
                Assert.That((string)Read(paths)["error_code"], Is.EqualTo(McpErrorCodes.InvalidParameter));
        }
        [Test] public void AggregateCollectionBudgetIsSharedAcrossPaths()
        {
            // Exercise the reader independently with 16 distinct fields pointing to a large
            // array graph; the values are data, not custom enumeration or property getters.
            var result = ReflectionReader.ReadDiagnosticFields(new BudgetProbe(),
                new JArray(Enumerable.Range(0, 16).Select(i => new JValue("values[" + i + "]"))));
            Assert.That(result.ToString(Newtonsoft.Json.Formatting.None).Length, Is.LessThan(10000));
            Assert.That(result.ToString(), Does.Contain("node_limit"));
        }
        private sealed class BudgetProbe
        {
            public int[][] values = Enumerable.Range(0, 16).Select(_ => Enumerable.Range(0, 1000).ToArray()).ToArray();
        }
    }
}
