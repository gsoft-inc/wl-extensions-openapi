using Microsoft.CodeAnalysis;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Diagnostics;
using Workleap.Extensions.OpenAPI.Analyzers.Internals;

namespace Workleap.Extensions.OpenAPI.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class CompareTypedResultWithAnnotationAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor AnnotationMustMatchTypedResult = new(
        id: RuleIdentifiers.MismatchResponseTypeWithAnnotation,
        title: "Mismatch between annotation return type and endpoint return type",
        messageFormat: "Mismatch between annotation return type and endpoint return type",
        category: RuleCategories.Design,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: RuleIdentifiers.HelpUri);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(AnnotationMustMatchTypedResult);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterCompilationStartAction(compilationContext =>
        {
            var analyzerContext = new AnalyzerContext(compilationContext.Compilation);
            if (analyzerContext.IsValid)
            {
                compilationContext.RegisterSymbolAction(analyzerContext.ValidateEndpointResponseWithAnnotationType, SymbolKind.Method);
            }
        });
    }

    private sealed class AnalyzerContext(Compilation compilation)
    {
        private INamedTypeSymbol? TaskOfTSymbol { get; } = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1");
        private INamedTypeSymbol? ValueTaskOfTSymbol { get; } = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1");
        private INamedTypeSymbol? ProducesResponseSymbol { get; } = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute");
        private INamedTypeSymbol? ProducesResponseOfTSymbol { get; } = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute`1");
        private INamedTypeSymbol? SwaggerResponseSymbol { get; } = compilation.GetTypeByMetadataName("Swashbuckle.AspNetCore.Annotations.SwaggerResponseAttribute");

        private INamedTypeSymbol? ResultSymbol { get; } = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Http.IResult");

        public bool IsValid => this.ResultSymbol is not null && this.ProducesResponseSymbol is not null;

        private INamedTypeSymbol?[] ResultTaskOfTSymbol { get; } =
        [
            compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Http.HttpResults.Results`2"),
            compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Http.HttpResults.Results`3"),
            compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Http.HttpResults.Results`4"),
            compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Http.HttpResults.Results`5"),
            compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Http.HttpResults.Results`6"),
        ];

        private readonly Dictionary<ITypeSymbol, int> _resultsToStatusCodeMap = InitializeHttpResultStatusCodeMap(compilation);
        private readonly Dictionary<int, ITypeSymbol> _statusCodeToResultsMap = InitializeStatusCodeMapHttpResultMap(compilation);

        private static Dictionary<ITypeSymbol, int> InitializeHttpResultStatusCodeMap(Compilation compilation)
        {
            var dictionary = new Dictionary<ITypeSymbol, int>(SymbolEqualityComparer.Default);

            foreach (var pair in HttpResultsStatusCodeTypeHelpers.HttpResultTypeToStatusCodes)
            {
                var type = compilation.GetTypeByMetadataName(pair.Key);
                if (type is not null)
                {
                    dictionary.Add(type, pair.Value);
                }
            }

            return dictionary;
        }

        private static Dictionary<int, ITypeSymbol> InitializeStatusCodeMapHttpResultMap(Compilation compilation)
        {
            var dictionary = new Dictionary<int, ITypeSymbol>();
            Add(200, "Microsoft.AspNetCore.Http.HttpResults.Ok");
            Add(201, "Microsoft.AspNetCore.Http.HttpResults.Created");
            Add(202, "Microsoft.AspNetCore.Http.HttpResults.Accepted");
            Add(204, "Microsoft.AspNetCore.Http.HttpResults.NoContent");
            Add(400, "Microsoft.AspNetCore.Http.HttpResults.BadRequest");
            Add(401, "Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult");
            Add(403, "Workleap.Extensions.OpenAPI.TypedResult.Forbidden");
            Add(404, "Microsoft.AspNetCore.Http.HttpResults.NotFound");
            Add(409, "Microsoft.AspNetCore.Http.HttpResults.Conflict");
            Add(422, "Microsoft.AspNetCore.Http.HttpResults.UnprocessableEntity");
            Add(500, "Workleap.Extensions.OpenAPI.TypedResult.InternalServerError");

            return dictionary;

            void Add(int statusCode, string metadata)
            {
                var type = compilation.GetTypeByMetadataName(metadata);
                if (type is not null)
                {
                    dictionary.Add(statusCode, type);
                }
            }
        }

        public void ValidateEndpointResponseWithAnnotationType(SymbolAnalysisContext context)
        {
            if (context.Symbol.GetAttributes().Length == 0)
            {
                return;
            }

            var methodSymbol = (IMethodSymbol)context.Symbol;
            var typedReturnType = this.GetReturnTypeSymbol(methodSymbol);
            if (typedReturnType != null && this.IsImplementingIResult(typedReturnType))
            {
                var methodSignatureStatusCodeToType = this.GetMethodReturnStatusCodeToType(typedReturnType);

                foreach (var attribute in methodSymbol.GetAttributes())
                {
                    this.ValidateAnnotationWithTypedResult(context, attribute, methodSignatureStatusCodeToType);
                }
            }
        }

        private INamedTypeSymbol? GetReturnTypeSymbol(IMethodSymbol methodSymbol)
        {
            var returnType = methodSymbol.ReturnType;

            if (returnType is INamedTypeSymbol typedReturnType)
            {
                return RoslynExtensions.UnwrapTypeFromTask(typedReturnType, this.TaskOfTSymbol, this.ValueTaskOfTSymbol);
            }

            return null;
        }

        private void ValidateAnnotationWithTypedResult(SymbolAnalysisContext context, AttributeData attribute,
            Dictionary<int, List<ITypeSymbol>> methodSignatureStatusCodeToTypeMap)
        {
            if (attribute.AttributeClass == null || (attribute.ConstructorArguments.Length == 0 && attribute.NamedArguments.Length == 0))
            {
                return;
            }

            // For the case when the Type is a named argument 
            if (TryGetTypeFromNamedArguments(attribute, out var typeFromNamedArgument) && typeFromNamedArgument != null)
            {
                this.HandleTypeAsNamedArgument(context, attribute, methodSignatureStatusCodeToTypeMap, typeFromNamedArgument);
            }

            // For the annotations [ProducesResponseType(<StatusCode>)], [ProducesResponseType(<typeof()>, <StatusCode>)] and [ProducesResponseType(<StatusCode>, Type = <typeof()>)] 
            else if (attribute.AttributeClass.Equals(this.ProducesResponseSymbol, SymbolEqualityComparer.Default))
            {
                this.HandleProducesResponseAnnotation(context, attribute, methodSignatureStatusCodeToTypeMap);
            }
            // For the annotations [ProducesResponseType<T>(<StatusCode>)]
            else if (attribute.AttributeClass.ConstructedFrom.Equals(this.ProducesResponseOfTSymbol, SymbolEqualityComparer.Default))
            {
                this.HandleProducesResponseOfTAnnotation(context, attribute, methodSignatureStatusCodeToTypeMap);
            }

            // For the annotations [SwaggerResponse(<StatusCode>, "description", <typeof()>] and [SwaggerResponse(<StatusCode>, "description", Type = <typeof()>]
            else if (attribute.AttributeClass.ConstructedFrom.Equals(this.SwaggerResponseSymbol, SymbolEqualityComparer.Default))
            {
                this.HandleSwaggerResponseAnnotation(context, attribute, methodSignatureStatusCodeToTypeMap);
            }
        }

        private void HandleTypeAsNamedArgument(SymbolAnalysisContext context, AttributeData attribute,
            Dictionary<int, List<ITypeSymbol>> methodSignatureStatusCodeToTypeMap, ITypeSymbol typeFromNamedArgument)
        {
            if (attribute.ConstructorArguments[0].Value is int statusCodeValue)
            {
                ValidateAnnotationForTypeMismatch(attribute, statusCodeValue, typeFromNamedArgument, methodSignatureStatusCodeToTypeMap, context);
            }
        }

        private void HandleProducesResponseAnnotation(SymbolAnalysisContext context, AttributeData attribute,
            Dictionary<int, List<ITypeSymbol>> methodSignatureStatusCodeToTypeMap)
        {
            // If there is a single argument, then StatusCode is the first argument.
            if (attribute.ConstructorArguments.Length == 1)
            {
                if (attribute.ConstructorArguments[0].Value is not int statusCodeValue)
                {
                    return;
                }

                if (this._statusCodeToResultsMap.TryGetValue(statusCodeValue, out var type))
                {
                    ValidateAnnotationForTypeMismatch(attribute, statusCodeValue, type, methodSignatureStatusCodeToTypeMap, context);
                }
            }
            // If there are two arguments, then StatusCode is the second argument.
            else if (attribute.ConstructorArguments[1].Value is int statusCodeValue && attribute.ConstructorArguments[0].Value is ITypeSymbol type)
            {
                ValidateAnnotationForTypeMismatch(attribute, statusCodeValue, type, methodSignatureStatusCodeToTypeMap, context);
            }
        }

        private void HandleProducesResponseOfTAnnotation(SymbolAnalysisContext context, AttributeData attribute,
            Dictionary<int, List<ITypeSymbol>> methodSignatureStatusCodeToTypeMap)
        {
            if (attribute.ConstructorArguments[0].Value is int statusCodeValue && attribute.AttributeClass != null)
            {
                ValidateAnnotationForTypeMismatch(attribute, statusCodeValue, attribute.AttributeClass.TypeArguments[0], methodSignatureStatusCodeToTypeMap, context);
            }
        }

        private void HandleSwaggerResponseAnnotation(SymbolAnalysisContext context, AttributeData attribute,
            Dictionary<int, List<ITypeSymbol>> methodSignatureStatusCodeToTypeMap)
        {
            if (attribute.ConstructorArguments.Length > 2 && attribute.ConstructorArguments[0].Value is int statusCodeValue)
            {
                if (attribute.ConstructorArguments[2].Value is ITypeSymbol typeFromAnnotation)
                {
                    ValidateAnnotationForTypeMismatch(attribute, statusCodeValue, typeFromAnnotation, methodSignatureStatusCodeToTypeMap, context);
                }
                else if (this._statusCodeToResultsMap.TryGetValue(statusCodeValue, out var type))
                {
                    ValidateAnnotationForTypeMismatch(attribute, statusCodeValue, type, methodSignatureStatusCodeToTypeMap, context);
                }
            }
        }

        private static bool TryGetTypeFromNamedArguments(AttributeData attribute, out ITypeSymbol? type)
        {
            var typeNamedArgument = attribute.NamedArguments.FirstOrDefault(kp => kp.Key == "Type");
            if (typeNamedArgument.Value.Value is ITypeSymbol matchedType)
            {
                type = matchedType;
                return true;
            }

            type = null;
            return false;
        }

        // Result<Ok<type>, Notfound>
        private Dictionary<int, List<ITypeSymbol>> GetMethodReturnStatusCodeToType(ITypeSymbol methodSymbol)
        {
            var methodReturnSignatures = this.ExtractStatusCodeAndResultFromMethodReturn(methodSymbol);
            var methodSignatureStatusCodeToTypeMap = new Dictionary<int, List<ITypeSymbol>>();
            foreach (var returnValues in methodReturnSignatures)
            {
                if (methodSignatureStatusCodeToTypeMap.TryGetValue(returnValues.statusCode, out var returnTypeSymbols))
                {
                    returnTypeSymbols.Add(returnValues.symbol);
                }
                else
                {
                    methodSignatureStatusCodeToTypeMap.Add(returnValues.statusCode, [returnValues.symbol]);
                }
            }

            return methodSignatureStatusCodeToTypeMap;
        }

        private IEnumerable<(int statusCode, ITypeSymbol symbol)> ExtractStatusCodeAndResultFromMethodReturn(ITypeSymbol methodSymbol)
        {
            if (methodSymbol is not INamedTypeSymbol namedTypeSymbol)
            {
                return Enumerable.Empty<(int, ITypeSymbol)>();
            }

            // Result<OK, NotFound>
            if (this.ResultTaskOfTSymbol.Any(symbol => SymbolEqualityComparer.Default.Equals(namedTypeSymbol.ConstructedFrom, symbol)))
            {
                return namedTypeSymbol.TypeArguments.SelectMany(this.ExtractStatusCodeAndResultFromMethodReturn);
            }

            if (this._resultsToStatusCodeMap.TryGetValue(namedTypeSymbol.ConstructedFrom, out var statusCode))
            {
                // If there is a type, then return the type, otherwise return IResult type
                return [(statusCode, namedTypeSymbol.TypeArguments.Length == 0 ? namedTypeSymbol : namedTypeSymbol.TypeArguments[0])];
            }

            return Enumerable.Empty<(int, ITypeSymbol)>();
        }

        private static void ValidateAnnotationForTypeMismatch(AttributeData attribute, int statusCodeFromAnnotation, ITypeSymbol typeFromAnnotation,
            Dictionary<int, List<ITypeSymbol>> methodReturnStatusCodeTypes, SymbolAnalysisContext context)
        {
            if (attribute.ApplicationSyntaxReference is null)
            {
                return;
            }

            if (!methodReturnStatusCodeTypes.TryGetValue(statusCodeFromAnnotation, out var mappedType))
            {
                return;
            }

            if (!mappedType.Any(type => SymbolEqualityComparer.Default.Equals(type, typeFromAnnotation)))
            {
                var attributeLocation = attribute.ApplicationSyntaxReference.GetSyntax(context.CancellationToken).GetLocation();
                context.ReportDiagnostic(AnnotationMustMatchTypedResult, attributeLocation);
            }
        }

        private bool IsImplementingIResult(ITypeSymbol currentClassSymbol)
        {
            var resultSymbol = this.ResultSymbol;
            return SymbolEqualityComparer.Default.Equals(currentClassSymbol, resultSymbol) || currentClassSymbol.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(resultSymbol, i));
        }
    }
}