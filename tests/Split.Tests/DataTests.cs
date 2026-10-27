using System.IO;
using System.Text;
using static Split.Tests.Harness;

namespace Split.Tests;

/// <summary>
/// JSON, XML and YAML: nothing a format can hold may be lost on the way, and hostile files must
/// fail with an explanation instead of hanging or reading what they shouldn't.
/// </summary>
public static class DataTests
{
    private static JsonValue Parse(string text) => JsonParser.Parse(Encoding.UTF8.GetBytes(text));

    public static void Register()
    {
        Test("Data", "The JSON reader keeps key order, exact numbers and every kind of text", () =>
        {
            var value = Parse("""{"z": 1, "a": [1.50, -0, 1e400, 12345678901234567890], "s": "tab\t \"q\" é 🍊", "n": null, "t": true}""");
            Require(value.Kind == JsonKind.Object, "not an object");
            Check(value.Members.Select(m => m.Key).SequenceEqual(["z", "a", "s", "n", "t"]), $"key order: {string.Join(",", value.Members.Select(m => m.Key))}");
            Check(value.Members[1].Value.Equals(JsonValue.Array([JsonValue.Number("1.50"), JsonValue.Number("-0"), JsonValue.Number("1e400"), JsonValue.Number("12345678901234567890")])),
                  "numbers keep their exact text");
            Check(value.Members[2].Value.Equals(JsonValue.String("tab\t \"q\" é 🍊")), $"escapes and surrogate pairs: {value.Members[2].Value.Text}");
            var written = JsonWriter.String(value);
            Check(Parse(written).Equals(value), "writing and reading back gives the same value");
            Check(written.Contains("12345678901234567890") && written.Contains("1.50"), "big and padded numbers are written as they were");
            Check(Parse("\uFEFF[1]").Equals(JsonValue.Array([JsonValue.Number("1")])) || JsonParser.Parse([0xEF, 0xBB, 0xBF, (byte)'[', (byte)'1', (byte)']']).Equals(JsonValue.Array([JsonValue.Number("1")])),
                  "a byte-order mark is skipped");
        });

        Test("Data", "Broken JSON is explained with its line and column", () =>
        {
            foreach (var (text, fragment) in new[] { ("{\"a\": 1,\n  \"b\" 2}", "line 2"), ("[1, 2", "ends too early"), ("{\"a\": tru}", "unexpected"), ("[1] x", "more text") })
            {
                try
                {
                    Parse(text);
                    Check(false, $"“{text}” should fail");
                }
                catch (JsonParser.Failure failure)
                {
                    Check($"{failure.Message} line {failure.Line}".Contains(fragment), $"“{text}”: {failure.Message}, line {failure.Line}, column {failure.Column}");
                }
            }
            var deep = new string('[', JsonParser.MaxDepth + 10) + new string(']', JsonParser.MaxDepth + 10);
            try
            {
                Parse(deep);
                Check(false, $"nesting deeper than {JsonParser.MaxDepth} is refused instead of overflowing the stack");
            }
            catch (JsonParser.Failure) { }
            var file = Hostile.File("broken.json", "json", "{\"name\": \"Split\",\n \"oops\"}");
            ExpectError("broken JSON file", ["not valid JSON", "line 2"], () => Conversions.Convert(file, Format.Csv));
        });

        Test("Data", "JSON → XML → JSON comes back exactly, keys that aren’t XML names included", () =>
        {
            var value = Parse("""{"name": "Split", "1st": "x", "a b": [1, "2", null, true, {"deep": []}], "empty": {}, "amp": "<&>", "pi": 3.14}""");
            var xml = XmlBridge.Document(value);
            var back = XmlBridge.Json(XmlBridge.Parse(Encoding.UTF8.GetBytes(xml)));
            Check(back.Equals(value), $"round trip:\n{JsonWriter.String(back)}");
            var folder = Hostile.Folder("json-xml");
            var source = Path.Combine(folder, "data.json");
            File.WriteAllText(source, JsonWriter.String(value), new UTF8Encoding(false));
            var converted = Conversions.Convert(source, Format.Xml)[0];
            var again = Conversions.Convert(converted, Format.Json)[0];
            Check(JsonConverter.Read(again).Equals(value), "through files as well");
        });

        Test("Data", "Any XML becomes JSON with its attributes, text and repeated elements", () =>
        {
            var (value, notes) = Fidelity.Collect(() => XmlBridge.Json(XmlBridge.Parse(Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\"?><!-- a comment --><catalog lang=\"en\"><book id=\"1\">Intro <b>bold</b> end</book><book id=\"2\"/></catalog>"))));
            var text = JsonWriter.String(value);
            Check(text.Contains("\"catalog\"") && text.Contains("\"@lang\"") && text.Contains("\"@id\""), $"attributes become @keys: {text}");
            Check(text.Contains("\"book\": ["), $"repeated elements become an array: {text}");
            Check(text.Contains("#text"), "mixed text is kept as #text");
            Check(notes.Any(n => n.Contains("Comments")) && notes.Any(n => n.Contains("#text")), $"what JSON can’t hold is said: {string.Join("; ", notes)}");
        });

        Test("Data", "Hostile XML: external entities, billion laughs and deep nesting", () =>
        {
            var wire = "<?xml version=\"1.0\"?><!DOCTYPE r [<!ENTITY x SYSTEM \"file:///C:/Windows/win.ini\">]><r>a&x;b</r>";
            ExpectError("an external entity", ["could not be read", "entities"], () => XmlBridge.Parse(Encoding.UTF8.GetBytes(wire)));
            var entities = "<!ENTITY l0 \"ha\">";
            for (var level = 1; level <= 9; level++) entities += $"<!ENTITY l{level} \"" + string.Concat(Enumerable.Repeat($"&l{level - 1};", 10)) + "\">";
            var laughs = $"<?xml version=\"1.0\"?><!DOCTYPE r [{entities}]><r>&l9;</r>";
            var started = DateTime.UtcNow;
            ExpectError("billion laughs", ["could not be read"], () => XmlBridge.Parse(Encoding.UTF8.GetBytes(laughs)));
            Check((DateTime.UtcNow - started).TotalSeconds < 10, $"billion laughs took {(DateTime.UtcNow - started).TotalSeconds} s");
            var deep = string.Concat(Enumerable.Repeat("<a>", 3000)) + string.Concat(Enumerable.Repeat("</a>", 3000));
            ExpectError("very deep XML", ["nested more than"], () => XmlBridge.Parse(Encoding.UTF8.GetBytes(deep)));
            ExpectError("XML that isn’t XML", ["could not be read", "line"], () => XmlBridge.Parse("<a><b></a>"u8.ToArray()));
        });

        Test("Data", "YAML: ambiguous text is quoted so it reads back as text", () =>
        {
            var value = Parse("""{"plain": "hello", "yes": "yes", "no": "No", "null": "null", "num": "1.0", "colon": "a: b", "dash": "- x", "lead": " x", "multi": "a\nb", "real": 1.0, "list": [], "map": {}}""");
            var yaml = YamlWriter.Document(value);
            Check(yaml.Contains("plain: hello"), $"plain text stays plain: {yaml}");
            foreach (var quoted in new[] { "\"yes\"", "\"No\"", "\"null\"", "\"1.0\"", "\"a: b\"", "\"- x\"", "\" x\"" })
                Check(yaml.Contains(quoted), $"{quoted} should be quoted:\n{yaml}");
            Check(yaml.Contains("real: 1.0") && yaml.Contains("list: []") && yaml.Contains("map: {}"), $"numbers and empty containers:\n{yaml}");
        });

        Test("Data", "JSON ↔ tables: nested keys become columns, text stays text", () =>
        {
            var value = Parse("""[{"id": "007", "user": {"name": "Ana"}, "tags": ["a", "b"]}, {"id": 8, "extra": null}]""");
            var table = JsonTable.Make(value);
            Check(table.Columns.SequenceEqual(["id", "user.name", "tags[0]", "tags[1]", "extra"]), $"columns: {string.Join(", ", table.Columns)}");
            var folder = Hostile.Folder("json-table");
            var source = Path.Combine(folder, "users.json");
            File.WriteAllText(source, JsonWriter.String(value), new UTF8Encoding(false));
            var (outputs, notes) = Fidelity.Collect(() => Conversions.Convert(source, Format.Xlsx));
            Check(Xlsx.EntryText(outputs[0], "xl/worksheets/sheet1.xml").Contains("007"), "“007” is kept as text, not turned into 7");
            Check(notes.Any(n => n.Contains("null")), $"null becoming an empty cell is said: {string.Join("; ", notes)}");
            var csv = Conversions.Convert(source, Format.Csv)[0];
            Check(Files.Text(csv).StartsWith("id,user.name,tags[0],tags[1],extra"), $"CSV header: {Files.Text(csv)[..Math.Min(60, Files.Text(csv).Length)]}");
            var back = Conversions.Convert(Fixtures.Isolated("people.tsv", "json-table"), Format.Json);
            Check(Files.Text(back[0]).Contains("\"Name\": \"Ana\"") && Files.Text(back[0]).Contains("\"Age\": 31"), $"TSV → JSON records: {Files.Text(back[0])[..Math.Min(80, Files.Text(back[0]).Length)]}");
        });

        Test("Data", "Spreadsheet formulas: JSON keeps the values and says so", () =>
        {
            var dates = Fixtures.Isolated("dates.xlsx", "json-formulas");
            var (outputs, notes) = Fidelity.Collect(() => Conversions.Convert(dates, Format.Json));
            Check(Files.Text(outputs[0]).Contains("\"Sum\": 2"), $"the cached value of 1+1: {Files.Text(outputs[0])[..Math.Min(120, Files.Text(outputs[0]).Length)]}");
            Check(notes.Any(n => n.Contains("formulas")), $"the lost formulas are said: {string.Join("; ", notes)}");
        });
    }
}
