#pragma warning disable CS1998

using MagicOnion.CodeAnalysis;
using System.Linq;
using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using MagicOnion.Generator;

namespace MagicOnion
{
    public class MagicOnionCompiler
    {
        private static readonly Encoding NoBomUtf8 = new UTF8Encoding(false);

        private Action<string> logger;
        private CancellationToken cancellationToken;

        public MagicOnionCompiler(Action<string> logger, CancellationToken cancellationToken)
        {
            this.logger = logger;
            this.cancellationToken = cancellationToken;
        }

        public async Task GenerateFileAsync(
            string input,
            string output,
            bool unuseUnityAttr,
            string @namespace,
            string conditionalSymbol,
            string messagePackGeneratedNamespace)
        {
            // Prepare args
            var namespaceDot = string.IsNullOrWhiteSpace(@namespace) ? string.Empty : @namespace + ".";
            var conditionalSymbols = conditionalSymbol?.Split(',') ?? Array.Empty<string>();

            // Generator Start...

            var sw = Stopwatch.StartNew();
            logger("Project Compilation Start:" + input);

            var collector = new MethodCollector(input, conditionalSymbols, logger);

            logger("Project Compilation Complete:" + sw.Elapsed.ToString());

            sw.Restart();
            logger("Method Collect Start");

            var definitions = collector.CollectServiceInterface();
            var hubDefinitions = collector.CollectHubInterface();

            GenericSerializationInfo[] genericInfos;
            EnumSerializationInfo[] enumInfos;
            ExtractResolverInfo(definitions, out genericInfos, out enumInfos, messagePackGeneratedNamespace);
            ExtractResolverInfo(hubDefinitions.Select(x => x.hubDefinition).ToArray(), out var genericInfos2, out var enumInfos2, messagePackGeneratedNamespace);
            ExtractResolverInfo(hubDefinitions.Select(x => x.receiverDefintion).ToArray(), out var genericInfos3, out var enumInfos3, messagePackGeneratedNamespace);
            enumInfos = enumInfos.Concat(enumInfos2).Concat(enumInfos3).Distinct().OrderBy(x => x.FullName).ToArray();
            genericInfos = genericInfos.Concat(genericInfos2).Concat(genericInfos3).Distinct().OrderBy(x => x.FullName).ToArray();

            logger("Method Collect Complete:" + sw.Elapsed.ToString());

            logger("Output Generation Start");
            sw.Restart();

            var resolverTemplate = new ResolverTemplate()
            {
                Namespace = namespaceDot + "Resolvers",
                FormatterNamespace = namespaceDot + "Formatters",
                ResolverName = "MagicOnionResolver",
                registerInfos = genericInfos.OrderBy(x => x.FullName).Cast<IResolverRegisterInfo>().Concat(enumInfos.OrderBy(x => x.FullName)).ToArray()
            };

            var registerTemplate = new RegisterTemplate
            {
                Namespace = @namespace,
                Interfaces = definitions.Where(x => x.IsServiceDefinition).ToArray(),
                HubInterfaces = hubDefinitions,
                UnuseUnityAttribute = unuseUnityAttr
            };

            if (Path.GetExtension(output) == ".cs")
            {
                var enumTemplates = enumInfos.GroupBy(x => x.Namespace)
                    .OrderBy(x => x.Key)
                    .Select(x => new EnumTemplate()
                    {
                        Namespace = namespaceDot + "Formatters",
                        enumSerializationInfos = x.ToArray()
                    })
                    .ToArray();

                var texts = definitions
                    .GroupBy(x => x.Namespace)
                    .OrderBy(x => x.Key)
                    .Select(x => new CodeTemplate()
                    {
                        Namespace = x.Key,
                        Interfaces = x.ToArray()
                    })
                    .ToArray();

                var hubTexts = hubDefinitions
                    .GroupBy(x => x.hubDefinition.Namespace)
                    .OrderBy(x => x.Key)
                    .Select(x => new HubTemplate()
                    {
                        Namespace = x.Key,
                        Interfaces = x.ToArray()
                    })
                    .ToArray();

                var sb = new StringBuilder();
                sb.AppendLine("// <auto-generated />");
                sb.AppendLine(registerTemplate.TransformText());
                sb.AppendLine(resolverTemplate.TransformText());
                foreach (var item in enumTemplates)
                {
                    sb.AppendLine(item.TransformText());
                }

                foreach (var item in texts)
                {
                    sb.AppendLine(item.TransformText());
                }

                foreach (var item in hubTexts)
                {
                    sb.AppendLine(item.TransformText());
                }

                Output(output, sb.ToString());
            }
            else
            {
                Output(NormalizePath(output, registerTemplate.Namespace, "MagicOnionInitializer"), WithAutoGenerated(registerTemplate.TransformText()));
                Output(NormalizePath(output, resolverTemplate.Namespace, resolverTemplate.ResolverName), WithAutoGenerated(resolverTemplate.TransformText()));

                foreach (var enumTemplate in enumInfos)
                {
                    var x = new EnumTemplate()
                    {
                        Namespace = namespaceDot + "Formatters",
                        enumSerializationInfos = new[] { enumTemplate }
                    };

                    Output(NormalizePath(output, x.Namespace, enumTemplate.Name + "Formatter"), WithAutoGenerated(x.TransformText()));
                }

                foreach (var serviceClient in definitions)
                {
                    var x = new CodeTemplate()
                    {
                        Namespace = serviceClient.Namespace,
                        Interfaces = new[] { serviceClient }
                    };

                    Output(NormalizePath(output, serviceClient.Namespace, serviceClient.ClientName), WithAutoGenerated(x.TransformText()));
                }

                foreach (var hub in hubDefinitions)
                {
                    var x = new HubTemplate()
                    {
                        Namespace = hub.hubDefinition.Namespace,
                        Interfaces = new[] { hub }
                    };

                    Output(NormalizePath(output, hub.hubDefinition.Namespace, hub.hubDefinition.ClientName), WithAutoGenerated(x.TransformText()));
                }
            }

            if (definitions.Length == 0 && hubDefinitions.Length == 0)
            {
                logger("Generated result is empty, unexpected result?");
            }

            logger("Output Generation Complete:" + sw.Elapsed.ToString());
        }

        static string NormalizePath(string dir, string ns, string className)
        {
            return Path.Combine(dir, $"{ns}_{className}".Replace(".", "_").Replace("global::", string.Empty) + ".cs");
        }

        static string WithAutoGenerated(string s)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated />");
            sb.AppendLine(s);
            return sb.ToString();
        }

        static string NormalizeNewLines(string content)
        {
            // The T4 generated code may be text with mixed line ending types. (CR + CRLF)
            // We need to normalize the line ending type in each Operating Systems. (e.g. Windows=CRLF, Linux/macOS=LF)
            return content.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        }

        static void Output(string path, string text)
        {
            path = path.Replace("global::", "");

            const string prefix = "[Out]";
            Console.WriteLine(prefix + path);

            var fi = new FileInfo(path);
            if (!fi.Directory.Exists)
            {
                fi.Directory.Create();
            }

            System.IO.File.WriteAllText(path, NormalizeNewLines(text), NoBomUtf8);
        }

        static readonly SymbolDisplayFormat binaryWriteFormat = new SymbolDisplayFormat(
                genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
                miscellaneousOptions: SymbolDisplayMiscellaneousOptions.ExpandNullable,
                typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly);

        static readonly HashSet<string> embeddedTypes = new HashSet<string>(new string[]
        {
            "short",
            "int",
            "long",
            "ushort",
            "uint",
            "ulong",
            "float",
            "double",
            "bool",
            "byte",
            "sbyte",
            "decimal",
            "char",
            "string",
            "System.Guid",
            "System.TimeSpan",
            "System.DateTime",
            "System.DateTimeOffset",

            "MessagePack.Nil",

            // and arrays
            
            "short[]",
            "int[]",
            "long[]",
            "ushort[]",
            "uint[]",
            "ulong[]",
            "float[]",
            "double[]",
            "bool[]",
            "byte[]",
            "sbyte[]",
            "decimal[]",
            "char[]",
            "string[]",
            "System.DateTime[]",
            "System.ArraySegment<byte>",
            "System.ArraySegment<byte>?",

            // extensions

            "UnityEngine.Vector2",
            "UnityEngine.Vector3",
            "UnityEngine.Vector4",
            "UnityEngine.Quaternion",
            "UnityEngine.Color",
            "UnityEngine.Bounds",
            "UnityEngine.Rect",

            "System.Reactive.Unit",
        });

        static readonly Dictionary<string, string> additionalSupportGenericFormatter = new Dictionary<string, string>
        {
            {"System.Collections.Generic.List<>", "global::MessagePack.Formatters.ListFormatter<TREPLACE>()" },
            {"System.Collections.Generic.Dictionary<,>", "global::MessagePack.Formatters.DictionaryFormatter<TREPLACE>()"},
        };

        static void ExtractResolverInfo(InterfaceDefinition[] definitions, out GenericSerializationInfo[] genericInfoResults, out EnumSerializationInfo[] enumInfoResults, string messagePackGeneratedNamespace)
        {
            var genericInfos = new List<GenericSerializationInfo>();
            var enumInfos = new List<EnumSerializationInfo>();

            foreach (var interfaceDef in definitions)
            {
                foreach (var method in interfaceDef.Methods)
                {
                    if (method.UnwrappedOriginalResposneTypeSymbol == null) continue;

                    var ifDirectiveConditions = new[] { interfaceDef.IfDirectiveCondition, method.IfDirectiveCondition }.Distinct().Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
                    TraverseTypes(method.UnwrappedOriginalResposneTypeSymbol, genericInfos, enumInfos, messagePackGeneratedNamespace, ifDirectiveConditions);

                    // paramter type
                    foreach (var p in method.Parameters.Select(x => x.OriginalSymbol.Type))
                    {
                        TraverseTypes(p, genericInfos, enumInfos, messagePackGeneratedNamespace, ifDirectiveConditions);
                    }

                    if (method.Parameters.Length > 1)
                    {
                        // create dynamicargumenttuple
                        var parameterArguments = method.Parameters.Select(x => x.OriginalSymbol)
                            .Select(x => $"default({x.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)})")
                            .ToArray();

                        var typeArguments = method.Parameters.Select(x => x.OriginalSymbol).Select(x => x.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

                        var tupleInfo = new GenericSerializationInfo(
                            $"global::MagicOnion.DynamicArgumentTuple<{string.Join(", ", typeArguments)}>",
                            $"global::MagicOnion.DynamicArgumentTupleFormatter<{string.Join(", ", typeArguments)}>({string.Join(", ", parameterArguments)})",
                            ifDirectiveConditions
                        );
                        genericInfos.Add(tupleInfo);
                    }
                }
            }

            genericInfoResults = genericInfos.Distinct().ToArray();
            enumInfoResults = enumInfos.Distinct().ToArray();
        }

        static void TraverseTypes(ITypeSymbol typeSymbol, List<GenericSerializationInfo> genericInfos, List<EnumSerializationInfo> enumInfos, string messagePackGeneratedNamespace, IReadOnlyList<string> ifDirectiveConditions)
        {
            var namedTypeSymbol = typeSymbol as INamedTypeSymbol;

            if (typeSymbol.TypeKind == TypeKind.Array)
            {
                var array = (IArrayTypeSymbol)typeSymbol;
                if (embeddedTypes.Contains(array.ToString())) return;
                MakeArray(array, genericInfos, ifDirectiveConditions);
                if (array.ElementType.TypeKind == TypeKind.Enum)
                {
                    MakeEnum(array.ElementType as INamedTypeSymbol, enumInfos, ifDirectiveConditions);
                }
            }
            else if (typeSymbol.TypeKind == TypeKind.Enum)
            {
                MakeEnum(namedTypeSymbol, enumInfos, ifDirectiveConditions);
            }
            else if (namedTypeSymbol != null && namedTypeSymbol.IsGenericType)
            {
                var genericType = namedTypeSymbol.ConstructUnboundGenericType();
                var genericTypeString = genericType.ToDisplayString();

                if (genericTypeString == "T?")
                {
                    // Nullable<T> (T?)
                    var more = namedTypeSymbol.TypeArguments[0];
                    if (more.TypeKind == TypeKind.Enum)
                    {
                        MakeEnum(more as INamedTypeSymbol, enumInfos, ifDirectiveConditions);
                    }

                    MakeNullable(namedTypeSymbol, genericInfos, ifDirectiveConditions);
                }
                else if (additionalSupportGenericFormatter.TryGetValue(genericTypeString, out var formatterString))
                {
                    // Well-known generic type.
                    // System.Collections.Generic.List<T> ...
                    MakeGenericWellKnown(namedTypeSymbol, formatterString, genericInfos, ifDirectiveConditions);
                }
                else
                {
                    // User-defined generic type.
                    // The object formatter is generated by MessagePack.Generator

                    // MyProject.MyClass<T> --> global::MessagePack.Formatters.MyProject.MyClassFormatter<T>
                    // MyProject.MyClass<T1, T2> --> global::MessagePack.Formatters.MyProject.MyClassFormatter<T1, T2>
                    //     MyProject.MyClass<MyObject> --> global::MessagePack.Generated.MyProject.MyClassFormatter<global::MyProject.MyObject>
                    //     MyProject.MyClass<MyObject, OtherObject> --> global::MessagePack.Generated.MyProject.MyClassFormatter<global::MyProject.MyObject, global::MyProject.OtherObject>
                    //         global::{MessagePackGeneratedNamespace}.{Namespace}.{ClassName}Formatter<{TypeArgs}>

                    var typeNamespace = namedTypeSymbol.ContainingNamespace.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "");
                    var typeName = namedTypeSymbol.Name;
                    var typeArgs = string.Join(", ", namedTypeSymbol.TypeArguments.Select(x => x.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).ToArray());
                    var formatterFullName =
                        $"global::{(string.IsNullOrWhiteSpace(messagePackGeneratedNamespace) ? "" : messagePackGeneratedNamespace + ".")}{(string.IsNullOrWhiteSpace(typeNamespace) ? "" : typeNamespace + ".")}{typeName}Formatter<{typeArgs}>";
                    var genericInfo = new GenericSerializationInfo(
                        namedTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                        $"{formatterFullName}()",
                        ifDirectiveConditions
                    );
                    genericInfos.Add(genericInfo);

                    // Recursively scan generic-types
                    foreach (var typeArg in namedTypeSymbol.TypeArguments)
                    {
                        TraverseTypes(typeArg, genericInfos, enumInfos, messagePackGeneratedNamespace, ifDirectiveConditions);
                    }
                }
            }
        }

        static void MakeArray(IArrayTypeSymbol array, List<GenericSerializationInfo> list, IReadOnlyList<string> ifDirectiveConditions)
        {
            var arrayInfo = new GenericSerializationInfo(
                array.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                $"global::MessagePack.Formatters.ArrayFormatter<{array.ElementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}>()",
                ifDirectiveConditions
            );
            list.Add(arrayInfo);
        }

        static void MakeEnum(INamedTypeSymbol enumType, List<EnumSerializationInfo> list, IReadOnlyList<string> ifDirectiveConditions)
        {
            var enumInfo = new EnumSerializationInfo(
                enumType.ContainingNamespace.IsGlobalNamespace ? null : enumType.ContainingNamespace.ToDisplayString(),
                enumType.Name,
                enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                enumType.EnumUnderlyingType.ToDisplayString(binaryWriteFormat),
                ifDirectiveConditions
            );
            list.Add(enumInfo);
        }

        static void MakeNullable(INamedTypeSymbol type, List<GenericSerializationInfo> list, IReadOnlyList<string> ifDirectiveConditions)
        {
            var info = new GenericSerializationInfo(
                type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                $"global::MessagePack.Formatters.NullableFormatter<{type.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}>()",
                ifDirectiveConditions
            );
            list.Add(info);
        }

        static void MakeGenericWellKnown(INamedTypeSymbol type, string formatterTemplate, List<GenericSerializationInfo> list, IReadOnlyList<string> ifDirectiveConditions)
        {
            var typeArgs = string.Join(", ", type.TypeArguments.Select(x => x.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
            var f = formatterTemplate.Replace("TREPLACE", typeArgs);

            var info = new GenericSerializationInfo(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), f, ifDirectiveConditions);
            list.Add(info);
        }
    }
}
