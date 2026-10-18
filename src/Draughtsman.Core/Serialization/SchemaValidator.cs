using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Draughtsman.Core.Serialization;

/// <summary>
/// A JSON Schema (draft 2020-12) validator for exactly the keywords <c>docs/schema/document.schema.json</c> uses and no others, so that the product
/// depends on no schema library (docs/adr/0004-document-schema-validator.md: the library it replaced is under a licence that charges whoever
/// redistributes its binary). <see cref="Compile"/> refuses a schema that uses a keyword this class does not implement, so a keyword added to the
/// schema without being implemented here fails a test the day it is added, instead of being ignored.
/// <para>
/// Implemented: <c>type</c> (a single type name), <c>properties</c>, <c>required</c>, <c>additionalProperties</c>, <c>items</c> (one schema),
/// <c>$ref</c> to <c>#</c> or <c>#/$defs/name</c>, <c>enum</c>, <c>const</c>, <c>pattern</c>, <c>minLength</c>, <c>maxLength</c>, <c>minItems</c>,
/// <c>maxItems</c>, <c>minimum</c>, <c>maximum</c>, <c>exclusiveMinimum</c>, <c>exclusiveMaximum</c>, <c>format: date-time</c>, the boolean schemas,
/// and the annotations. Format is always asserted.
/// </para>
/// <para>
/// What it reports and in what order is not incidental: the 400 body of the document API and the MCP error carry these lines to clients, and the
/// corpus in <c>tests/Draughtsman.Core.Tests/SchemaCorpus</c> pins them against what the previous library said for ten thousand documents. A line is
/// <c>location: message</c> (the location a JSON pointer, or <c>(document)</c>); every keyword that fails at an evaluated node says so, a failing
/// <c>properties</c>, <c>additionalProperties</c> or <c>items</c> says so at the parent as well as at each child; lines come breadth-first through the tree of
/// evaluations (a <c>$ref</c> is a level of its own), then in document order; duplicates are dropped. Where this class is deliberately stricter than that
/// library (it threw), see ADR 0004.
/// </para>
/// </summary>
internal sealed class SchemaValidator
{
    private const string Dialect = "https://json-schema.org/draft/2020-12/schema";

    private static readonly string[] s_annotations = ["$schema", "$id", "$comment", "title", "description", "default", "examples"];

    private readonly Node _root;

    private SchemaValidator(Node root)
    {
        _root = root;
    }

    /// <summary>
    /// Builds a validator from a schema document. Throws <see cref="NotSupportedException"/> naming the path and the keyword for a keyword (or a form of
    /// one) this class does not implement, and <see cref="InvalidOperationException"/> for a schema that is malformed or whose reference does not resolve.
    /// </summary>
    public static SchemaValidator Compile(string schemaJson)
    {
        using JsonDocument document = JsonDocument.Parse(schemaJson);
        return new SchemaValidator(new Compiler().Compile(document.RootElement));
    }

    /// <summary>One <c>location: message</c> line per violation; empty when the JSON text is valid. Throws <see cref="JsonException"/> for text that is not JSON.</summary>
    public IReadOnlyList<string> Validate(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return Evaluate(document.RootElement);
    }

    public IReadOnlyList<string> Evaluate(JsonElement instance)
    {
        return Evaluate(instance, StringComparer.Ordinal);
    }

    /// <summary>
    /// <paramref name="names"/> is how the names of refused members are told apart when a message lists them (always ordinal outside a test). A test
    /// counts through it to prove that listing a name costs one hash, not a scan of the names listed so far (SchemaValidatorTests): a count cannot
    /// flake on a busy or garbage-collecting machine the way a ratio of timings does.
    /// </summary>
    internal IReadOnlyList<string> Evaluate(JsonElement instance, IEqualityComparer<string> names)
    {
        var run = new Run(names);
        run.EvaluateNode(_root, instance, 0);
        return run.Lines();
    }

    /// <summary>The number of grapheme clusters in a string: what <c>minLength</c> and <c>maxLength</c> count, as the previous library did.</summary>
    internal static int TextElements(string text)
    {
        // Below U+0300 every character is a cluster of its own except a CR LF pair (no combining, joining or prepending character is that low).
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] >= '\u0300' || (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n'))
            {
                return new StringInfo(text).LengthInTextElements;
            }
        }

        return text.Length;
    }

    // ---- the compiled schema ------------------------------------------------------------------------------------------------------------------

    private sealed class Node
    {
        /// <summary>The boolean schemas <c>true</c> and <c>false</c>; null for an object schema.</summary>
        public bool? Constant { get; set; }

        public Keyword[] Keywords { get; set; } = [];
    }

    private abstract class Keyword
    {
        public abstract void Apply(Run run, JsonElement instance, ref Scope scope);
    }

    /// <summary>Where a keyword is being applied: the depth and order of its schema node in the tree of evaluations, and the instance's text once decoded.</summary>
    private struct Scope(int depth, int order)
    {
        public int Depth { get; } = depth;

        public int Order { get; } = order;

        public string? Text { get; set; }

        public bool TextFailed { get; set; }
    }

    private enum SchemaType
    {
        Null,
        Boolean,
        Object,
        Array,
        Number,
        Integer,
        String,
    }

    private sealed class TypeKeyword(SchemaType expected) : Keyword
    {
        public override void Apply(Run run, JsonElement instance, ref Scope scope)
        {
            bool ok = expected switch
            {
                SchemaType.Null => instance.ValueKind == JsonValueKind.Null,
                SchemaType.Boolean => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
                SchemaType.Object => instance.ValueKind == JsonValueKind.Object,
                SchemaType.Array => instance.ValueKind == JsonValueKind.Array,
                SchemaType.String => instance.ValueKind == JsonValueKind.String,
                SchemaType.Number => instance.ValueKind == JsonValueKind.Number,
                _ => instance.ValueKind == JsonValueKind.Number && JsonNumberText.IsInteger(instance),
            };
            if (!ok)
            {
                run.Report(in scope, $"Value is \"{Describe(instance)}\" but should be \"{expected.ToString().ToLowerInvariant()}\"");
            }
        }

        private static string Describe(JsonElement instance)
        {
            return instance.ValueKind switch
            {
                JsonValueKind.Object => "object",
                JsonValueKind.Array => "array",
                JsonValueKind.String => "string",
                JsonValueKind.True or JsonValueKind.False => "boolean",
                JsonValueKind.Null => "null",
                _ => JsonNumberText.IsInteger(instance) ? "integer" : "number",
            };
        }
    }

    private sealed class RequiredKeyword(string[] names) : Keyword
    {
        public override void Apply(Run run, JsonElement instance, ref Scope scope)
        {
            if (instance.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            List<string>? missing = null;
            foreach (string name in names)
            {
                if (!JsonMembers.Has(instance, name) && !(missing ??= []).Contains(name))
                {
                    missing.Add(name);
                }
            }

            if (missing is not null)
            {
                run.Report(in scope, $"Required properties {NameList(missing)} are not present");
            }
        }
    }

    private sealed class PropertiesKeyword(PropertySchema[] properties) : Keyword
    {
        public string[] Names { get; } = [.. properties.Select(p => p.Name)];

        public override void Apply(Run run, JsonElement instance, ref Scope scope)
        {
            if (instance.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            List<string>? failed = null;
            foreach (PropertySchema property in properties)
            {
                // The first occurrence of a member name is the one that is checked (see ADR 0004, duplicate member names).
                if (!JsonMembers.TryGetFirst(instance, property.Name, out JsonElement value))
                {
                    continue;
                }

                run.Push(property.Name);
                bool ok = run.EvaluateNode(property.Schema, value, scope.Depth + 1);
                run.Pop();
                if (!ok)
                {
                    (failed ??= []).Add(property.Name);
                }
            }

            if (failed is not null)
            {
                run.Report(in scope, $"Some properties did not match the required schema: {NameList(failed)}");
            }
        }
    }

    private sealed record PropertySchema(string Name, Node Schema);

    private sealed class AdditionalPropertiesKeyword(Node schema, PropertiesKeyword? declared) : Keyword
    {
        public override void Apply(Run run, JsonElement instance, ref Scope scope)
        {
            if (instance.ValueKind != JsonValueKind.Object || schema.Constant == true)
            {
                return;
            }

            List<string>? failed = null;
            HashSet<string>? failedSet = null; // a body can carry a million unknown members: finding a repeated name must not be a scan of the list
            foreach (JsonProperty member in instance.EnumerateObject())
            {
                if (IsDeclared(member))
                {
                    continue;
                }

                string name = JsonMembers.Name(member);
                run.Push(name);
                bool ok = run.EvaluateNode(schema, member.Value, scope.Depth + 1);
                run.Pop();
                if (!ok && (failedSet ??= new HashSet<string>(run.Names)).Add(name))
                {
                    (failed ??= []).Add(name);
                }
            }

            if (failed is not null)
            {
                run.Report(in scope, $"Some additional properties did not match the required schema: {NameList(failed)}");
            }
        }

        private bool IsDeclared(JsonProperty member)
        {
            if (declared is null)
            {
                return false;
            }

            foreach (string name in declared.Names)
            {
                if (JsonMembers.Is(member, name))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private sealed class ItemsKeyword(Node schema) : Keyword
    {
        public override void Apply(Run run, JsonElement instance, ref Scope scope)
        {
            if (instance.ValueKind != JsonValueKind.Array || schema.Constant == true)
            {
                return;
            }

            List<int>? failed = null;
            int index = 0;
            foreach (JsonElement item in instance.EnumerateArray())
            {
                run.Push(index);
                bool ok = run.EvaluateNode(schema, item, scope.Depth + 1);
                run.Pop();
                if (!ok)
                {
                    (failed ??= []).Add(index);
                }

                index++;
            }

            if (failed is not null)
            {
                run.Report(in scope, "Some items do not match the required schema; failing indexes: [" + string.Join(',', failed.Select(i => i.ToString(CultureInfo.InvariantCulture))) + "]");
            }
        }
    }

    private sealed class RefKeyword(string target) : Keyword
    {
        public string Target { get; } = target;

        public Node? Resolved { get; set; }

        public override void Apply(Run run, JsonElement instance, ref Scope scope)
        {
            run.EvaluateNode(Resolved!, instance, scope.Depth + 1);
        }
    }

    private sealed class EnumKeyword(JsonElement[] values) : Keyword
    {
        public override void Apply(Run run, JsonElement instance, ref Scope scope)
        {
            string? text = null;
            if (instance.ValueKind == JsonValueKind.String && !run.TryGetText(instance, ref scope, out text))
            {
                return;
            }

            if (!values.Any(value => text is not null ? value.ValueKind == JsonValueKind.String && value.ValueEquals(text) : SchemaJson.Equal(value, instance)))
            {
                run.Report(in scope, "Value should match one of the values specified by the enum");
            }
        }
    }

    private sealed class ConstKeyword(JsonElement value) : Keyword
    {
        private readonly string _text = value.GetRawText();

        public override void Apply(Run run, JsonElement instance, ref Scope scope)
        {
            // Only a string is compared as text; a string against the number 1 is simply not equal, whatever it holds.
            if (value.ValueKind == JsonValueKind.String && instance.ValueKind == JsonValueKind.String && !run.TryGetText(instance, ref scope, out _))
            {
                return;
            }

            if (!SchemaJson.Equal(value, instance))
            {
                run.Report(in scope, $"Expected \"{_text}\"");
            }
        }
    }

    private sealed class PatternKeyword(Regex pattern) : Keyword
    {
        public override void Apply(Run run, JsonElement instance, ref Scope scope)
        {
            if (instance.ValueKind != JsonValueKind.String || !run.TryGetText(instance, ref scope, out string? text))
            {
                return;
            }

            bool matches;
            try
            {
                matches = pattern.IsMatch(text);
            }
            catch (RegexMatchTimeoutException)
            {
                matches = false; // text that cannot be matched in the time allowed is not accepted
            }

            if (!matches)
            {
                run.Report(in scope, "The string value is not a match for the indicated regular expression");
            }
        }
    }

    private sealed class LengthKeyword(int limit, bool isMaximum) : Keyword
    {
        public override void Apply(Run run, JsonElement instance, ref Scope scope)
        {
            if (instance.ValueKind != JsonValueKind.String || !run.TryGetText(instance, ref scope, out string? text))
            {
                return;
            }

            int length = TextElements(text);
            if (isMaximum && length > limit)
            {
                run.Report(in scope, $"Value should be at most {limit.ToString(CultureInfo.InvariantCulture)} characters");
            }
            else if (!isMaximum && length < limit)
            {
                run.Report(in scope, $"Value should be at least {limit.ToString(CultureInfo.InvariantCulture)} characters");
            }
        }
    }

    private sealed class ItemCountKeyword(int limit, bool isMaximum) : Keyword
    {
        public override void Apply(Run run, JsonElement instance, ref Scope scope)
        {
            if (instance.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            int count = instance.GetArrayLength();
            if (isMaximum && count > limit)
            {
                run.Report(in scope, $"Value should have at most {limit.ToString(CultureInfo.InvariantCulture)} items");
            }
            else if (!isMaximum && count < limit)
            {
                run.Report(in scope, $"Value should have at least {limit.ToString(CultureInfo.InvariantCulture)} items");
            }
        }
    }

    private enum Bound
    {
        Minimum,
        Maximum,
        ExclusiveMinimum,
        ExclusiveMaximum,
    }

    private sealed class BoundKeyword(Bound bound, JsonNumberText.Limit limit) : Keyword
    {
        public override void Apply(Run run, JsonElement instance, ref Scope scope)
        {
            if (instance.ValueKind != JsonValueKind.Number)
            {
                return;
            }

            int comparison = JsonNumberText.Compare(instance, in limit);
            string? problem = bound switch
            {
                Bound.Minimum when comparison < 0 => "at least",
                Bound.Maximum when comparison > 0 => "at most",
                Bound.ExclusiveMinimum when comparison <= 0 => "greater than",
                Bound.ExclusiveMaximum when comparison >= 0 => "less than",
                _ => null,
            };
            if (problem is not null)
            {
                run.Report(in scope, $"{instance.GetRawText()} should be {problem} {limit.Text}");
            }
        }
    }

    private sealed class DateTimeFormatKeyword : Keyword
    {
        public override void Apply(Run run, JsonElement instance, ref Scope scope)
        {
            if (instance.ValueKind != JsonValueKind.String || !run.TryGetText(instance, ref scope, out string? text))
            {
                return;
            }

            if (!Rfc3339.IsDateTime(text))
            {
                run.Report(in scope, "Value does not match format \"date-time\"");
            }
        }
    }

    /// <summary>
    /// A name list as a message prints it: a JSON array of strings, escaped the way <see cref="JsonSerializer"/> escapes them. A body can refuse a million
    /// names and this is built from all of them, so a name that needs no escaping is joined as it is: <see cref="JsonEncodedText.Encode(string, System.Text.Encodings.Web.JavaScriptEncoder?)"/>
    /// allocates two objects per name even then. The names that skip it are a conservative set the encoder never touches (SchemaValidatorTests pins
    /// every character against it).
    /// </summary>
    private static string NameList(List<string> names)
    {
        var builder = new StringBuilder();
        builder.Append('[');
        for (int i = 0; i < names.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append('"').Append(IsPlain(names[i]) ? names[i] : JsonEncodedText.Encode(names[i]).ToString()).Append('"');
        }

        return builder.Append(']').ToString();
    }

    private static bool IsPlain(string name)
    {
        foreach (char c in name)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    // ---- one evaluation -----------------------------------------------------------------------------------------------------------------------

    private readonly record struct PathSegment(string? Name, int Index);

    /// <summary>A reported violation: where it sorts, and the finished <c>location: message</c> line (one string per violation, which is what a body of a million refused members costs).</summary>
    private readonly record struct Entry(int Depth, int Order, int Sequence, string Line);

    private sealed class Run(IEqualityComparer<string> names)
    {
        private readonly List<Entry> _entries = [];
        private readonly List<PathSegment> _path = [];
        private int _nextOrder;

        /// <summary>How the names of refused members are compared and hashed to list each once.</summary>
        public IEqualityComparer<string> Names { get; } = names;

        public void Push(string name) => _path.Add(new PathSegment(name, -1));

        public void Push(int index) => _path.Add(new PathSegment(null, index));

        public void Pop() => _path.RemoveAt(_path.Count - 1);

        /// <summary>Evaluates a schema node against the instance at the current path, one level deeper than its parent; true when nothing was reported.</summary>
        public bool EvaluateNode(Node node, JsonElement instance, int depth)
        {
            int before = _entries.Count;
            var scope = new Scope(depth, _nextOrder++);
            if (node.Constant == false)
            {
                Report(in scope, "All values fail against the false schema");
            }
            else if (node.Constant is null)
            {
                foreach (Keyword keyword in node.Keywords)
                {
                    keyword.Apply(this, instance, ref scope);
                }
            }

            return _entries.Count == before;
        }

        public void Report(in Scope scope, string message)
        {
            _entries.Add(new Entry(scope.Depth, scope.Order, _entries.Count, Line(message)));
        }

        /// <summary>The instance's text. A string that is not valid UTF-16 (a lone surrogate, which JSON permits) has no text to test: it is reported once, and false returned.</summary>
        public bool TryGetText(JsonElement instance, ref Scope scope, [NotNullWhen(true)] out string? text)
        {
            if (scope.Text is not null)
            {
                text = scope.Text;
                return true;
            }

            if (!scope.TextFailed)
            {
                try
                {
                    text = scope.Text = instance.GetString()!;
                    return true;
                }
                catch (InvalidOperationException)
                {
                    scope.TextFailed = true;
                    Report(in scope, "Value is not valid Unicode text");
                }
            }

            text = null;
            return false;
        }

        public IReadOnlyList<string> Lines()
        {
            if (_entries.Count == 0)
            {
                return [];
            }

            _entries.Sort((a, b) =>
            {
                int byDepth = a.Depth.CompareTo(b.Depth);
                if (byDepth != 0)
                {
                    return byDepth;
                }

                int byOrder = a.Order.CompareTo(b.Order);
                return byOrder != 0 ? byOrder : a.Sequence.CompareTo(b.Sequence);
            });

            var lines = new List<string>(_entries.Count);
            var seen = new HashSet<string>(_entries.Count, StringComparer.Ordinal);
            foreach (Entry entry in _entries)
            {
                if (seen.Add(entry.Line))
                {
                    lines.Add(entry.Line);
                }
            }

            return lines;
        }

        /// <summary>
        /// <c>location: message</c>, written into one string of exactly its length. The location is the JSON pointer of the current path (<c>(document)</c> at
        /// the root), with <c>~</c> and <c>/</c> in a name escaped as <c>~0</c> and <c>~1</c>.
        /// </summary>
        private string Line(string message)
        {
            if (_path.Count == 0)
            {
                return "(document): " + message;
            }

            int length = 2 + message.Length;
            foreach (PathSegment segment in _path)
            {
                length += 1 + (segment.Name is null ? DigitCount(segment.Index) : segment.Name.Length + CountEscaped(segment.Name));
            }

            return string.Create(length, (Path: _path, Message: message), static (span, state) =>
            {
                int at = 0;
                foreach (PathSegment segment in state.Path)
                {
                    span[at++] = '/';
                    if (segment.Name is null)
                    {
                        segment.Index.TryFormat(span[at..], out int written, default, CultureInfo.InvariantCulture);
                        at += written;
                        continue;
                    }

                    foreach (char c in segment.Name)
                    {
                        switch (c)
                        {
                            case '~':
                                span[at++] = '~';
                                span[at++] = '0';
                                break;
                            case '/':
                                span[at++] = '~';
                                span[at++] = '1';
                                break;
                            default:
                                span[at++] = c;
                                break;
                        }
                    }
                }

                span[at++] = ':';
                span[at++] = ' ';
                state.Message.AsSpan().CopyTo(span[at..]);
            });
        }

        private static int CountEscaped(string name)
        {
            int count = 0;
            foreach (char c in name)
            {
                if (c is '~' or '/')
                {
                    count++;
                }
            }

            return count;
        }

        private static int DigitCount(int index)
        {
            int digits = 1;
            for (; index >= 10; index /= 10)
            {
                digits++;
            }

            return digits;
        }
    }

    // ---- reading the schema -------------------------------------------------------------------------------------------------------------------

    private sealed class Compiler
    {
        private readonly Dictionary<string, Node> _definitions = new(StringComparer.Ordinal);
        private readonly List<(RefKeyword Keyword, string Path)> _references = [];
        private Node _root = new();

        public Node Compile(JsonElement schema)
        {
            if (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("$schema", out JsonElement dialect) && dialect.GetString() != Dialect)
            {
                throw new NotSupportedException($"#: $schema '{dialect.GetString()}' is not draft 2020-12, the only dialect this validator implements.");
            }

            JsonElement definitions = default;
            if (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("$defs", out definitions))
            {
                foreach (JsonProperty definition in definitions.EnumerateObject())
                {
                    _definitions[definition.Name] = new Node();
                }
            }

            Fill(_root, schema, "#", isRoot: true);
            if (definitions.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty definition in definitions.EnumerateObject())
                {
                    Fill(_definitions[definition.Name], definition.Value, "#/$defs/" + definition.Name, isRoot: false);
                }
            }

            foreach ((RefKeyword keyword, string path) in _references)
            {
                keyword.Resolved = Resolve(keyword.Target, path);
            }

            return _root;
        }

        private Node Resolve(string target, string path)
        {
            if (target == "#")
            {
                return _root;
            }

            const string Prefix = "#/$defs/";
            if (target.StartsWith(Prefix, StringComparison.Ordinal) && _definitions.TryGetValue(target[Prefix.Length..], out Node? definition))
            {
                return definition;
            }

            throw new InvalidOperationException($"{path}: $ref '{target}' does not point at '#' or an entry of the root $defs, the only references this validator resolves.");
        }

        private Node Subschema(JsonElement schema, string path)
        {
            var node = new Node();
            Fill(node, schema, path, isRoot: false);
            return node;
        }

        private void Fill(Node node, JsonElement schema, string path, bool isRoot)
        {
            switch (schema.ValueKind)
            {
                case JsonValueKind.True:
                    node.Constant = true;
                    return;
                case JsonValueKind.False:
                    node.Constant = false;
                    return;
                case JsonValueKind.Object:
                    break;
                default:
                    throw new InvalidOperationException($"{path}: a schema must be an object or a boolean.");
            }

            var keywords = new List<Keyword>();
            PropertiesKeyword? properties = null;
            JsonElement? additional = null;
            int additionalAt = -1;
            foreach (JsonProperty property in schema.EnumerateObject())
            {
                string name = property.Name;
                JsonElement value = property.Value;
                string at = path + "/" + name;
                if (Array.IndexOf(s_annotations, name) >= 0 || (name == "$defs" && isRoot))
                {
                    continue;
                }

                switch (name)
                {
                    case "type":
                        keywords.Add(new TypeKeyword(ReadType(value, at)));
                        break;
                    case "required":
                        keywords.Add(new RequiredKeyword([.. Elements(value, at).Select(e => String(e, at))]));
                        break;
                    case "properties":
                        properties = new PropertiesKeyword([.. Members(value, at).Select(m => new PropertySchema(m.Name, Subschema(m.Value, at + "/" + m.Name)))]);
                        keywords.Add(properties);
                        break;
                    case "additionalProperties":
                        additional = value;
                        additionalAt = keywords.Count;
                        break;
                    case "items":
                        keywords.Add(new ItemsKeyword(Subschema(value, at)));
                        break;
                    case "$ref":
                        var reference = new RefKeyword(String(value, at));
                        _references.Add((reference, at));
                        keywords.Add(reference);
                        break;
                    case "enum":
                        keywords.Add(new EnumKeyword([.. Elements(value, at).Select(e => e.Clone())]));
                        break;
                    case "const":
                        keywords.Add(new ConstKeyword(value.Clone()));
                        break;
                    case "pattern":
                        keywords.Add(new PatternKeyword(new Regex(String(value, at), RegexOptions.ECMAScript, RegexLimits.Timeout)));
                        break;
                    case "minLength" or "maxLength":
                        keywords.Add(new LengthKeyword(Count(value, at), name == "maxLength"));
                        break;
                    case "minItems" or "maxItems":
                        keywords.Add(new ItemCountKeyword(Count(value, at), name == "maxItems"));
                        break;
                    case "minimum" or "maximum" or "exclusiveMinimum" or "exclusiveMaximum":
                        keywords.Add(new BoundKeyword(
                            name switch { "minimum" => Bound.Minimum, "maximum" => Bound.Maximum, "exclusiveMinimum" => Bound.ExclusiveMinimum, _ => Bound.ExclusiveMaximum },
                            ReadLimit(value, at)));
                        break;
                    case "format":
                        keywords.Add(String(value, at) == "date-time"
                            ? new DateTimeFormatKeyword()
                            : throw new NotSupportedException($"{at}: format '{value.GetString()}' is not implemented; only 'date-time' is."));
                        break;
                    default:
                        throw new NotSupportedException($"{at}: the keyword '{name}' is not implemented by SchemaValidator (docs/adr/0004-document-schema-validator.md). Implement it, with corpus and test-suite cases, before using it in a schema.");
                }
            }

            if (additional is { } schemaValue)
            {
                // additionalProperties is told what `properties` declared, so it is applied after it whatever order the schema writes them in.
                var keyword = new AdditionalPropertiesKeyword(Subschema(schemaValue, path + "/additionalProperties"), properties);
                int propertiesAt = properties is null ? -1 : keywords.IndexOf(properties);
                keywords.Insert(propertiesAt >= additionalAt ? propertiesAt + 1 : additionalAt, keyword);
            }

            node.Keywords = [.. keywords];
        }

        private static SchemaType ReadType(JsonElement value, string at)
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                throw new NotSupportedException($"{at}: only a single type name is implemented, not a list of types.");
            }

            return value.GetString() switch
            {
                "null" => SchemaType.Null,
                "boolean" => SchemaType.Boolean,
                "object" => SchemaType.Object,
                "array" => SchemaType.Array,
                "number" => SchemaType.Number,
                "integer" => SchemaType.Integer,
                "string" => SchemaType.String,
                var other => throw new InvalidOperationException($"{at}: '{other}' is not a JSON Schema type."),
            };
        }

        private static string String(JsonElement value, string at)
        {
            return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidOperationException($"{at}: expected a string.");
        }

        private static IEnumerable<JsonElement> Elements(JsonElement value, string at)
        {
            return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : throw new InvalidOperationException($"{at}: expected an array.");
        }

        private static IEnumerable<JsonProperty> Members(JsonElement value, string at)
        {
            return value.ValueKind == JsonValueKind.Object ? value.EnumerateObject() : throw new InvalidOperationException($"{at}: expected an object.");
        }

        private static int Count(JsonElement value, string at)
        {
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int count) && count >= 0 ? count : throw new InvalidOperationException($"{at}: expected a non-negative integer.");
        }

        private static JsonNumberText.Limit ReadLimit(JsonElement value, string at)
        {
            return value.ValueKind == JsonValueKind.Number ? JsonNumberText.ReadLimit(value) : throw new InvalidOperationException($"{at}: expected a number.");
        }
    }
}

/// <summary>Equality of JSON values as <c>enum</c> and <c>const</c> use it: structural, objects regardless of member order, numbers by value.</summary>
internal static class SchemaJson
{
    public static bool Equal(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind)
        {
            return false;
        }

        switch (a.ValueKind)
        {
            case JsonValueKind.String:
                try
                {
                    return a.ValueEquals(b.GetString()!);
                }
                catch (InvalidOperationException)
                {
                    return false; // a string with a lone surrogate equals nothing
                }

            case JsonValueKind.Number:
                return JsonNumberText.SameValue(a, b);
            case JsonValueKind.Array:
                return a.GetArrayLength() == b.GetArrayLength() && a.EnumerateArray().Zip(b.EnumerateArray(), Equal).All(same => same);
            case JsonValueKind.Object:
                {
                    List<JsonProperty> left = [.. a.EnumerateObject()];
                    List<JsonProperty> right = [.. b.EnumerateObject()];
                    return left.Count == right.Count && left.All(p => right.Any(q => JsonMembers.Is(q, JsonMembers.Name(p)) && Equal(p.Value, q.Value)));
                }

            default:
                return true;
        }
    }
}

/// <summary>
/// Reading the members of a JSON object when a name may not be text: JSON allows <c>"\ud800"</c>, which no UTF-16 string can hold, and the framework's
/// name accessors throw for it. A name like that is shown as it was written and equals no other name.
/// </summary>
internal static class JsonMembers
{
    public static string Name(JsonProperty member)
    {
        try
        {
            return member.Name;
        }
        catch (InvalidOperationException)
        {
            return Encoding.UTF8.GetString(JsonMarshal.GetRawUtf8PropertyName(member));
        }
    }

    public static bool Is(JsonProperty member, string name)
    {
        try
        {
            return member.NameEquals(name);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The first member with this name: the one the schema checks when a document names a member twice.</summary>
    public static bool TryGetFirst(JsonElement instance, string name, out JsonElement value)
    {
        foreach (JsonProperty member in instance.EnumerateObject())
        {
            if (Is(member, name))
            {
                value = member.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    public static bool Has(JsonElement instance, string name)
    {
        return TryGetFirst(instance, name, out _);
    }
}
