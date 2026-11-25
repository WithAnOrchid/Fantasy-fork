using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Fantasy.ProtocolExportTool.Abstract;
using Fantasy.ProtocolExportTool.Models;
using Fantasy.ProtocolExportTool.Services;
// ReSharper disable PossibleMultipleEnumeration

namespace Fantasy.ProtocolExportTool.Generators;

public sealed class CSharpExporter(
    string protocolDirectory,
    string clientDirectory,
    string serverDirectory,
    OpCodeCacheSession opCodeCache,
    ProtocolExportType protocolExportType)
    : AProtocolExporter(protocolDirectory, clientDirectory, serverDirectory, opCodeCache, protocolExportType)
{
    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum",
        "event", "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto",
        "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace",
        "new", "null", "object", "operator", "out", "override", "params", "private", "protected", "public",
        "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
        "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked",
        "unsafe", "ushort", "using", "virtual", "void", "volatile", "while"
    };

    private static readonly HashSet<string> CSharpPrimitiveTypeKeywords = new(StringComparer.Ordinal)
    {
        "bool", "byte", "char", "decimal", "double", "float", "int", "long",
        "object", "sbyte", "short", "string", "uint", "ulong", "ushort"
    };

    public static string EscapeIdentifier(string identifier)
    {
        if (string.IsNullOrEmpty(identifier) || identifier[0] == '@')
        {
            return identifier;
        }

        return CSharpKeywords.Contains(identifier) ? $"@{identifier}" : identifier;
    }

    public static string EscapeTypeIdentifier(string typeName)
    {
        if (string.IsNullOrEmpty(typeName) || typeName[0] == '@' || CSharpPrimitiveTypeKeywords.Contains(typeName))
        {
            return typeName;
        }

        return EscapeIdentifier(typeName);
    }

    public static string ToCamelCaseIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var raw = name[0] == '@' ? name[1..] : name;
        if (raw.Length == 0)
        {
            return name;
        }

        var camelCase = raw.Length == 1
            ? char.ToLowerInvariant(raw[0]).ToString()
            : $"{char.ToLowerInvariant(raw[0])}{raw[1..]}";
        return EscapeIdentifier(camelCase);
    }
    protected override string GenerateRouteTypes(IReadOnlyDictionary<string, int> routeTypes)
    {
        if (routeTypes.Count == 0)
        {
            return string.Empty;
        }

        var routeTypeEnumerable = routeTypes.OrderBy(d => d.Value);

        var constants = string.Join(Environment.NewLine, routeTypeEnumerable
            .Select(x =>
            {
                var comment = x.Key.EndsWith("Route")
                    ? x.Key.Substring(0, x.Key.Length - "Route".Length)
                    : x.Key;
                return $"        public const int {EscapeIdentifier(x.Key)} = {x.Value}; // {comment}";
            }));
        
        var enumerable = string.Join(Environment.NewLine, 
            routeTypeEnumerable.Select(x => $"                yield return {EscapeIdentifier(x.Key)};"));

        return $$"""
                 using System.Collections.Generic;
                 namespace Fantasy
                 {
                     /// <summary>
                     /// 本代码有编辑器生成,请不要再这里进行编辑。
                     /// Route协议定义(需要定义1000以上、因为1000以内的框架预留)
                     /// </summary>
                     public static partial class RouteType
                     {
                 {{constants}}
                 
                         public static IEnumerable<int> RoamingTypes
                         {
                             get
                             {
                 {{enumerable}}
                             }
                         }
                     }
                 }
                 """;
    }

    protected override string GenerateRoamingTypes(IReadOnlyDictionary<string, int> roamingTypes)
    {
        if (roamingTypes.Count == 0)
        {
            return string.Empty;
        }

        var roamingTypeEnumerable = roamingTypes.OrderBy(d => d.Value);

        var constants = string.Join(Environment.NewLine, roamingTypeEnumerable
            .Select(x =>
            {
                var comment = x.Key.EndsWith("RoamingType")
                    ? x.Key.Substring(0, x.Key.Length - "RoamingType".Length)
                    : x.Key.EndsWith("Type")
                        ? x.Key.Substring(0, x.Key.Length - "Type".Length)
                        : x.Key;
                return $"        public const int {EscapeIdentifier(x.Key)} = {x.Value}; // {comment}";
            }));

        var enumerable = string.Join(Environment.NewLine, 
            roamingTypeEnumerable.Select(x => $"                yield return {EscapeIdentifier(x.Key)};"));

        return $$"""
                 using System.Collections.Generic;
                 namespace Fantasy
                 {
                     /// <summary>
                     /// 本代码有编辑器生成,请不要再这里进行编辑。
                     /// Roaming协议定义(需要定义10000以上、因为10000以内的框架预留)
                     /// </summary>
                     public static partial class RoamingType
                     {
                 {{constants}}
                 
                         public static IEnumerable<int> RoamingTypes
                         {
                             get
                             {
                 {{enumerable}}
                             }
                         }
                     }
                 }
                 """;
    }

    protected override string GenerateOuterOpcode(IReadOnlyList<OpcodeInfo> opcodeInfos)
    {
        if (opcodeInfos.Count == 0)
        {
            return string.Empty;
        }

        return $$"""
                 // ReSharper disable InconsistentNaming
                 namespace Fantasy
                 {
                     /// <summary>
                     /// 本代码有编辑器生成,请不要再这里进行编辑。
                     /// </summary>
                     public static partial class OuterOpcode
                     {
                 {{string.Join(Environment.NewLine, opcodeInfos
                     .Select(x => $"        public const uint {EscapeIdentifier(x.Name)} = {x.Code};"))}}
                     }
                 }
                 """;
    }

    protected override string GenerateInnerOpcode(IReadOnlyList<OpcodeInfo> opcodeInfos)
    {
        if (opcodeInfos.Count == 0)
        {
            return string.Empty;
        }

        return $$"""
                 // ReSharper disable InconsistentNaming
                 namespace Fantasy
                 {
                     /// <summary>
                     /// 本代码有编辑器生成,请不要再这里进行编辑。
                     /// </summary>
                     public static partial class InnerOpcode
                     {
                 {{string.Join(Environment.NewLine, opcodeInfos
                     .Select(x => $"        public const uint {EscapeIdentifier(x.Name)} = {x.Code};"))}}
                     }
                 }
                 """;
    }

    protected override string GenerateOuterMessages(IReadOnlySet<string> outerNamespaces, IReadOnlyDictionary<string, MessageDefinition> messageDefinitions)
    {
        return messageDefinitions.Count == 0 ? string.Empty : $$"""
                                                                using LightProto;
                                                                using System;
                                                                using MemoryPack;
                                                                using System.Collections.Generic;
                                                                using Fantasy;
                                                                using Fantasy.Pool;
                                                                using Fantasy.Network.Interface;
                                                                using Fantasy.Serialize;
                                                                {{GenerateNamespaces(outerNamespaces)}}
                                                                #pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
                                                                #pragma warning disable CS8618
                                                                // ReSharper disable InconsistentNaming
                                                                // ReSharper disable CollectionNeverUpdated.Global
                                                                // ReSharper disable RedundantTypeArgumentsOfMethod
                                                                // ReSharper disable PartialTypeWithSinglePart
                                                                // ReSharper disable UnusedAutoPropertyAccessor.Global
                                                                // ReSharper disable PreferConcreteValueOverDefault
                                                                // ReSharper disable RedundantNameQualifier
                                                                // ReSharper disable MemberCanBePrivate.Global
                                                                // ReSharper disable CheckNamespace
                                                                // ReSharper disable FieldCanBeMadeReadOnly.Global
                                                                // ReSharper disable RedundantUsingDirective
                                                                // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
                                                                namespace Fantasy
                                                                {
                                                                {{GenerateMessages("OuterOpcode", messageDefinitions)}}
                                                                }
                                                                """;
    }

    protected override string GenerateInnerMessages(IReadOnlySet<string> innerNamespaces, IReadOnlyDictionary<string, MessageDefinition> messageDefinitions)
    {
        return messageDefinitions.Count == 0 ? string.Empty : $$"""
                                                                using LightProto;
                                                                using MemoryPack;
                                                                using System;
                                                                using System.Collections.Generic;
                                                                using MongoDB.Bson.Serialization.Attributes;
                                                                using Fantasy;
                                                                using Fantasy.Pool;
                                                                using Fantasy.Network.Interface;
                                                                using Fantasy.Serialize;
                                                                {{GenerateNamespaces(innerNamespaces)}}
                                                                // ReSharper disable InconsistentNaming
                                                                // ReSharper disable CollectionNeverUpdated.Global
                                                                // ReSharper disable RedundantTypeArgumentsOfMethod
                                                                // ReSharper disable PartialTypeWithSinglePart
                                                                // ReSharper disable UnusedAutoPropertyAccessor.Global
                                                                // ReSharper disable PreferConcreteValueOverDefault
                                                                // ReSharper disable RedundantNameQualifier
                                                                // ReSharper disable MemberCanBePrivate.Global
                                                                // ReSharper disable CheckNamespace
                                                                // ReSharper disable FieldCanBeMadeReadOnly.Global
                                                                // ReSharper disable RedundantUsingDirective
                                                                // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
                                                                #pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
                                                                #pragma warning disable CS8618
                                                                namespace Fantasy
                                                                {
                                                                {{GenerateMessages("InnerOpcode", messageDefinitions)}}
                                                                }
                                                                """;
    }

    protected override string GenerateOuterMessageHelper(IReadOnlyDictionary<string, MessageDefinition> messageDefinitions)
    {
        if (messageDefinitions.Count == 0)
        {
            return string.Empty;
        }

        var helper = new StringBuilder();
        
        foreach (var (messageDefinitionName, messageDefinition) in messageDefinitions)
        {
            if (string.IsNullOrEmpty(messageDefinition.InterfaceType))
            {
                continue;
            }

            var escapedMessageName = EscapeIdentifier(messageDefinitionName);
            var escapedResponseType = EscapeIdentifier(messageDefinition.ResponseType);

            switch (messageDefinition.MessageType)
                {
                    case MessageType.Message:
                    case MessageType.RoamingMessage:
                    case MessageType.RouteTypeMessage:
                        {
                            helper.AppendLine("\t\t[MethodImpl(MethodImplOptions.AggressiveInlining)]");
                            helper.AppendLine($"\t\tpublic static void {escapedMessageName}(this Session session, {escapedMessageName} {messageDefinitionName}_message)");
                            helper.AppendLine("\t\t{");
                            helper.AppendLine($"\t\t\tsession.Send({messageDefinitionName}_message);");
                            helper.AppendLine("\t\t}");

                            if (messageDefinition.Fields.Count > 0)
                            {
                                var parameters = string.Join(", ",
                                    messageDefinition.Fields
                                    .Where(f => f.CustomTextLine == null) // 跳过自定义文本行
                                    .Select(f => $"{ConvertType(f)} {ToCamelCaseIdentifier(f.Name)}"));
                                helper.AppendLine("\t\t[MethodImpl(MethodImplOptions.AggressiveInlining)]");
                                helper.AppendLine($"\t\tpublic static void {escapedMessageName}(this Session session, {parameters})");
                                helper.AppendLine("\t\t{");
                                helper.AppendLine($"\t\t\tusing var {messageDefinitionName}_message = Fantasy.{escapedMessageName}.Create();");

                                foreach (var field in messageDefinition.Fields)
                                {
                                    if (field.CustomTextLine != null) // 跳过自定义文本行
                                        continue;
                                    helper.AppendLine($"\t\t\t{messageDefinitionName}_message.{EscapeIdentifier(field.Name)} = {ToCamelCaseIdentifier(field.Name)};");
                                }

                                helper.AppendLine($"\t\t\tsession.Send({messageDefinitionName}_message);");
                                helper.AppendLine("\t\t}");
                            }
                            else
                            {
                                helper.AppendLine("\t\t[MethodImpl(MethodImplOptions.AggressiveInlining)]");
                                helper.AppendLine($"\t\tpublic static void {escapedMessageName}(this Session session)");
                                helper.AppendLine("\t\t{");
                                helper.AppendLine($"\t\t\tusing var message = Fantasy.{escapedMessageName}.Create();");
                                helper.AppendLine("\t\t\tsession.Send(message);");
                                helper.AppendLine("\t\t}");
                            }

                            break;
                        }
                    case MessageType.Request:
                    case MessageType.RouteTypeRequest:
                    case MessageType.RoamingRequest:
                        {
                            helper.AppendLine($"\t\t[MethodImpl(MethodImplOptions.AggressiveInlining)]");
                            helper.AppendLine($"\t\tpublic static async FTask<{escapedResponseType}> {escapedMessageName}(this Session session, {escapedMessageName} {messageDefinitionName}_request)");
                            helper.AppendLine("\t\t{");
                            helper.AppendLine($"\t\t\treturn ({escapedResponseType})await session.Call({messageDefinitionName}_request);");
                            helper.AppendLine("\t\t}");

                            if (messageDefinition.Fields.Count > 0)
                            {
                                var parameters = string.Join(", ",
                                    messageDefinition.Fields
                                    .Where(f => f.CustomTextLine == null) // 跳过自定义文本行
                                    .Select(f =>
                                        $"{ConvertType(f)} {ToCamelCaseIdentifier(f.Name)}"));
                                helper.AppendLine($"\t\t[MethodImpl(MethodImplOptions.AggressiveInlining)]");
                                helper.AppendLine($"\t\tpublic static async FTask<{escapedResponseType}> {escapedMessageName}(this Session session, {parameters})");
                                helper.AppendLine("\t\t{");
                                helper.AppendLine($"\t\t\tusing var {messageDefinitionName}_request = Fantasy.{escapedMessageName}.Create();");
                                foreach (var field in messageDefinition.Fields)
                                {
                                    if (field.CustomTextLine != null) // 跳过自定义文本行
                                        continue;
                                    helper.AppendLine($"\t\t\t{messageDefinitionName}_request.{EscapeIdentifier(field.Name)} = {ToCamelCaseIdentifier(field.Name)};");
                                }
                                helper.AppendLine($"\t\t\treturn ({escapedResponseType})await session.Call({messageDefinitionName}_request);");
                                helper.AppendLine("\t\t}");
                            }
                            else
                            {
                                helper.AppendLine($"\t\t[MethodImpl(MethodImplOptions.AggressiveInlining)]");
                                helper.AppendLine($"\t\tpublic static async FTask<{escapedResponseType}> {escapedMessageName}(this Session session)");
                                helper.AppendLine("\t\t{");
                                helper.AppendLine($"\t\t\tusing var {messageDefinitionName}_request = Fantasy.{escapedMessageName}.Create();");
                                helper.AppendLine($"\t\t\treturn ({escapedResponseType})await session.Call({messageDefinitionName}_request);");
                                helper.AppendLine("\t\t}");
                            }
                            break;
                        }
                }
        }
        
        return $$"""
                 using System.Runtime.CompilerServices;
                 using Fantasy;
                 using Fantasy.Async;
                 using Fantasy.Network;
                 using System.Collections.Generic;
                 #pragma warning disable CS8618
                 namespace Fantasy
                 {
                    public static class NetworkProtocolHelper
                    {
                 {{helper}}
                    }
                 }
                 """;
    }
    
    protected override string GenerateOuterEnums(IReadOnlyList<EnumDefinition> enumDefinitions)
    {
        return enumDefinitions.Count == 0
            ? string.Empty
            : $$"""
                // ReSharper disable InconsistentNaming
                // ReSharper disable UnusedMember.Global
                #pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
                namespace Fantasy
                {
                {{GenerateEnumsCode(enumDefinitions)}}
                }
                """;
    }

    protected override string GenerateInnerEnums(IReadOnlyList<EnumDefinition> enumDefinitions)
    {
        return enumDefinitions.Count == 0
            ? string.Empty
            : $$"""
                // ReSharper disable InconsistentNaming
                // ReSharper disable UnusedMember.Global
                #pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
                namespace Fantasy
                {
                {{GenerateEnumsCode(enumDefinitions)}}
                }
                """;
    }

    private string GenerateNamespaces(IReadOnlySet<string> namespaces)
    {
        var builder = new StringBuilder();

        foreach (var @namespace in namespaces)
        {
            builder.AppendLine($"using {@namespace};");
        }
        
        return builder.ToString();
    }

    private string GenerateMessages(string opcodeName, IReadOnlyDictionary<string, MessageDefinition> messageDefinitions)
    {
        var builder = new StringBuilder();

        foreach (var (messageDefinitionName, messageDefinition) in messageDefinitions)
        {
            var escapedMessageName = EscapeIdentifier(messageDefinitionName);
            var escapedMessageClassName = EscapeIdentifier(messageDefinition.Name);
            var escapedResponseType = EscapeIdentifier(messageDefinition.ResponseType);
            var disposeCode = new StringBuilder();
            var members = new List<string>();
            var memberAttribute = messageDefinition.Protocol.MemberAttribute;
            var ignoreAttribute = messageDefinition.Protocol.IgnoreAttribute;
            var hasErrorCodeField = messageDefinition.Fields.Any(field => field.Name == "ErrorCode");
            
            if (messageDefinition.HasOpCode)
            {
                members.Add($"        public uint OpCode() {{ return {opcodeName}.{escapedMessageName}; }} ");
            }
            
            if (IsRequestType(messageDefinition.MessageType))
            {
                members.Add($"        {ignoreAttribute}");
                members.Add($"        public {escapedResponseType} ResponseType {{ get; set; }}");
            }
            
            if (IsResponseType(messageDefinition.MessageType))
            {
                if (!hasErrorCodeField && memberAttribute != null)
                {
                    members.Add($"        [{memberAttribute}({messageDefinition.ErrorCodeIndex})]");
                }

                disposeCode.AppendLine("            ErrorCode = 0;");
                if (!hasErrorCodeField)
                {
                    members.Add("        public uint ErrorCode { get; set; }");
                }
            }
            
            if (HasRouteType(messageDefinition.MessageType))
            {
                if (memberAttribute != null)
                {
                    members.Add($"        {ignoreAttribute}");
                }

                members.Add($"        public int RouteType => {messageDefinition.CustomRouteType};");
            }
            
            foreach (var field in messageDefinition.Fields)
            {
                if(field.CustomTextLine != null) // 直接输出自定义文本行
                {
                    members.Add(field.CustomTextLine);
                    continue;
                }

                var fieldName = EscapeIdentifier(field.Name);
                string? disposeStatement;
                switch (field.CollectionType)
                {
                    case FieldCollectionType.Repeated:
                    {
                        if (messageDefinitions.ContainsKey(field.Type))
                        {
                            disposeStatement = $"foreach (var __t in {fieldName}) __t.Dispose();\n            {fieldName}.Clear();";
                        }
                        else
                        {
                            disposeStatement = $"{fieldName}.Clear();";
                        }
                        break;
                    }
                    case FieldCollectionType.Map:
                    {
                        disposeStatement = messageDefinitions.ContainsKey(field.MapValueType) ? 
                            $"foreach (var __t in {fieldName}.Values) __t.Dispose();\n            {fieldName}.Clear();" : 
                            $"{fieldName}.Clear();";
                        break;
                    }
                    case FieldCollectionType.RepeatedList:
                    case FieldCollectionType.RepeatedArray:
                    {
                        disposeStatement = $"{fieldName} = null;";
                        break;
                    }
                    default:
                    {
                        disposeStatement = messageDefinitions.ContainsKey(field.Type) ? $$"""
                                  if ({{fieldName}} != null)
                                              {
                                                  {{fieldName}}.Dispose();
                                                  {{fieldName}} = null;
                                              }
                                  """ : $"{fieldName} = default;";
                        break;
                    }
                }

                disposeCode.AppendLine($"            {disposeStatement}");

                if (field.DocumentationComments.Count > 0)
                {
                    members.Add("        /// <summary>");
                    foreach (var messageDefinitionDocumentationComment in field.DocumentationComments)
                    {
                        members.Add($"        /// {messageDefinitionDocumentationComment}");
                    }
                    members.Add("        /// </summary>");
                }
                
                if (memberAttribute != null)
                {
                    members.Add($"        [{memberAttribute}({field.KeyIndex})]");
                }
                
                var fieldType = ConvertType(field);
                var initializer = GetInitializer(field);
                members.Add($"        public {fieldType} {fieldName} {{ get; set; }}{initializer}");
            }
            
            disposeCode.Append($"            MessageObjectPool<{escapedMessageName}>.Return(this);");

            if (messageDefinition.DocumentationComments.Count > 0)
            {
                builder.AppendLine("    /// <summary>");
                foreach (var messageDefinitionDocumentationComment in messageDefinition.DocumentationComments)
                {
                    builder.AppendLine($"    /// {messageDefinitionDocumentationComment}");
                }
                builder.AppendLine("    /// </summary>");
            }

            if (messageDefinition.DefineConstants.Count > 0)
            {
                foreach (var messageDefinitionDefineConstant in messageDefinition.DefineConstants)
                {
                    builder.AppendLine($"{messageDefinitionDefineConstant}");
                }
            }

            if (!string.IsNullOrEmpty(messageDefinition.Protocol.ClassAttribute))
            {
                builder.AppendLine($"    [Serializable]\n" +
                                   $"    {messageDefinition.Protocol.ClassAttribute}");
            }
            
            if (string.IsNullOrEmpty(messageDefinition.InterfaceType))
            {
                builder.AppendLine($"    public partial class {escapedMessageClassName} : AMessage, IDisposable");
            }
            else
            {
                builder.AppendLine($"    public partial class {escapedMessageClassName} : AMessage, {messageDefinition.InterfaceType}");
            }

            var messageName = ToCamelCaseIdentifier(messageDefinitionName);
            builder.AppendLine($$"""
                                       {
                                           public static {{escapedMessageName}} Create(bool autoReturn = true)
                                           {
                                               var {{messageName}} = MessageObjectPool<{{escapedMessageName}}>.Rent();
                                               {{messageName}}.AutoReturn = autoReturn;
                                               
                                               if (!autoReturn)
                                               {
                                                   {{messageName}}.SetIsPool(false);
                                               }
                                               
                                               return {{messageName}};
                                           }
                                           
                                           public void Return()
                                           {
                                               if (!AutoReturn)
                                               {
                                                   SetIsPool(true);
                                                   AutoReturn = true;
                                               }
                                               else if (!IsPool())
                                               {
                                                   return;
                                               }
                                               Dispose();
                                           }

                                           public void Dispose()
                                           {
                                               if (!IsPool()) return; 
                                   {{disposeCode}}
                                           }
                                   {{string.Join(Environment.NewLine, members)}}
                                       }
                                   """);
        }
        
        if (builder.Length >= Environment.NewLine.Length)
        {
            builder.Length -= Environment.NewLine.Length;
        }

        return builder.ToString();
    }
    
    public static string ConvertType(FieldDefinition field)
    {
        switch (field.CollectionType)
        {
            case FieldCollectionType.Normal:
            case FieldCollectionType.None:
            {
                return ConvertBaseType(field.Type);
            }
            case FieldCollectionType.Repeated:
            case FieldCollectionType.RepeatedList:
            {
                return $"List<{ConvertBaseType(field.Type)}>";
            }
            case FieldCollectionType.RepeatedArray:
            {
                return $"{ConvertBaseType(field.Type)}[]";
            }
            case FieldCollectionType.Map:
            {
                var keyType = ConvertBaseType(field.MapKeyType);
                var valueType = ConvertBaseType(field.MapValueType);
                return $"Dictionary<{keyType}, {valueType}>";
            }
            default:
            {
                throw new NotSupportedException(
                    $"Unsupported collection type '{field.CollectionType}' at line {field.SourceLineNumber}");
            }
        }
    }

    public static string GetInitializer(FieldDefinition field)
    {
        switch (field.CollectionType)
        {
            case FieldCollectionType.Repeated:
            {
                return $" = new List<{ConvertBaseType(field.Type)}>();";
            }
            case FieldCollectionType.Map:
            {
                var keyType = ConvertBaseType(field.MapKeyType);
                var valueType = ConvertBaseType(field.MapValueType);
                return $" = new Dictionary<{keyType}, {valueType}>();";
            }
            default:
            {
                return string.Empty;
            }
        }
    }

    public static string ConvertBaseType(string type)
    {
        switch (type)
        {
            case "int32":
            {
                return "int";
            }
            case "uint32":
            {
                return "uint";
            }
            case "int64":
            {
                return "long";
            }
            case "uint64":
            {
                return "ulong";
            }
            case "float":
            {
                return "float";
            }
            case "double":
            {
                return "double";
            }
            case "bool":
            {
                return "bool";
            }
            case "string":
            {
                return "string";
            }
            case "int":
            {
                return "int";
            }
            case "uint":
            {
                return "uint";
            }
            case "long":
            {
                return "long";
            }
            case "ulong":
            {
                return "ulong";
            }
            case "byte":
            {
                return "byte";
            }
            case "bytes":
            {
                return "byte[]";
            }
            default:
            {
                // 无法识别的类型，默认就按照定义的类型来输出（若为非基元类型 C# 关键字则转义）
                return EscapeTypeIdentifier(type);
            }
        }
    }

    /// <summary>
    /// 生成枚举代码（处理条件编译）
    /// </summary>
    private static string GenerateEnumsCode(IReadOnlyList<EnumDefinition> enumDefinitions)
    {
        var builder = new StringBuilder();

        foreach (var enumDefinition in enumDefinitions)
        {
            builder.Append(GenerateEnumCode(enumDefinition));
        }

        return builder.ToString();
    }

    /// <summary>
    /// 生成单个枚举的 C# 代码
    /// </summary>
    private static string GenerateEnumCode(EnumDefinition enumDefinition)
    {
        var builder = new StringBuilder();

        // 生成枚举的 XML 注释
        if (enumDefinition.DocumentationComments.Count > 0)
        {
            builder.AppendLine("\t/// <summary>");
            foreach (var comment in enumDefinition.DocumentationComments)
            {
                builder.AppendLine($"\t/// {comment}");
            }
            builder.AppendLine("\t/// </summary>");
        }

        // 生成枚举声明
        builder.AppendLine($"\tpublic enum {EscapeIdentifier(enumDefinition.Name)}");
        builder.AppendLine("\t{");

        // 生成枚举值
        for (var i = 0; i < enumDefinition.Values.Count; i++)
        {
            var value = enumDefinition.Values[i];

            // 生成枚举值的 XML 注释
            if (value.DocumentationComments.Count > 0)
            {
                builder.AppendLine("\t\t/// <summary>");
                foreach (var comment in value.DocumentationComments)
                {
                    builder.AppendLine($"\t\t/// {comment}");
                }
                builder.AppendLine("\t\t/// </summary>");
            }

            // 生成枚举值
            var comma = i < enumDefinition.Values.Count - 1 ? "," : string.Empty;
            builder.AppendLine($"\t\t{EscapeIdentifier(value.Name)} = {value.Value}{comma}");
        }

        builder.AppendLine("\t}");
        builder.AppendLine();

        return builder.ToString();
    }
    
    private static bool IsRequestType(MessageType type) =>
        type is MessageType.Request or MessageType.RouteTypeRequest or MessageType.RoamingRequest;

    private static bool IsResponseType(MessageType type) =>
        type is MessageType.Response or MessageType.RouteTypeResponse or MessageType.RoamingResponse;

    private static bool HasRouteType(MessageType type) =>
        type is MessageType.RouteTypeMessage or MessageType.RoamingMessage
            or MessageType.RoamingRequest or MessageType.RouteTypeRequest;
}
