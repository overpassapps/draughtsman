using System.Globalization;
using System.Text.Json;
using Draughtsman.Core.Serialization;
using Draughtsman.Tests.Shared;

namespace Draughtsman.Core.Tests;

/// <summary>
/// <see cref="SchemaValidator"/> keyword by keyword, against small schemas, in both directions: what is accepted, what is refused, and the exact lines
/// said about it. The committed schema's behaviour is pinned by the corpus (SchemaCorpus/) and the keyword semantics by the official suite
/// (JsonSchemaTestSuiteTests); these say what each handler does in words, so a handler broken on purpose fails a test with its name.
/// </summary>
public class SchemaValidatorTests
{
    private static IReadOnlyList<string> Check(string schema, string json)
    {
        return SchemaValidator.Compile(schema).Validate(json);
    }

    // ---- the schema is read strictly ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_committed_schema_uses_only_keywords_the_validator_implements()
    {
        SchemaValidator.Compile(File.ReadAllText(ContractFiles.SchemaPath));
        SchemaValidator.Compile(DocumentSchema.RawJson);
    }

    [Theory]
    [InlineData("oneOf", "[{}]")]
    [InlineData("anyOf", "[{}]")]
    [InlineData("allOf", "[{}]")]
    [InlineData("not", "{}")]
    [InlineData("if", "{}")]
    [InlineData("then", "{}")]
    [InlineData("else", "{}")]
    [InlineData("patternProperties", "{}")]
    [InlineData("prefixItems", "[]")]
    [InlineData("uniqueItems", "true")]
    [InlineData("multipleOf", "2")]
    [InlineData("contains", "{}")]
    [InlineData("minProperties", "1")]
    [InlineData("maxProperties", "1")]
    [InlineData("propertyNames", "{}")]
    [InlineData("dependentRequired", "{}")]
    [InlineData("dependentSchemas", "{}")]
    [InlineData("unevaluatedProperties", "false")]
    [InlineData("unevaluatedItems", "false")]
    [InlineData("$anchor", "\"a\"")]
    [InlineData("$dynamicRef", "\"#a\"")]
    [InlineData("contentEncoding", "\"base64\"")]
    [InlineData("somethingNew", "1")]
    public void A_keyword_that_is_not_implemented_stops_the_schema_loading(string keyword, string value)
    {
        NotSupportedException error = Assert.Throws<NotSupportedException>(() => SchemaValidator.Compile($"{{\"properties\":{{\"a\":{{\"{keyword}\":{value}}}}}}}"));

        Assert.Contains($"'{keyword}'", error.Message, StringComparison.Ordinal);
        Assert.Contains("#/properties/a/" + keyword, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Forms_the_validator_does_not_implement_are_refused_not_ignored()
    {
        Assert.Throws<NotSupportedException>(() => SchemaValidator.Compile("{\"type\":[\"string\",\"null\"]}"));
        Assert.Throws<NotSupportedException>(() => SchemaValidator.Compile("{\"format\":\"email\"}"));
        Assert.Throws<NotSupportedException>(() => SchemaValidator.Compile("{\"$schema\":\"http://json-schema.org/draft-07/schema#\"}"));
        Assert.Throws<NotSupportedException>(() => SchemaValidator.Compile("{\"properties\":{\"a\":{\"$defs\":{}}}}"));
        Assert.Throws<InvalidOperationException>(() => SchemaValidator.Compile("{\"$ref\":\"https://example.com/other.json\"}"));
        Assert.Throws<InvalidOperationException>(() => SchemaValidator.Compile("{\"$ref\":\"#/$defs/missing\"}"));
        Assert.Throws<InvalidOperationException>(() => SchemaValidator.Compile("{\"$ref\":\"#/properties/a\",\"properties\":{\"a\":{}}}"));
        Assert.Throws<InvalidOperationException>(() => SchemaValidator.Compile("{\"required\":\"id\"}"));
        Assert.Throws<InvalidOperationException>(() => SchemaValidator.Compile("{\"type\":\"text\"}"));
        Assert.Throws<InvalidOperationException>(() => SchemaValidator.Compile("{\"minLength\":-1}"));
        Assert.Throws<InvalidOperationException>(() => SchemaValidator.Compile("{\"minimum\":\"0\"}"));
        Assert.ThrowsAny<ArgumentException>(() => SchemaValidator.Compile("{\"pattern\":\"[\"}"));
    }

    [Fact]
    public void Annotations_do_not_change_what_is_valid()
    {
        const string schema = "{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"$id\":\"urn:x\",\"title\":\"t\",\"description\":\"d\",\"default\":1,\"examples\":[1],\"$comment\":\"c\",\"type\":\"string\"}";

        Assert.Empty(Check(schema, "\"x\""));
        Assert.Single(Check(schema, "1"));
    }

    // ---- type ---------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("object", "{}", "[]", "array")]
    [InlineData("array", "[]", "{}", "object")]
    [InlineData("string", "\"\"", "1", "integer")]
    [InlineData("boolean", "false", "0", "integer")]
    [InlineData("null", "null", "false", "boolean")]
    [InlineData("number", "1.5", "\"1\"", "string")]
    [InlineData("integer", "3", "1.5", "number")]
    public void Type_accepts_its_own_kind_and_names_what_it_found_instead(string type, string accepted, string refused, string found)
    {
        string schema = $"{{\"type\":\"{type}\"}}";

        Assert.Empty(Check(schema, accepted));
        Assert.Equal([$"(document): Value is \"{found}\" but should be \"{type}\""], Check(schema, refused));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("-0", true)]
    [InlineData("1.0", true)]
    [InlineData("1e2", true)]
    [InlineData("0.1e1", true)]
    [InlineData("1.0e-0", true)]
    [InlineData("12345678901234567890123456789", true)]
    [InlineData("1.5", false)]
    [InlineData("1.25e1", false)]
    [InlineData("1e-1", false)]
    [InlineData("10e-1", false)] // an integer in value, not in spelling: as the library judged it
    [InlineData("100e-2", false)]
    [InlineData("0.001", false)]
    public void Integer_is_decided_from_the_spelling_of_the_number(string number, bool isInteger)
    {
        Assert.Equal(isInteger, Check("{\"type\":\"integer\"}", number).Count == 0);
        Assert.Empty(Check("{\"type\":\"number\"}", number));
    }

    // ---- required, properties, additionalProperties --------------------------------------------------------------------------------------------

    [Fact]
    public void Required_lists_what_is_missing_in_the_schemas_order_and_ignores_what_is_not_an_object()
    {
        const string schema = "{\"required\":[\"b\",\"a\",\"c\"]}";

        Assert.Empty(Check(schema, "{\"a\":1,\"b\":2,\"c\":3}"));
        Assert.Equal(["(document): Required properties [\"b\",\"c\"] are not present"], Check(schema, "{\"a\":1}"));
        Assert.Empty(Check(schema, "[]"));
        Assert.Empty(Check(schema, "\"a\""));
    }

    [Fact]
    public void Required_counts_a_member_that_is_null_as_present_and_names_are_exact()
    {
        const string schema = "{\"required\":[\"a\"]}";

        Assert.Empty(Check(schema, "{\"a\":null}"));
        Assert.Single(Check(schema, "{\"A\":1}"));
        Assert.Empty(Check(schema, "{\"\\u0061\":1}"));
    }

    [Fact]
    public void Properties_checks_each_member_that_is_there_and_names_the_failures_at_the_parent_and_at_each_member()
    {
        const string schema = "{\"properties\":{\"x\":{\"type\":\"string\"},\"y\":{\"type\":\"number\"},\"z\":{\"type\":\"boolean\"}}}";

        Assert.Empty(Check(schema, "{\"y\":1}"));
        Assert.Empty(Check(schema, "{\"other\":[]}"));
        Assert.Equal(
            [
                "(document): Some properties did not match the required schema: [\"x\",\"z\"]",
                "/x: Value is \"integer\" but should be \"string\"",
                "/z: Value is \"null\" but should be \"boolean\"",
            ],
            Check(schema, "{\"z\":null,\"y\":2,\"x\":1}"));
        Assert.Empty(Check(schema, "[1]"));
    }

    [Fact]
    public void Only_the_first_of_two_members_with_the_same_name_is_checked_as_a_declared_property()
    {
        const string schema = "{\"properties\":{\"x\":{\"type\":\"string\"}},\"additionalProperties\":false}";

        Assert.Empty(Check(schema, "{\"x\":\"ok\",\"x\":1}"));
        Assert.Equal(2, Check(schema, "{\"x\":1,\"x\":\"ok\"}").Count);
    }

    [Fact]
    public void AdditionalProperties_false_refuses_every_member_the_properties_do_not_declare_and_says_so_twice()
    {
        const string schema = "{\"properties\":{\"a\":{}},\"additionalProperties\":false}";

        Assert.Empty(Check(schema, "{\"a\":1}"));
        Assert.Equal(
            [
                "(document): Some additional properties did not match the required schema: [\"b\",\"c\"]",
                "/b: All values fail against the false schema",
                "/c: All values fail against the false schema",
            ],
            Check(schema, "{\"b\":1,\"a\":1,\"c\":2}"));
    }

    [Fact]
    public void AdditionalProperties_is_applied_after_properties_whichever_order_the_schema_writes_them_in()
    {
        const string before = "{\"additionalProperties\":false,\"properties\":{\"a\":{\"type\":\"string\"}}}";
        const string after = "{\"properties\":{\"a\":{\"type\":\"string\"}},\"additionalProperties\":false}";
        const string both = "{\"a\":1,\"b\":2}";

        Assert.Equal(Check(after, both), Check(before, both));
        Assert.Equal(
            [
                "(document): Some properties did not match the required schema: [\"a\"]",
                "(document): Some additional properties did not match the required schema: [\"b\"]",
                "/a: Value is \"integer\" but should be \"string\"",
                "/b: All values fail against the false schema",
            ],
            Check(before, both));
    }

    [Fact]
    public void AdditionalProperties_true_and_a_schema_for_the_members_nothing_declared()
    {
        Assert.Empty(Check("{\"additionalProperties\":true}", "{\"a\":1,\"b\":[null]}"));
        Assert.Empty(Check("{\"additionalProperties\":{\"type\":\"string\"}}", "{\"a\":\"x\"}"));
        Assert.Equal(
            ["(document): Some additional properties did not match the required schema: [\"n\"]", "/n: Value is \"integer\" but should be \"string\""],
            Check("{\"properties\":{\"k\":{\"type\":\"number\"}},\"additionalProperties\":{\"type\":\"string\"}}", "{\"k\":1,\"n\":1,\"s\":\"x\"}"));
    }

    [Fact]
    public void A_location_escapes_tilde_and_slash_and_keeps_everything_else()
    {
        const string schema = "{\"additionalProperties\":{\"type\":\"string\"}}";

        IReadOnlyList<string> lines = Check(schema, "{\"a/b\":1,\"c~d\":1,\"\":1,\"\\u00e9 x\":1,\"%41\":1}");

        Assert.Equal(
            [
                "/a~1b: Value is \"integer\" but should be \"string\"",
                "/c~0d: Value is \"integer\" but should be \"string\"",
                "/: Value is \"integer\" but should be \"string\"",
                "/\u00e9 x: Value is \"integer\" but should be \"string\"",
                "/%41: Value is \"integer\" but should be \"string\"",
            ],
            [.. lines.Skip(1)]);
        Assert.Equal("(document): Some additional properties did not match the required schema: [\"a/b\",\"c~d\",\"\",\"\\u00E9 x\",\"%41\"]", lines[0]);
    }

    // ---- items, $ref, the order lines come in ------------------------------------------------------------------------------------------------

    [Fact]
    public void Items_checks_every_element_and_names_the_failing_indexes()
    {
        const string schema = "{\"items\":{\"type\":\"integer\"}}";

        Assert.Empty(Check(schema, "[]"));
        Assert.Empty(Check(schema, "[1,2,3]"));
        Assert.Equal(
            ["(document): Some items do not match the required schema; failing indexes: [1,3]", "/1: Value is \"string\" but should be \"integer\"", "/3: Value is \"null\" but should be \"integer\""],
            Check(schema, "[1,\"a\",2,null]"));
        Assert.Empty(Check(schema, "{\"a\":\"x\"}"));
    }

    [Fact]
    public void A_ref_to_a_definition_or_to_the_whole_schema_is_followed_and_adds_a_level()
    {
        const string schema = "{\"$defs\":{\"id\":{\"type\":\"string\",\"minLength\":2}},\"type\":\"object\",\"properties\":{\"id\":{\"$ref\":\"#/$defs/id\"},\"child\":{\"$ref\":\"#\"}}}";

        Assert.Empty(Check(schema, "{\"id\":\"ab\",\"child\":{\"id\":\"cd\"}}"));
        Assert.Equal(
            [
                "(document): Some properties did not match the required schema: [\"id\",\"child\"]",
                "/id: Value should be at least 2 characters",
                "/child: Some properties did not match the required schema: [\"id\"]",
                "/child/id: Value is \"integer\" but should be \"string\"",
            ],
            Check(schema, "{\"id\":\"a\",\"child\":{\"id\":1}}"));
    }

    [Fact]
    public void Lines_come_breadth_first_through_the_evaluations_and_a_ref_is_a_level_of_its_own()
    {
        // /a and /b are one level down; /b/c is two; /d goes through a $ref, which is a level of its own, so its line is at the second level, after /b/c's.
        const string schema = "{\"$defs\":{\"s\":{\"type\":\"string\"}},\"properties\":{\"a\":{\"type\":\"string\"},\"b\":{\"properties\":{\"c\":{\"type\":\"string\"}}},\"d\":{\"$ref\":\"#/$defs/s\"}}}";

        IReadOnlyList<string> lines = Check(schema, "{\"d\":1,\"b\":{\"c\":1},\"a\":1}");

        Assert.Equal(
            [
                "(document): Some properties did not match the required schema: [\"a\",\"b\",\"d\"]",
                "/a: Value is \"integer\" but should be \"string\"",
                "/b: Some properties did not match the required schema: [\"c\"]",
                "/b/c: Value is \"integer\" but should be \"string\"",
                "/d: Value is \"integer\" but should be \"string\"",
            ],
            lines);
    }

    [Fact]
    public void A_line_that_would_be_said_twice_is_said_once()
    {
        const string schema = "{\"additionalProperties\":false}";

        IReadOnlyList<string> lines = Check(schema, "{\"a\":1,\"a\":2}");

        Assert.Equal(["(document): Some additional properties did not match the required schema: [\"a\"]", "/a: All values fail against the false schema"], lines);
    }

    // ---- boolean schemas, enum, const ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_boolean_schemas_accept_everything_and_nothing()
    {
        Assert.Empty(Check("true", "{\"a\":1}"));
        Assert.Equal(["(document): All values fail against the false schema"], Check("false", "1"));
        Assert.Equal(["/x: All values fail against the false schema"], Check("{\"properties\":{\"x\":false}}", "{\"x\":1}").Skip(1).ToList());
    }

    [Fact]
    public void Enum_accepts_exactly_the_listed_values_by_value()
    {
        const string schema = "{\"enum\":[\"a\",1,null,[1,2],{\"k\":true}]}";

        foreach (string ok in new[] { "\"a\"", "1", "1.0", "1e0", "null", "[1,2]", "{\"k\":true}" })
        {
            Assert.Empty(Check(schema, ok));
        }

        foreach (string bad in new[] { "\"A\"", "\"1\"", "2", "false", "[2,1]", "[1]", "{\"k\":false}", "{\"k\":true,\"j\":1}", "{}" })
        {
            Assert.Equal(["(document): Value should match one of the values specified by the enum"], Check(schema, bad));
        }
    }

    [Fact]
    public void Const_accepts_only_its_value_and_says_what_it_expected()
    {
        Assert.Empty(Check("{\"const\":1}", "1"));
        Assert.Empty(Check("{\"const\":1}", "1.0"));
        Assert.Equal(["(document): Expected \"1\""], Check("{\"const\":1}", "2"));
        Assert.Equal(["(document): Expected \"1\""], Check("{\"const\":1}", "\"1\""));
        Assert.Equal(["(document): Expected \"{\"a\":[1,2]}\""], Check("{\"const\":{\"a\":[1,2]}}", "{\"a\":[2,1]}"));
        Assert.Empty(Check("{\"const\":{\"a\":[1,2],\"b\":null}}", "{\"b\":null,\"a\":[1,2]}"));
    }

    [Fact]
    public void Numbers_are_the_same_value_to_the_precision_decimal_holds_and_exactly_beyond_it()
    {
        // 29 digits fit a decimal and a 40-digit spelling rounds to 1, as the library judged (docs/adr/0004).
        Assert.Empty(Check("{\"const\":1}", "1.0000000000000000000000000000000000001"));
        Assert.Single(Check("{\"const\":1}", "1.00000000000000000001"));
        // Outside decimal's range the library threw; the numbers are compared exactly instead.
        Assert.Single(Check("{\"const\":1}", "1e30"));
        Assert.Empty(Check("{\"const\":1e30}", "1e30"));
        Assert.Empty(Check("{\"const\":1e30}", "10e29"));
        Assert.Single(Check("{\"const\":1e30}", "1.0000000000000000000000000000001e30"));
    }

    // ---- pattern, length, count ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Pattern_is_not_anchored_unless_it_says_so_and_ignores_what_is_not_a_string()
    {
        Assert.Empty(Check("{\"pattern\":\"b\"}", "\"abc\""));
        Assert.Single(Check("{\"pattern\":\"^b\"}", "\"abc\""));
        Assert.Equal(["(document): The string value is not a match for the indicated regular expression"], Check("{\"pattern\":\"^[a-z]+$\"}", "\"ab1\""));
        Assert.Empty(Check("{\"pattern\":\"^[a-z]+$\"}", "5"));
        Assert.Empty(Check("{\"pattern\":\"^[a-z]+$\"}", "null"));
    }

    [Theory]
    [InlineData("\"abc\\n\"", true)] // a final line feed is allowed before $, as the library allowed it
    [InlineData("\"abc\\n\\n\"", false)]
    [InlineData("\"abc\\r\\n\"", false)]
    [InlineData("\"\\nabc\"", false)]
    [InlineData("\"abc \"", false)]
    public void Pattern_dollar_matches_before_one_final_line_feed(string json, bool matches)
    {
        Assert.Equal(matches, Check("{\"pattern\":\"^abc$\"}", json).Count == 0);
    }

    [Theory]
    [InlineData("\"123\"", true)]
    [InlineData("\"\\u0661\\u0662\\u0663\"", false)] // Arabic-Indic digits
    [InlineData("\"\\uff11\\uff12\\uff13\"", false)] // fullwidth digits
    public void Pattern_character_classes_are_ascii_when_they_name_ascii_ranges(string json, bool matches)
    {
        Assert.Equal(matches, Check("{\"pattern\":\"^[0-9]+$\"}", json).Count == 0);
        Assert.Equal(matches, Check("{\"pattern\":\"^\\\\d+$\"}", json).Count == 0);
    }

    [Fact]
    public void Length_counts_characters_as_a_reader_sees_them_not_utf16_units()
    {
        const string schema = "{\"minLength\":2,\"maxLength\":3}";
        string family = "\\ud83d\\udc68\\u200d\\ud83d\\udc69\\u200d\\ud83d\\udc67";

        Assert.Empty(Check(schema, "\"ab\""));
        Assert.Empty(Check(schema, "\"abc\""));
        Assert.Equal(["(document): Value should be at least 2 characters"], Check(schema, "\"a\""));
        Assert.Equal(["(document): Value should be at most 3 characters"], Check(schema, "\"abcd\""));
        Assert.Empty(Check(schema, $"\"{family}{family}\"")); // two clusters, 16 UTF-16 units
        Assert.Equal(["(document): Value should be at least 2 characters"], Check(schema, $"\"{family}\""));
        Assert.Empty(Check(schema, "\"e\\u0301e\\u0301e\\u0301\"")); // three letters with accents, six units
        Assert.Equal(["(document): Value should be at most 3 characters"], Check(schema, "\"e\\u0301e\\u0301e\\u0301e\\u0301\""));
        Assert.Empty(Check(schema, "\"a\\r\\n\"")); // CR LF is one character
        Assert.Empty(Check(schema, "5"));
    }

    [Fact]
    public void The_fast_path_for_text_length_agrees_with_the_grapheme_count_for_every_short_string_it_takes()
    {
        for (int a = 0; a < 0x300; a++)
        {
            Assert.Equal(1, SchemaValidator.TextElements(((char)a).ToString()));
            for (int b = 0; b < 0x300; b++)
            {
                string pair = new([(char)a, (char)b]);
                int expected = new StringInfo(pair).LengthInTextElements;
                Assert.True(expected == SchemaValidator.TextElements(pair), $"U+{a:X4} U+{b:X4}");
            }
        }

        Assert.Equal(0, SchemaValidator.TextElements(string.Empty));
    }

    [Fact]
    public void ItemCount_limits_say_what_they_wanted()
    {
        const string schema = "{\"minItems\":1,\"maxItems\":2}";

        Assert.Empty(Check(schema, "[1]"));
        Assert.Empty(Check(schema, "[1,2]"));
        Assert.Equal(["(document): Value should have at least 1 items"], Check(schema, "[]"));
        Assert.Equal(["(document): Value should have at most 2 items"], Check(schema, "[1,2,3]"));
        Assert.Empty(Check(schema, "\"abc\""));
    }

    // ---- bounds -------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Bounds_compare_exactly_whatever_the_spelling()
    {
        const string schema = "{\"minimum\":0,\"maximum\":600000,\"exclusiveMinimum\":-1}";

        foreach (string ok in new[] { "0", "-0", "0.0", "0e5", "600000", "6e5", "600000.0", "599999.999999999999999999999999999999", "0.000000000000000000000000000000000001", "1e-400" })
        {
            Assert.Empty(Check(schema, ok));
        }

        foreach (string bad in new[] { "-1", "-0.000000000000000000000000000000000001", "600000.000000000000000000000000000000000001", "6.000000000000000000000000000000000001e5", "1e400", "-1e400" })
        {
            Assert.NotEmpty(Check(schema, bad));
        }

        Assert.Empty(Check(schema, "\"600001\""));
        Assert.Empty(Check(schema, "null"));
    }

    [Fact]
    public void Each_bound_names_its_limit_in_the_words_of_the_message()
    {
        Assert.Equal(["(document): -1 should be at least 0"], Check("{\"minimum\":0}", "-1"));
        Assert.Equal(["(document): 2 should be at most 1"], Check("{\"maximum\":1}", "2"));
        Assert.Equal(["(document): 0 should be greater than 0"], Check("{\"exclusiveMinimum\":0}", "0"));
        Assert.Equal(["(document): 1E+2 should be less than 100"], Check("{\"exclusiveMaximum\":100}", "1E+2"));
        Assert.Empty(Check("{\"minimum\":0}", "0"));
        Assert.Empty(Check("{\"maximum\":1}", "1"));
        Assert.Empty(Check("{\"exclusiveMinimum\":0}", "1e-400"));
        Assert.Empty(Check("{\"exclusiveMaximum\":100}", "99.99"));
    }

    [Fact]
    public void Number_comparison_orders_by_sign_then_magnitude_then_digits()
    {
        string[] ascending = ["-1e400", "-1000", "-1.5", "-1", "-0.5", "-1e-400", "0", "1e-400", "0.5", "1", "1.25", "1.5", "10", "123456789012345678901234567890", "1e400"];
        for (int i = 0; i < ascending.Length; i++)
        {
            for (int j = 0; j < ascending.Length; j++)
            {
                JsonNumberText.Limit limit = JsonNumberText.ReadLimit(JsonDocument.Parse(ascending[j]).RootElement);
                int actual = JsonNumberText.Compare(JsonDocument.Parse(ascending[i]).RootElement, in limit);
                Assert.True(Math.Sign(actual) == Math.Sign(i.CompareTo(j)), $"{ascending[i]} against {ascending[j]} gave {actual}");
            }
        }
    }

    // ---- format ------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("2026-10-01T12:30:45Z", true)]
    [InlineData("2026-10-01T12:30:45.123456789+01:00", true)]
    [InlineData("2026-10-01t12:30:45z", true)]
    [InlineData("2026-10-01 12:30:45-23:59", true)]
    [InlineData("2026-10-01_12:30:45Z", true)]
    [InlineData("2024-02-29T00:00:00Z", true)]
    [InlineData("0000-02-29T00:00:00Z", true)]
    [InlineData("2026-12-31T23:59:60Z", true)]
    [InlineData("2026-12-31T00:59:60+01:00", true)]
    [InlineData("2026-12-31T22:59:60-01:00", true)]
    [InlineData("2026-10-01T12:30:45Z\n", true)]
    [InlineData("2026-10-01T12:30:45Z\n\n", false)]
    [InlineData("2026-10-01T12:30:45Z\r\n", false)]
    [InlineData("2026-13-01T00:00:00Z", false)]
    [InlineData("2026-02-29T00:00:00Z", false)]
    [InlineData("1900-02-29T00:00:00Z", false)]
    [InlineData("2026-04-31T00:00:00Z", false)]
    [InlineData("2026-10-01T24:00:00Z", false)]
    [InlineData("2026-10-01T12:60:00Z", false)]
    [InlineData("2026-10-01T12:30:61Z", false)]
    [InlineData("2026-10-01T12:30:60Z", false)]
    [InlineData("2026-12-31T23:59:60-01:00", false)]
    [InlineData("2026-10-01T12:30:45+24:00", false)]
    [InlineData("2026-10-01T12:30:45+00:60", false)]
    [InlineData("2026-10-01T12:30:45", false)]
    [InlineData("2026-10-01T12:30:45+0100", false)]
    [InlineData("2026-10-01T12:30:45.Z", false)]
    [InlineData("2026-10-01T12:30Z", false)]
    [InlineData("2026-10-1T12:30:45Z", false)]
    [InlineData("20261001T123045Z", false)]
    [InlineData(" 2026-10-01T12:30:45Z", false)]
    [InlineData("2026-10-01T12:30:45Z ", false)]
    [InlineData("\u0662\u0660\u0662\u0666-10-01T12:30:45Z", false)]
    [InlineData("", false)]
    public void Date_time_is_rfc_3339_as_the_previous_validator_read_it(string text, bool valid)
    {
        string json = JsonSerializer.Serialize(text);

        Assert.Equal(valid, Check("{\"format\":\"date-time\"}", json).Count == 0);
        Assert.Empty(Check("{\"format\":\"date-time\"}", "5"));
    }

    [Fact]
    public void A_date_time_violation_says_the_format()
    {
        Assert.Equal(["(document): Value does not match format \"date-time\""], Check("{\"format\":\"date-time\"}", "\"yesterday\""));
    }

    // ---- text that is not text -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_lone_surrogate_is_a_violation_where_a_string_keyword_reads_it_and_nothing_where_none_does()
    {
        const string schema = "{\"properties\":{\"free\":{\"type\":\"string\"},\"id\":{\"type\":\"string\",\"pattern\":\"^a\",\"maxLength\":9,\"enum\":[\"a\"]}}}";

        Assert.Empty(Check(schema, "{\"free\":\"\\ud800\"}"));
        Assert.Equal(
            ["(document): Some properties did not match the required schema: [\"id\"]", "/id: Value is not valid Unicode text"],
            Check(schema, "{\"id\":\"\\ud800\"}"));
    }

    [Fact]
    public void A_member_name_that_is_not_text_is_shown_as_written_and_never_throws()
    {
        IReadOnlyList<string> lines = Check("{\"additionalProperties\":false}", "{\"\\ud800\":1}");

        Assert.Equal(2, lines.Count);
        Assert.StartsWith("/\\ud800: ", lines[1], StringComparison.Ordinal);
        Assert.Empty(Check("{\"additionalProperties\":true}", "{\"\\ud800\":1}"));
    }

    [Fact]
    public void Text_that_is_not_json_is_a_json_exception_and_nothing_else()
    {
        SchemaValidator validator = SchemaValidator.Compile("{}");

        Assert.ThrowsAny<JsonException>(() => validator.Validate("{"));
        Assert.ThrowsAny<JsonException>(() => validator.Validate(string.Empty));
        Assert.ThrowsAny<JsonException>(() => validator.Validate("{\"a\":1} x"));
    }

    [Fact]
    public void A_body_with_hundreds_of_thousands_of_unknown_members_is_reported_in_linear_time()
    {
        // How the cost grows is counted, never timed. A ratio of two timings flaked on a loaded Windows runner (4x the members took 8.0x as long) although
        // the work is linear: it allocates ~800 bytes a member, all of which lives until the end, so a third to a half of the time is garbage collection,
        // and that share is not steady. Measured on this validator: allocations identical on macOS, Linux and Windows and 4.1x for 4x the members, while
        // single-shot ratios of the wall-clock times ran from 1x to 27x (over 8x in up to one run in five once other threads allocated in the same process,
        // as a test host running classes in parallel does). So:
        //  - listing a refused name must cost one hash and next to no comparisons (a scan of the names listed so far costs a comparison per name listed);
        //  - the bytes allocated must grow with the members (four times the members, not sixteen times the bytes);
        //  - the wall clock only has a ceiling a loaded machine cannot reach, which still stops anything that takes seconds for a body of this size.
        SchemaValidator validator = SchemaValidator.Compile("{\"additionalProperties\":false}");
        static string Body(int members) => "{" + string.Join(',', Enumerable.Range(0, members).Select(i => "\"m" + i.ToString(CultureInfo.InvariantCulture) + "\":1")) + "}";

        // The warm-up is counted too, so a scan fails here, in a fraction of a second, instead of after minutes on the large body.
        var warmNames = new CountingNames(budget: 1_000_000);
        Assert.Equal(10_001, Measure(validator, Body(10_000), warmNames).Lines);
        AssertListedByHashing(warmNames, 10_000);

        const int Small = 50_000;
        const int Large = 200_000;
        string smallBody = Body(Small);
        string largeBody = Body(Large);
        var smallNames = new CountingNames(budget: 10L * Small);
        var largeNames = new CountingNames(budget: 10L * Large);
        Measurement small = Measure(validator, smallBody, smallNames);
        Measurement large = Measure(validator, largeBody, largeNames);

        Assert.Equal(Small + 1, small.Lines);
        Assert.Equal(Large + 1, large.Lines);
        AssertListedByHashing(smallNames, Small);
        AssertListedByHashing(largeNames, Large);

        // Four times the members allocate about four times the bytes (4.1 times measured, the same on macOS, Linux and Windows); anything built per
        // member from all the members so far would allocate far more than that.
        Assert.True(large.AllocatedBytes < 6.0 * small.AllocatedBytes, $"4x the members allocated {(double)large.AllocatedBytes / small.AllocatedBytes:F1}x the bytes ({small.AllocatedBytes:N0}, {large.AllocatedBytes:N0})");

        // What a refused member costs is bounded too: the same bytes for the first member as the last, not only a steady growth. 820 a member before the
        // violation's line was written once, as one string (and a listed name that needs no escaping stopped taking two encoder objects); 500 since. This
        // is the memory a 10 MiB body of unknown members costs (limits.maxDocumentBytes), so it stops at a ceiling between the two.
        Assert.True(large.AllocatedBytes < 650L * Large, $"{Large:N0} refused members allocated {(double)large.AllocatedBytes / Large:F0} bytes each");

        // Linear takes 0.1 to 0.7 s here (0.63 s on the runner that flaked); a validator that is quadratic in the members takes minutes.
        Assert.True(large.Elapsed < LoadScale.Scaled(TimeSpan.FromSeconds(10)), $"{Large:N0} members took {large.Elapsed.TotalSeconds:F1} s");
    }

    [Fact]
    public void A_message_lists_a_name_as_the_json_encoder_writes_it_whatever_the_name_holds()
    {
        // Every character from NUL to U+024F on its own, and in a name with letters round it (the unescaped names take a path of their own), the astral
        // plane and the characters the encoder writes as a short escape. The expected text comes from JsonEncodedText, never from the validator.
        List<string> names = [];
        for (int c = 0; c <= 0x24F; c++)
        {
            if (c is >= 0xD800 and <= 0xDFFF)
            {
                continue;
            }

            names.Add(((char)c).ToString());
            names.Add("a" + (char)c + "b");
        }

        names.AddRange(["\U0001F600", "x\U0001F600", "plain_name-1.0", "with space", "tab\there", "quote\"here", "back\\slash", "a/b", "c~d", "<&>'+`", "\u2028", "\uFFFF"]);
        names = [.. names.Distinct(StringComparer.Ordinal)];
        string body = "{" + string.Join(',', names.Select(name => JsonSerializer.Serialize(name) + ":1")) + "}";

        IReadOnlyList<string> lines = Check("{\"additionalProperties\":false}", body);

        string expected = "(document): Some additional properties did not match the required schema: [" + string.Join(',', names.Select(name => "\"" + JsonEncodedText.Encode(name) + "\"")) + "]";
        Assert.Equal(expected, lines[0]);
        Assert.Equal(names.Count + 1, lines.Count);
    }

    [Fact]
    public void A_location_writes_an_array_index_in_every_number_of_digits()
    {
        const int Items = 1_200;
        string body = "[" + string.Join(',', Enumerable.Repeat("1", Items)) + "]";

        IReadOnlyList<string> lines = Check("{\"items\":false}", body);

        Assert.Equal(Items + 1, lines.Count);
        Assert.Equal("(document): Some items do not match the required schema; failing indexes: [" + string.Join(',', Enumerable.Range(0, Items)) + "]", lines[0]);
        for (int i = 0; i < Items; i++)
        {
            Assert.Equal($"/{i.ToString(CultureInfo.InvariantCulture)}: All values fail against the false schema", lines[i + 1]);
        }
    }

    /// <summary>
    /// One hash per refused name, the set's own; none at all means the names were not looked up in a set (they were scanned for), and many times that
    /// means the set was rebuilt. Comparisons only happen for two names with the same 32-bit hash, so a handful at most (none for these names): a scan
    /// makes one per name listed so far.
    /// </summary>
    private static void AssertListedByHashing(CountingNames names, int members)
    {
        Assert.True(names.Hashes >= members && names.Hashes <= 2L * members, $"{members:N0} refused names were hashed {names.Hashes:N0} times, not once each: they were scanned for instead of looked up, or looked up repeatedly");
        Assert.True(names.Comparisons <= members / 100, $"{members:N0} refused names took {names.Comparisons:N0} comparisons: only names with the same hash should be compared");
    }

    private readonly record struct Measurement(int Lines, long AllocatedBytes, TimeSpan Elapsed);

    private static Measurement Measure(SchemaValidator validator, string json, IEqualityComparer<string> names)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        using JsonDocument document = JsonDocument.Parse(json);
        int lines = validator.Evaluate(document.RootElement, names).Count;
        timer.Stop();
        return new Measurement(lines, GC.GetAllocatedBytesForCurrentThread() - before, timer.Elapsed);
    }

    /// <summary>
    /// Ordinal comparison that counts what it is asked. Its hash is FNV-1a, not <c>string.GetHashCode</c> (which is different in every process), so the
    /// counts are the same on every run and every machine. It gives up once it has been asked for more comparisons than <c>budget</c>, so a regression to
    /// a scan through it fails at once instead of running for minutes.
    /// </summary>
    private sealed class CountingNames(long budget) : IEqualityComparer<string>
    {
        public long Hashes { get; private set; }

        public long Comparisons { get; private set; }

        public bool Equals(string? x, string? y)
        {
            if (++Comparisons > budget)
            {
                throw new InvalidOperationException($"Asked to compare names {Comparisons:N0} times: the names listed so far are being scanned.");
            }

            return string.Equals(x, y, StringComparison.Ordinal);
        }

        public int GetHashCode(string name)
        {
            Hashes++;
            uint hash = 2166136261;
            foreach (char c in name)
            {
                hash = (hash ^ c) * 16777619;
            }

            return (int)hash;
        }
    }

    [Fact]
    public void One_validator_serves_many_threads_at_once()
    {
        SchemaValidator validator = SchemaValidator.Compile(File.ReadAllText(ContractFiles.SchemaPath));
        string valid = ContractFiles.ReadExample();
        string invalid = valid.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal);

        Parallel.For(0, 400, i =>
        {
            Assert.Empty(validator.Validate(valid));
            Assert.Equal(2, validator.Validate(invalid).Count);
        });
    }
}
