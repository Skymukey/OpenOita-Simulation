using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenOita.Contracts;

namespace OpenOita.Data
{
    internal sealed class ConfigurationException : Exception
    {
        internal readonly WorldResult Result;
        internal ConfigurationException(string file, string path, string message,
            WorldErrorCode code = WorldErrorCode.InvalidConfig, string stage = "配置校验") : base(message)
        {
            Result = WorldResult.Failure(code, new WorldDiagnostic(stage, path, message, file));
        }
    }

    internal static class StrictJson
    {
        internal static JObject Parse(string text, string file)
        {
            if (text == null) throw new ConfigurationException(file, "$", "输入文本为空。");
            new Syntax(text, file).Validate();
            try
            {
                // 不进行对象类型反序列化；所有输入仅作为 JSON 值处理。
                using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, MaxDepth = 32 })
                {
                    return JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                }
            }
            catch (JsonException ex)
            {
                string path = ex is JsonReaderException error ? error.Path : "$";
                throw new ConfigurationException(file, path, ex.Message, stage: "JSON解析");
            }
        }

        internal static JObject Object(JToken token, string file, params string[] fields)
        {
            if (!(token is JObject value)) Fail(token, file, "必须是对象。");
            var result = (JObject)token;
            foreach (var property in result.Properties())
                if (System.Array.IndexOf(fields, property.Name) < 0) Fail(property, file, "未知字段或字段大小写错误。");
            foreach (string field in fields)
                if (result[field] == null) throw new ConfigurationException(file, Path(result) + "." + field, "缺少必需字段。");
            return result;
        }

        internal static JArray Array(JToken token, string file, int min, int max)
        {
            if (!(token is JArray result)) Fail(token, file, "必须是数组。");
            var value = (JArray)token;
            if (value.Count < min || value.Count > max) Fail(token, file, "数组长度超出范围。");
            return value;
        }

        internal static string String(JToken token, string file)
        {
            if (token.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)token)) Fail(token, file, "必须是非空白字符串。");
            return (string)token;
        }

        internal static long Integer(JToken token, string file, long min, long max)
        {
            if (token.Type != JTokenType.Integer || !long.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
                Fail(token, file, "必须是范围内的整数，不接受字符串或数值溢出。");
            long number = long.Parse(token.ToString(), CultureInfo.InvariantCulture);
            if (number < min || number > max) Fail(token, file, $"整数必须在 {min}–{max} 范围内。");
            return number;
        }

        internal static float Number(JToken token, string file, bool positive)
        {
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float) Fail(token, file, "必须是数值。");
            string representation = Convert.ToString(((JValue)token).Value, CultureInfo.InvariantCulture);
            if (!double.TryParse(representation, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                || double.IsNaN(number) || double.IsInfinity(number) || Math.Abs(number) > float.MaxValue)
                Fail(token, file, "数值非有限或超出运行时浮点范围。");
            float value = (float)number;
            if (positive && value <= 0) Fail(token, file, "必须是可表示的有限正数。");
            return value;
        }

        internal static void Version(JToken token, string file)
        {
            long version = Integer(token, file, 0, int.MaxValue);
            if (version != 1) Fail(token, file, "仅支持 schemaVersion=1。", WorldErrorCode.UnsupportedVersion);
        }

        internal static string Path(JToken token) => string.IsNullOrEmpty(token?.Path) ? "$" : token.Path;
        internal static void Fail(JToken token, string file, string message, WorldErrorCode code = WorldErrorCode.InvalidConfig)
            => throw new ConfigurationException(file, Path(token), message, code);

        // Newtonsoft 默认允许注释、单引号和尾逗号；在加载 DOM 前显式拒绝这些非 JSON 语法。
        private sealed class Syntax
        {
            private readonly string _text;
            private readonly string _file;
            private int _index;
            internal Syntax(string text, string file) { _text = text; _file = file; }
            internal void Validate()
            {
                White();
                if (Peek() != '{') Error("$", "根节点必须是对象。");
                Value("$", 0); White();
                if (_index != _text.Length) Error("$", "存在尾随内容。");
            }
            private char Peek() => _index < _text.Length ? _text[_index] : '\0';
            private void White() { while (Peek() == ' ' || Peek() == '\t' || Peek() == '\r' || Peek() == '\n') _index++; }
            private void Expect(char c, string path) { if (Peek() != c) Error(path, $"预期字符 {c}。"); _index++; White(); }
            private void Error(string path, string message) => throw new ConfigurationException(_file, path, $"{message} 字符位置 {_index}。", stage: "JSON语法");
            private void Value(string path, int depth)
            {
                if (depth > 32) Error(path, "嵌套深度超过32。");
                White(); char c = Peek();
                if (c == '{')
                {
                    _index++; White();
                    if (Peek() == '}') { _index++; return; }
                    while (true)
                    {
                        int start = _index; Quoted(path);
                        string name = JsonConvert.DeserializeObject<string>(_text.Substring(start, _index - start),
                            new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None });
                        White(); Expect(':', path + "." + name); Value(path + "." + name, depth + 1); White();
                        if (Peek() == '}') { _index++; return; }
                        Expect(',', path);
                    }
                }
                else if (c == '[')
                {
                    _index++; White(); int item = 0;
                    if (Peek() == ']') { _index++; return; }
                    while (true)
                    {
                        Value(path + "[" + item++ + "]", depth + 1); White();
                        if (Peek() == ']') { _index++; return; }
                        Expect(',', path);
                    }
                }
                else if (c == '"') Quoted(path);
                else if (c == 't') Literal("true", path);
                else if (c == 'f') Literal("false", path);
                else if (c == 'n') Literal("null", path);
                else
                {
                    if (c == '-') _index++;
                    if (Peek() == '0') _index++;
                    else { if (Peek() < '1' || Peek() > '9') Error(path, "非法 JSON 值。"); Digits(); }
                    if (Peek() == '.') { _index++; RequiredDigits(path); }
                    if (Peek() == 'e' || Peek() == 'E') { _index++; if (Peek() == '+' || Peek() == '-') _index++; RequiredDigits(path); }
                }
            }
            private void Digits() { while (Peek() >= '0' && Peek() <= '9') _index++; }
            private void RequiredDigits(string path) { if (Peek() < '0' || Peek() > '9') Error(path, "缺少数字。"); Digits(); }
            private void Literal(string literal, string path)
            {
                foreach (char c in literal) { if (Peek() != c) Error(path, "非法 JSON 值。"); _index++; }
            }
            private void Quoted(string path)
            {
                if (Peek() != '"') Error(path, "字符串必须使用双引号。");
                _index++;
                while (Peek() != '"')
                {
                    char c = Peek(); if (c < 32) Error(path, "字符串未结束或含控制字符。"); _index++;
                    if (c != '\\') continue;
                    char escape = Peek(); _index++;
                    if (escape == 'u')
                    {
                        for (int i = 0; i < 4; i++) { char hex = Peek(); if (!Uri.IsHexDigit(hex)) Error(path, "非法 Unicode 转义。"); _index++; }
                    }
                    else if ("\"\\/bfnrt".IndexOf(escape) < 0) Error(path, "非法字符串转义。");
                }
                _index++;
            }
        }
    }
}
