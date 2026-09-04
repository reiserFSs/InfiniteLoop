from pathlib import Path
import tempfile
import unittest

from Scripts import protocol_gap


class ProtocolGapTests(unittest.TestCase):
    def test_joins_metadata_lua_handlers_and_observations(self):
        metadata = """// Namespace: Protocol.Protocol.Frontend
[Route(1)]
[MessagePackObject(True)]
public class FooRequest // TypeDefIndex: 1
{
    // Fields
    public int Id; // 0x10

    // Methods
    public void .ctor() { }
}
// Namespace: Protocol.Protocol.Frontend
[MessagePackObject(True)]
public class FooResponse // TypeDefIndex: 2
{
    // Fields
    public XCode Code; // 0x10
    public string Value; // 0x18

    // Methods
    public void .ctor() { }
}
"""
        lua = """local METHODS = { Foo = \"FooRequest\" }
local request = { Id = 7, Nested = { Inner = 1 } }
request.Token = "token"
XNetwork.Call(METHODS.Foo, request, function(res)
    print(res.Code, res.Value)
end)
"""
        handler = '[RequestPacketHandler("FooRequest")]\nstatic void Foo() { }\n'

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            metadata_path = root / "dump.cs"
            lua_root = root / "lua"
            handler_root = root / "handlers"
            metadata_path.write_text(metadata)
            lua_root.mkdir()
            handler_root.mkdir()
            (lua_root / "foo.lua").write_text(lua)
            (handler_root / "Foo.cs").write_text(handler)
            (root / "capture-summary.jsonl").write_text(
                '{"name":"FooRequest","packet_type_name":"Request",'
                '"payload_summary":{"kind":"map","keys":["Id","CaptureOnly"]}}\n'
                '{"name":"FooRequest","packet_type_name":"Request"}\n'
            )

            schemas = protocol_gap.parse_metadata(metadata_path)
            calls, lua_fields, responses, consumers = protocol_gap.parse_lua(lua_root)
            handlers, pushes = protocol_gap.parse_handlers(handler_root)
            observed, observed_fields, observed_shapes = protocol_gap.parse_observed([root])
            report = protocol_gap.rows(
                schemas, calls, lua_fields, responses, consumers, handlers, pushes,
                observed, observed_fields, observed_shapes,
            )

        row = next(row for row in report if row[2] == "FooRequest")
        self.assertEqual("2-observed-covered", row[0])
        self.assertEqual("foo", row[3])
        self.assertEqual("map", row[4])
        self.assertEqual("Id:int", row[5])
        self.assertEqual("CaptureOnly,Id,Nested,Token", row[6])
        self.assertEqual("lua,capture", row[7])
        self.assertEqual("Code,Value", row[8])
        self.assertEqual("foo.lua:4", row[9])
        self.assertEqual("handled", row[10])
        self.assertEqual(2, row[11])
        self.assertEqual("FooResponse", row[12])

    def test_clusters_observed_missing_requests_by_feature(self):
        def gap(name, observed):
            return ["0-observed-missing", "request", name, "Feature", "map", "", "", "capture", "", "", "missing", observed, ""]

        self.assertEqual(
            [["Feature", 2, 5, "FirstRequest,SecondRequest"]],
            protocol_gap.cluster_rows([gap("FirstRequest", 2), gap("SecondRequest", 3)]),
        )
        self.assertEqual(
            [["Feature", 2, 0, 2, 2, 2, 5, "FirstRequest,SecondRequest"]],
            protocol_gap.feature_rows([gap("FirstRequest", 2), gap("SecondRequest", 3)]),
        )


if __name__ == "__main__":
    unittest.main()
