using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using System.Diagnostics;

namespace GHIElectronics.TinyCLR.Data.Json
{
    // The protocol mantra: Be strict in what you emit, and generous in what you accept.

    public delegate object InstanceFactory(string instancePath, JToken token, Type baseType, string fieldName, int length);

    public static class JsonConverter
    {
        private enum TokenType
        {
            LBrace, RBrace, LArray, RArray, Colon, Comma, String, Number, Date, Error,
            True, False, Null, End
        }

        private struct LexToken
        {
            public TokenType TType;
            public string TValue;
        }

        public class SerializationCtx
        {
            public SerializationCtx(JsonSerializationOptions options)
            {
                this.options = options;
            }

            public readonly JsonSerializationOptions options;
            public int IndentLevel;
        }

        public static SerializationCtx SerializationContext = null;
        public static object SyncObj = new object();

        public static JToken Serialize(object oSource, JsonSerializerSettings settings = null)
        {
            if (settings == null)
            {
                settings = new JsonSerializerSettings();
            }

            var type = oSource.GetType();
            if (type.IsArray)
                return JArray.Serialize(type, oSource, settings);
            else
                return JObject.Serialize(type, oSource, settings);
        }

        public static object DeserializeObject(string sourceString, Type type, InstanceFactory factory = null)
        {
            var dserResult = Deserialize(sourceString);
            return PopulateObject(dserResult, type, "/", factory);
        }

        public static object DeserializeObject(Stream stream, Type type, InstanceFactory factory = null)
        {
            var dserResult = Deserialize(stream);
            return PopulateObject(dserResult, type, "/", factory);
        }

        public static object DeserializeObject(StreamReader sr, Type type, InstanceFactory factory = null)
        {
            var dserResult = Deserialize(sr);
            return PopulateObject(dserResult, type, "/", factory);
        }

        private static object DefaultInstanceFactory(string instancePath, JToken token, Type baseType, string fieldName, int length)
        {
            object instance = null;

            if (token is JObject jobj)
            {
                if (jobj.Contains("$type"))
                {
                    var typeName = jobj.Contains("$type") ? ((JValue)jobj["$type"].Value).Value.ToString() : string.Empty;
                    if (!string.IsNullOrEmpty(typeName))
                    {
                        var assembly = baseType.Assembly;
                        foreach (var type in assembly.GetTypes())
                        {
                            if (type.Name == typeName)
                            {
                                instance = AppDomain.CurrentDomain.CreateInstanceAndUnwrap(assembly.FullName, type.FullName);
                                break;
                            }
                        }
                    }
                }
            }
            else if (token is JArray jarr)
            {
                var result = Array.CreateInstance(baseType, jarr.Length);
                for (int i = 0; i < jarr.Items.Length; i++)
                {
                    if (jarr.Items[i] is JObject item)
                    {
                        ((IList)result)[i] = DefaultInstanceFactory(instancePath, item, baseType, fieldName, length);
                        break;
                    }
                }
                instance = result;
            }

            if (baseType != null && instance == null)
                instance = AppDomain.CurrentDomain.CreateInstanceAndUnwrap(baseType.Assembly.FullName, baseType.FullName);

            return instance;
        }

        private static object PopulateObject(JToken root, Type type, string path, InstanceFactory factory)
        {
            if (root is JObject)
            {
                object instance = null;
                if (factory != null)
                {
                    instance = factory(path, root, type, null, -1);
                    if (instance != null)
                    {
                        type = instance.GetType();
                    }
                }

                instance = DefaultInstanceFactory(path, root, type, null, -1);

                if (instance == null)
                    throw new Exception("failed to create target instance");

                var resolvedType = instance.GetType();

                var jobj = (JObject)root;
                foreach (var item in jobj.Members)
                {
                    var prop = (JProperty)item;
                    MethodInfo method = null;
                    Type itemType = null;
                    var field = resolvedType.GetField(prop.Name);
                    if (field != null)
                    {
                        itemType = field.FieldType;
                    }
                    else
                    {
                        method = resolvedType.GetMethod("get_" + prop.Name);
                        if (method == null)
                        {
                            continue;
                        }
                        itemType = method.ReturnType;
                        method = resolvedType.GetMethod("set_" + prop.Name);
                    }

                    if (itemType != null)
                    {
                        if (prop.Value is JObject)
                        {
                            var childpath = path;
                            if (childpath[childpath.Length - 1] != '/')
                                childpath = childpath + '/' + prop.Name;
                            else
                                childpath += prop.Name;
                            var child = PopulateObject(prop.Value, itemType, childpath, factory);
                            if (field != null)
                                field.SetValue(instance, child);
                            else
                                method.Invoke(instance, new object[] { child });
                        }
                        else if (prop.Value is JValue)
                        {
                            var scalarValue = ConvertScalar((JValue)prop.Value, itemType);
                            if (field != null)
                            {
                                field.SetValue(instance, scalarValue);
                            }
                            else
                            {
                                method.Invoke(instance, new object[] { scalarValue });
                            }
                        }
                        else if (prop.Value is JArray)
                        {
                            var jarray = (JArray)prop.Value;
                            var list = new ArrayList();
                            var array = (Array)DefaultInstanceFactory(path, prop.Value, itemType.GetElementType(), prop.Name, jarray.Length);
                            if (array == null)
                                instance = DefaultInstanceFactory(path, prop.Value, itemType.GetElementType(), prop.Name, jarray.Length);
                            if (array != null)
                            {
                                var elemType = array.GetType().GetElementType();
                                foreach (var elem in jarray.Items)
                                {
                                    if (elem is JValue)
                                    {
                                        var scalarValue = ConvertScalar((JValue)elem, elemType);
                                        list.Add(scalarValue);
                                    }
                                    else
                                    {
                                        var arrayObj = PopulateObject(elem, type, path + "/" + prop.Name, factory);
                                        list.Add(arrayObj);
                                    }
                                }
                                list.CopyTo(array);
                                if (field != null)
                                    field.SetValue(instance, array);
                                else
                                    method.Invoke(instance, new object[] { array });
                            }
                        }
                    }
                }
                return instance;
            }
            else if (root is JArray)
            {
                var elemType = type.GetElementType();
                if (elemType == null && factory == null)
                    throw new NotSupportedException("You must provide an instance factory if you want to populate objects that have arrays in them");

                var jarray = (JArray)root;
                var list = new ArrayList();

                Array array;
                if (elemType != null)
                    array = Array.CreateInstance(elemType, jarray.Length);
                else
                {
                    array = (Array)factory(path, root, null, null, jarray.Length);
                    if (array == null)
                    {
                        array = (Array)DefaultInstanceFactory(path, root, null, null, jarray.Length);
                    }
                }

                foreach (var item in jarray.Items)
                {
                    if (item is JValue)
                        list.Add(((JValue)item).Value);
                    else
                    {
                        var arrayObj = PopulateObject(item, null, path, factory);
                        list.Add(arrayObj);
                    }
                }
                list.CopyTo(array);
                return array;
            }
            return null;
        }

        private static object ConvertScalar(JValue value, Type targetType)
        {
            object result = null;

            // This is a matrix of conversions from the source (value) type to targetType.
            // This is a bit long because the available Convert.ToXXXX functions are more
            // limited than in .net, so we have to do the scalar conversions ourselves.
            if (value.Value == null)
            {
                result = null;
            }
            else if (targetType == value.Value.GetType())
            {
                result = value.Value;
            }
            else if (targetType == typeof(DateTime))
            {
                DateTime dt;
                var sdt = value.Value.ToString();
                if (sdt.Contains("Date("))
                    dt = DateTimeExtensions.FromASPNetAjax(sdt);
                else
                    dt = DateTimeExtensions.FromIso8601(sdt);

                result = dt;
            }
            else
            {
                if (targetType.IsEnum)
                {
                    // This is broken in TinyCLR
                    var baseType = Enum.GetUnderlyingType(targetType);

                    // explicit unboxing, because unboxing with casting fails
                    var source = value.Value;
                    ulong ulSource;
                    var sourceType = value.Value.GetType();
                    if (sourceType == typeof(ulong))
                    {
                        ulSource = (ulong)source;
                    }
                    else if (sourceType == typeof(int))
                    {
                        ulSource = (ulong)(int)source;
                    }
                    else if (sourceType == typeof(long))
                    {
                        ulSource = (ulong)(long)source;
                    }
                    else if (sourceType == typeof(short))
                    {
                        ulSource = (ulong)(ushort)source;
                    }
                    else if (sourceType == typeof(byte))
                    {
                        ulSource = (ulong)(byte)source;
                    }
                    else if (sourceType == typeof(sbyte))
                    {
                        ulSource = (ulong)(sbyte)source;
                    }
                    else
                    {
                        throw new Exception("Could not encode the source type for use in an enum");
                    }

                    if (baseType == typeof(int))
                    {
                        result = (int)ulSource;
                    }
                    else if (baseType == typeof(long))
                    {
                        result = (long)ulSource;
                    }
                    else if (baseType == typeof(ulong))
                    {
                        result = ulSource;
                    }
                    else if (baseType == typeof(short))
                    {
                        result = (short)ulSource;
                    }
                    else if (baseType == typeof(ushort))
                    {
                        result = (ushort)ulSource;
                    }
                    else if (baseType == typeof(byte))
                    {
                        result = (byte)ulSource;
                    }
                    else if (baseType == typeof(sbyte))
                    {
                        result = (sbyte)ulSource;
                    }
                }
                else if (value.Value.GetType() == typeof(ulong))
                {
                    var source = (ulong)value.Value;
                    if (targetType == typeof(float))
                    {
                        result = (float)source;
                    }
                    else if (targetType == typeof(double))
                    {
                        result = (double)source;
                    }
                    else if (targetType == typeof(long))
                    {
                        result = (long)source;
                    }
                    else if (targetType == typeof(short))
                    {
                        result = (short)source;
                    }
                    else if (targetType == typeof(ushort))
                    {
                        result = (ushort)source;
                    }
                    else if (targetType == typeof(int))
                    {
                        result = (int)source;
                    }
                    else if (targetType == typeof(uint))
                    {
                        result = (uint)source;
                    }
                    else
                    {
                        result = value.Value;
                    }
                }
                else if (value.Value.GetType() == typeof(long))
                {
                    var source = (long)value.Value;
                    if (targetType == typeof(float))
                    {
                        result = (float)source;
                    }
                    else if (targetType == typeof(double))
                    {
                        result = (double)source;
                    }
                    else if (targetType == typeof(ulong))
                    {
                        result = (ulong)source;
                    }
                    else if (targetType == typeof(short))
                    {
                        result = (short)source;
                    }
                    else if (targetType == typeof(ushort))
                    {
                        result = (ushort)source;
                    }
                    else if (targetType == typeof(int))
                    {
                        result = (int)source;
                    }
                    else if (targetType == typeof(uint))
                    {
                        result = (uint)source;
                    }
                    else
                    {
                        result = value.Value;
                    }
                }
                else if (value.Value.GetType() == typeof(double))
                {
                    var source = (double)value.Value;
                    if (targetType == typeof(float))
                    {
                        result = (float)source;
                    }
                    else if (targetType == typeof(long))
                    {
                        result = (long)source;
                    }
                    else if (targetType == typeof(ulong))
                    {
                        result = (ulong)source;
                    }
                    else if (targetType == typeof(short))
                    {
                        result = (short)source;
                    }
                    else if (targetType == typeof(ushort))
                    {
                        result = (ushort)source;
                    }
                    else if (targetType == typeof(int))
                    {
                        result = (int)source;
                    }
                    else if (targetType == typeof(uint))
                    {
                        result = (uint)source;
                    }
                    else
                    {
                        result = value.Value;
                    }
                }
            }

            return result;
        }

        private static object ConvertScalar(JProperty prop, Type targetType) => ConvertScalar((JValue)prop.Value, targetType);

        public static JToken Deserialize(string sourceString)
        {
            var data = Encoding.UTF8.GetBytes(sourceString);
            var mem = new MemoryStream(data);
            mem.Seek(0, SeekOrigin.Begin);
            return Deserialize(new StreamReader(mem));
        }

        public static JToken Deserialize(Stream sourceStream)
        {
            return Deserialize(new StreamReader(sourceStream));
        }

        public static JToken Deserialize(StreamReader sourceReader)
        {
            JToken result = null;
            LexToken token;
            token = GetNextToken(sourceReader);
            switch (token.TType)
            {
                case TokenType.LBrace:
                    result = ParseObject(sourceReader, ref token);
                    if (token.TType == TokenType.RBrace)
                        token = GetNextToken(sourceReader);
                    else if (token.TType != TokenType.End && token.TType != TokenType.Error)
                        throw new Exception("unexpected content after end of object");
                    break;
                case TokenType.LArray:
                    result = ParseArray(sourceReader, ref token);
                    if (token.TType == TokenType.RArray)
                        token = GetNextToken(sourceReader);
                    else if (token.TType != TokenType.End && token.TType != TokenType.Error)
                        throw new Exception("unexpected content after end of array");
                    break;
                default:
                    throw new Exception("unexpected initial token in json parse");
            }
            if (token.TType != TokenType.End)
                throw new Exception("unexpected end token in json parse");
            else if (token.TType == TokenType.Error)
                throw new Exception("unexpected lexical token during json parse");
            return result;
        }

        public static JToken FromBson(byte[] buffer, InstanceFactory factory = null)
        {
            var offset = 0;
            var len = (Int32)SerializationUtilities.Unmarshall(buffer, ref offset, TypeCode.Int32);

            JToken dserResult = null;
            while (offset < buffer.Length - 1)
            {
                var bsonType = (BsonTypes)buffer[offset++];

                // eat the empty ename
                var idxNul = JToken.FindNul(buffer, offset);
                if (idxNul == -1)
                    throw new Exception("Missing ename terminator");
                var ename = JToken.ConvertToString(buffer, offset, idxNul - offset);
                offset = idxNul + 1;

                switch (bsonType)
                {
                    case BsonTypes.BsonDocument:
                        dserResult = JObject.FromBson(buffer, ref offset, factory);
                        break;
                    case BsonTypes.BsonArray:
                        dserResult = JArray.FromBson(buffer, ref offset, factory);
                        break;
                    default:
                        throw new Exception("unexpected top-level object type in bson");
                }
            }
            if (buffer[offset++] != 0)
                throw new Exception("bad format - missing trailing null on bson document");
            return dserResult;
        }

        public static object FromBson(byte[] buffer, Type resultType, InstanceFactory factory = null)
        {
            var jtoken = FromBson(buffer);
            return PopulateObject(jtoken, resultType, "/", factory);
        }

        private static JObject ParseObject(StreamReader sr, ref LexToken token)
        {
            Debug.Assert(token.TType == TokenType.LBrace);
            var result = new JObject();
            token = GetNextToken(sr);
            while (token.TType != TokenType.End && token.TType != TokenType.Error && token.TType != TokenType.RBrace)
            {
                if (token.TType != TokenType.String)
                    throw new Exception("expected label");
                var propName = token.TValue;

                token = GetNextToken(sr);
                if (token.TType != TokenType.Colon)
                    throw new Exception("expected colon");

                var value = ParseValue(sr, ref token);
                result.Add(propName, value);

                token = GetNextToken(sr);
                if (token.TType == TokenType.Comma)
                    token = GetNextToken(sr);

            };
            if (token.TType == TokenType.Error)
                throw new Exception("unexpected token in json object");
            else if (token.TType != TokenType.RBrace)
                throw new Exception("unterminated json object");
            return result;
        }

        private static JArray ParseArray(StreamReader sr, ref LexToken token)
        {
            Debug.Assert(token.TType == TokenType.LArray || token.TType == TokenType.RArray);
            ArrayList list = new ArrayList();
            while (token.TType != TokenType.End && token.TType != TokenType.Error && token.TType != TokenType.RArray)
            {
                var value = ParseValue(sr, ref token);
                if (value != null)
                {
                    list.Add(value);
                    token = GetNextToken(sr);
                    if (token.TType != TokenType.Comma && token.TType != TokenType.RArray)
                        throw new Exception("badly formed array");
                }
            };
            if (token.TType == TokenType.Error)
                throw new Exception("unexpected token in array");
            else if (token.TType != TokenType.RArray)
                throw new Exception("unterminated json array");
            var result = new JArray((JToken[])list.ToArray(typeof(JToken)));
            return result;
        }

        private static JToken ParseValue(StreamReader sr, ref LexToken token)
        {
            token = GetNextToken(sr);
            if (token.TType == TokenType.RArray)
            {
                // we were expecting a value in an array, and came across the end-of-array marker,
                //  so this is an empty array
                return null;
            }
            else if (token.TType == TokenType.String)
                return new JValue(token.TValue);
            else if (token.TType == TokenType.Number)
            {
                // If it looks like a double, use double as the underlying storage type
                if (token.TValue.Length > 0 && (token.TValue[0] == 'E' || token.TValue[0] == 'e'))
                {
                    // exponent with no leading mantissa
                    return new JValue(double.Parse("1" + token.TValue));
                }
                else if (token.TValue.IndexOfAny(new char[] { '.', 'e', 'E' }) != -1)
                {
                    // well-formed double, with or without E notation
                    return new JValue(double.Parse(token.TValue));
                }
                else
                {
                    // Otherwise, an int of some type
                    // Use Int64 as the underlying storage type. If you don't see a minus
                    // sign, then prefer UInt64 for maximum range.
                    if (token.TValue.IndexOf('-') != -1)
                        return new JValue(Int64.Parse(token.TValue));
                    else
                        return new JValue(UInt64.Parse(token.TValue));
                }
            }
            else if (token.TType == TokenType.True)
                return new JValue(true);
            else if (token.TType == TokenType.False)
                return new JValue(false);
            else if (token.TType == TokenType.Null)
                return new JValue(null);
            else if (token.TType == TokenType.Date)
            {
                throw new NotSupportedException("datetime parsing not supported");
            }
            else if (token.TType == TokenType.LBrace)
                return ParseObject(sr, ref token);
            else if (token.TType == TokenType.LArray)
                return ParseArray(sr, ref token);

            throw new Exception("invalid value found during json parse");
        }

        private static LexToken GetNextToken(StreamReader sourceReader)
#if DEBUG
        {
            // a diagnostic wrapper, used only in debug builds
            var result = GetNextTokenInternal(sourceReader);
            //Debug.WriteLine(result.TType.TokenTypeToString());
            //if (result.TType == TokenType.String || result.TType == TokenType.Number)
            //    Debug.WriteLine("    " + result.TValue);
            return result;
        }

        private static LexToken GetNextTokenInternal(StreamReader sourceReader)
#endif
        {
            StringBuilder sb = null;
            char openQuote = '\0';

            char ch = ' ';
            while (true) // EndOfStream doesn't seem to work for mem streams - it's always 'true'
            {
                ch = (char)sourceReader.Read();

                // Handle json escapes
                bool escaped = false;
                if (ch == '\\')
                {
                    escaped = true;
                    ch = (char)sourceReader.Read();
                    if (ch == (char)0xffff)
                        return EndToken(sb);
                    //TODO: replace with a mapping array? This switch is really incomplete.
                    switch (ch)
                    {
                        case '\'':
                            ch = '\'';
                            break;
                        case '"':
                            ch = '"';
                            break;
                        case 't':
                            ch = '\t';
                            break;
                        case 'r':
                            ch = '\r';
                            break;
                        case 'n':
                            ch = '\n';
                            break;
                        default:
                            throw new Exception("unsupported escape");
                    }
                }

                if (sb != null && (ch != openQuote || escaped))
                {
                    sb.Append(ch);
                }
                else if (IsNumberIntroChar(ch))
                {
                    sb = new StringBuilder();
                    while (IsNumberChar(ch))
                    {
                        sb.Append(ch);
                        // Don't consume chars that are not part of the number
                        ch = (char)sourceReader.Peek();
                        if (IsNumberChar(ch))
                            sourceReader.Read();

                        if (ch == (char)0xffff)
                            return EndToken(sb);
                    }
                    // Note that we don't claim that this is a well-formed number
                    return new LexToken() { TType = TokenType.Number, TValue = sb.ToString() };
                }
                else
                {
                    switch (ch)
                    {
                        case '{':
                            return new LexToken() { TType = TokenType.LBrace, TValue = null };
                        case '}':
                            return new LexToken() { TType = TokenType.RBrace, TValue = null };
                        case '[':
                            return new LexToken() { TType = TokenType.LArray, TValue = null };
                        case ']':
                            return new LexToken() { TType = TokenType.RArray, TValue = null };
                        case ':':
                            return new LexToken() { TType = TokenType.Colon, TValue = null };
                        case ',':
                            return new LexToken() { TType = TokenType.Comma, TValue = null };
                        case '"':
                        case '\'':
                            if (sb == null)
                            {
                                openQuote = ch;
                                sb = new StringBuilder();
                            }
                            else
                            {
                                // We're building a string and we hit a quote character.
                                // The ch must match openQuote, or otherwise we should have eaten it above as string content
                                Debug.Assert(ch == openQuote);
                                return new LexToken() { TType = TokenType.String, TValue = sb.ToString() };
                            }
                            break;
                        case ' ':
                        case '\t':
                        case '\r':
                        case '\n':
                            break; // whitespace - go around again
                        case (char)0xffff:
                            return EndToken(sb);
                        default:
                            // try to collect a token
                            switch (ch.ToLower())
                            {
                                case 't':
                                    Expect(sourceReader, 'r');
                                    Expect(sourceReader, 'u');
                                    Expect(sourceReader, 'e');
                                    return new LexToken() { TType = TokenType.True, TValue = null };
                                case 'f':
                                    Expect(sourceReader, 'a');
                                    Expect(sourceReader, 'l');
                                    Expect(sourceReader, 's');
                                    Expect(sourceReader, 'e');
                                    return new LexToken() { TType = TokenType.False, TValue = null };
                                case 'n':
                                    Expect(sourceReader, 'u');
                                    Expect(sourceReader, 'l');
                                    Expect(sourceReader, 'l');
                                    return new LexToken() { TType = TokenType.Null, TValue = null };
                                default:
                                    throw new Exception("unexpected character during json lexical parse");
                            }
                    }
                }
            }
        }

        private static void Expect(StreamReader sr, char ch)
        {
            if (((char)sr.Read()).ToLower() != ch)
                throw new Exception("unexpected character during json lexical parse");
        }

        private static bool IsValidTokenChar(char ch)
        {
            return (ch >= 'a' && ch <= 'z') ||
                   (ch >= 'A' && ch <= 'Z') ||
                   (ch >= '0' && ch <= '9');
        }

        private static LexToken EndToken(StringBuilder sb)
        {
            if (sb != null)
                return new LexToken() { TType = TokenType.Error, TValue = null };
            else
                return new LexToken() { TType = TokenType.End, TValue = null };
        }

        // Legal first characters for numbers
        private static bool IsNumberIntroChar(char ch)
        {
            return (ch == '-') || (ch == '+') || (ch == '.') || (ch >= '0' & ch <= '9') || (ch == 'e') || (ch == 'E');  // 'e' for exponential notation
        }

        // Legal chars for 2..n'th position of a number
        private static bool IsNumberChar(char ch)
        {
            return (ch == '-') || (ch == '+') || (ch == '.') || (ch == 'e') || (ch == 'E') || (ch >= '0' & ch <= '9');
        }

#if DEBUG
        private static string TokenTypeToString(this TokenType val)
        {
            switch (val)
            {
                case TokenType.Colon:
                    return "COLON";
                case TokenType.Comma:
                    return "COMMA";
                case TokenType.Date:
                    return "DATE";
                case TokenType.End:
                    return "END";
                case TokenType.Error:
                    return "ERROR";
                case TokenType.LArray:
                    return "LARRAY";
                case TokenType.LBrace:
                    return "LBRACE";
                case TokenType.Number:
                    return "NUMBER";
                case TokenType.RArray:
                    return "RARRAY";
                case TokenType.RBrace:
                    return "RBRACE";
                case TokenType.String:
                    return "STRING";
                case TokenType.Null:
                    return "NULL";
                case TokenType.True:
                    return "TRUE";
                case TokenType.False:
                    return "FALSE";
                default:
                    return "??unknown??";
            }
        }
#endif
    }
}
