// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SysBox.Abstractions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace SysBox.Rules;

public sealed class RuleValidator
{
    public const int MaxBytes = 65536;
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull, WriteIndented = true };
    private static readonly JsonObject Schema = JsonNode.Parse(Resource("memory-rules.schema.json"))!.AsObject();
    public static string DefaultYaml => Resource("default.yaml");
    private static string Resource(string name)
    {
        using var stream = typeof(RuleValidator).Assembly.GetManifestResourceStream("SysBox.Rules." + name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    public RuleValidation Validate(string yaml)
    {
        var issues = new List<RuleIssue>();
        if (yaml is null || Encoding.UTF8.GetByteCount(yaml) > MaxBytes)
            return new(null, [new("INPUT_SIZE", "$", "YAML 必须不超过 64 KiB。")]);
        try
        {
            var parser = new Parser(new StringReader(yaml));
            var depth = 0;
            var count = 0;
            while (parser.MoveNext())
            {
                var e = parser.Current!;
                if (++count > 4096) throw new RuleException("YAML_LIMIT", "节点超过限制。");
                if (e is AnchorAlias || e is NodeEvent n && (!n.Anchor.IsEmpty || !n.Tag.IsEmpty))
                    throw new RuleException("YAML_UNSAFE", "禁止别名、锚点和显式标签。");
                if (e is MappingStart or SequenceStart && ++depth > 12) throw new RuleException("YAML_DEPTH", "嵌套深度超过 12。");
                if (e is MappingEnd or SequenceEnd) depth--;
            }
            parser = new Parser(new StringReader(yaml));
            parser.Consume<StreamStart>();
            parser.Consume<DocumentStart>();
            var node = ReadNode(parser);
            parser.Consume<DocumentEnd>();
            parser.Consume<StreamEnd>();
            node = JsonNode.Parse(node?.ToJsonString() ?? "null");
            Check(node, Schema, "$", issues);
            if (issues.Count > 0) return new(null, issues);
            var rule = node!.Deserialize<MemoryRule>(JsonOptions)!;
            void Error(string path, string message) => issues.Add(new("RULE_SEMANTIC", path, message));
            if ((rule.Conditions.All is null) == (rule.Conditions.Any is null)) Error("$.conditions", "必须且只能设置 all 或 any。");
            foreach (var c in rule.Conditions.All ?? rule.Conditions.Any ?? [])
                if (c.HoldSeconds < rule.Sampling.IntervalSeconds) Error("$.conditions", "持续时间必须至少为一个采样周期。");
            if (rule.Sampling.KeepHistoryMinutes * 60 < (rule.Conditions.All ?? rule.Conditions.Any ?? []).Select(c=>c.HoldSeconds).DefaultIfEmpty(0).Max())
                Error("$.sampling.keep_history_minutes", "历史窗口必须覆盖持续时间。");
            foreach (var (a, i) in rule.Actions.Select((a,i) => (a,i)))
            {
                var path = $"$.actions[{i}]";
                if (a.Type == "notify" && (a.Level is null || a.Message is null || a.Selector is not null)) Error(path, "通知必须有 level/message，不能有 selector。");
                if (a.Type == "trim_working_set" && (a.Enabled || a.Selector is null || a.Level is not null || a.Message is not null))
                    Error(path, "M3 只允许禁用的修剪动作，并要求完整 selector；不执行清理。");
            }
            return new(issues.Count == 0 ? rule : null, issues);
        }
        catch (RuleException e) { return new(null, [new(e.Code, "$", e.Message)]); }
        catch (YamlException e) { return new(null, [new("YAML_SYNTAX", $"line:{e.Start.Line + 1}:column:{e.Start.Column + 1}", "YAML 语法错误。")]); }
        catch (Exception e) when (e is InvalidOperationException or JsonException or FormatException or OverflowException)
        { return new(null, [new("RULE_TYPE", "$", "结构或字段类型不正确。")]); }
    }
    private static JsonNode? ReadNode(IParser p)
    {
        if (p.TryConsume<MappingStart>(out _))
        {
            var result = new JsonObject();
            while (!p.Accept<MappingEnd>(out _))
            {
                var key = p.Consume<Scalar>().Value;
                if (result.ContainsKey(key)) throw new RuleException("YAML_DUPLICATE", "重复字段：" + key);
                result.Add(key, ReadNode(p));
            }
            p.Consume<MappingEnd>(); return result;
        }
        if (p.TryConsume<SequenceStart>(out _))
        {
            var result = new JsonArray();
            while (!p.Accept<SequenceEnd>(out _)) result.Add(ReadNode(p));
            p.Consume<SequenceEnd>(); return result;
        }
        var scalar = p.Consume<Scalar>();
        if (scalar.Style == ScalarStyle.Plain)
        {
            if (scalar.Value is "true" or "false") return JsonValue.Create(scalar.Value == "true");
            if (scalar.Value is "null" or "~" or "") return null;
            if (long.TryParse(scalar.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)) return JsonValue.Create(integer);
            if (double.TryParse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)) return JsonValue.Create(number);
        }
        return JsonValue.Create(scalar.Value);
    }
    private static void Check(JsonNode? node, JsonObject schema, string path, List<RuleIssue> issues)
    {
        void Error(string text) => issues.Add(new("RULE_SCHEMA", path, text));
        var type = schema["type"]!.GetValue<string>();
        var kind = node?.GetValueKind();
        var valid = type switch
        {
            "object" => node is JsonObject, "array" => node is JsonArray,
            "string" => kind == JsonValueKind.String, "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
            "number" => kind == JsonValueKind.Number,
            "integer" => kind == JsonValueKind.Number && double.IsInteger(node!.GetValue<double>()), _ => false
        };
        if (!valid) { Error("字段类型必须为 " + type); return; }
        if (schema.TryGetPropertyValue("const", out var constant) && !JsonNode.DeepEquals(node, constant)) Error("该安全字段必须为 " + constant);
        if (schema["enum"] is JsonArray values && !values.Any(v => JsonNode.DeepEquals(v,node))) Error("不支持的枚举值。");
        if (node is JsonObject map)
        {
            var properties = schema["properties"]!.AsObject();
            foreach (var required in schema["required"]!.AsArray())
                if (!map.ContainsKey(required!.GetValue<string>())) issues.Add(new("RULE_REQUIRED", path + "." + required.GetValue<string>(), "缺少必填字段。"));
            foreach (var (key,value) in map)
                if (properties[key] is JsonObject child) Check(value,child,path + "." + key,issues);
                else issues.Add(new("RULE_UNKNOWN",path + "." + key,"未知字段。"));
        }
        else if (node is JsonArray array)
        {
            if (array.Count < schema["minItems"]!.GetValue<int>() || array.Count > schema["maxItems"]!.GetValue<int>()) Error("列表长度越界。");
            for (var i=0;i<array.Count;i++) Check(array[i],schema["items"]!.AsObject(),$"{path}[{i}]",issues);
        }
        else if (type == "string")
        {
            var text = node!.GetValue<string>();
            if (text.Length < schema["minLength"]!.GetValue<int>() || text.Length > schema["maxLength"]!.GetValue<int>()) Error("文本长度越界。");
            if (schema["pattern"] is { } pattern && !Regex.IsMatch(text,pattern.GetValue<string>(),RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100))) Error("文本格式无效。");
        }
        else if (type is "number" or "integer")
        {
            var value = node!.GetValue<double>();
            if (schema["minimum"] is {} min && value < min.GetValue<double>() || schema["maximum"] is {} max && value > max.GetValue<double>()) Error("数值越界。");
        }
    }
}
