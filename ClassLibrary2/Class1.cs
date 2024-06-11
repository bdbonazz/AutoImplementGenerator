using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace ClassLibrary2;
#nullable enable
[Generator]
public class AutoImplementGenerator : IIncrementalGenerator
{
    private const string NameOfString = "nameof(";
    private const string AutoImplementAttributeNameSpace = "AttributeGenerator";
    private const string AutoImplementAttributeName = "AutoImplement";
    private const string AutoImplementAttributeClassName = $"{AutoImplementAttributeName}Attribute";
    private const string FullyQualifiedMetadataName = $"{AutoImplementAttributeNameSpace}.{AutoImplementAttributeClassName}";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(ctx =>
        {
            //Generate the AutoImplement Attribute
            const string autoImplementAttributeDeclarationCode = $$"""
using System;
namespace {{AutoImplementAttributeNameSpace}};

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
sealed class {{AutoImplementAttributeClassName}} : Attribute
{
    public string[] InterfacesNames { get; }
    public {{AutoImplementAttributeClassName}}(params string[] interfacesNames)
    {
        InterfacesNames = interfacesNames;
    }
}
""";
            ctx.AddSource($"{AutoImplementAttributeClassName}.g.cs", autoImplementAttributeDeclarationCode);
        });
        IncrementalValuesProvider<Model> provider = context.SyntaxProvider.ForAttributeWithMetadataName(
            fullyQualifiedMetadataName: FullyQualifiedMetadataName,
            predicate: static (node, cancellationToken_) => node is ClassDeclarationSyntax classDeclarationSyntax,
            transform: static (ctx, cancellationToken) =>
            {
                ClassDeclarationSyntax classDeclarationSyntax = (ClassDeclarationSyntax)ctx.TargetNode;
                InterfaceModel[] interfaces = GetInterfaceModels(ctx.SemanticModel.Compilation, classDeclarationSyntax);
                string classNameSpace = GetClassNameSpace(classDeclarationSyntax.Parent);
                
                return new Model(
                    classNameSpace,
                    classDeclarationSyntax.Identifier.ValueText,
                    interfaces
                    );
            }).Where(m => m is not null);

        IncrementalValueProvider<ImmutableArray<Model>> collection = provider.Collect();

        context.RegisterSourceOutput(provider.Collect(), Execute);
    }

    private static InterfaceModel[] GetInterfaceModels(Compilation compilation, ClassDeclarationSyntax classDeclarationSyntax)
    {
        //Get class usings in order to build full interface name if needed
        string[] usings = GetUsings(classDeclarationSyntax.SyntaxTree.GetRoot());
        string[] interfacesNames = GetInterfacesNames(classDeclarationSyntax.AttributeLists);

        List<InterfaceModel> ret = [];
        foreach (string interfaceName in interfacesNames)
        {
            INamedTypeSymbol? interfaceSymbol = GetInterfaceSymbol(compilation, usings, interfaceName);
            if (interfaceSymbol is null)
                continue;

            //Get interface usings, in order to be sure to have the needed usings for the properties that will be generated
            string[] interfaceUsings = interfaceSymbol
                .DeclaringSyntaxReferences
                .FirstOrDefault()?
                .GetSyntax()
                .SyntaxTree
                .GetRoot()
                .DescendantNodes()
                .OfType<UsingDirectiveSyntax>()
                .Select(x => x.ToString())
                .ToArray() ?? [];

            IPropertySymbol[] interfaceProperties = interfaceSymbol
                .GetMembers()
                .OfType<IPropertySymbol>()
                .ToArray();

            InterfacePropertyModel[] propertyModels = interfaceProperties
                .Select(interfaceProperty =>
                {
                    /*Using "interfaceProperty.Type" instead of "interfaceProperty.Type.Name" in order to not have error in specific cases
                    Es. the type "int" has Name "Int32", but writing "Int32" casue compilation error if we are not using "System" namespace*/
                    string type = interfaceProperty.Type.ToString();
                    return new InterfacePropertyModel(type, interfaceProperty.Name, interfaceProperty.SetMethod is not null);
                })
                .ToArray();

            INamespaceSymbol containingNamespace = interfaceSymbol.ContainingNamespace;
            ret.Add(new InterfaceModel(interfaceName, containingNamespace.ToString(), interfaceUsings, propertyModels));
        }

        return [.. ret];
    }
    private static string[] GetUsings(SyntaxNode root)
    {
        return root.DescendantNodes()
                .OfType<UsingDirectiveSyntax>()
                .Select(usingDirective =>
                {
                    //get string 'System' from string 'using System;'
                    string usingName = usingDirective.ToString().Split(' ').Last().TrimEnd(';');
                    return usingName;
                })
                .ToArray();
    }
    private static string[] GetInterfacesNames(SyntaxList<AttributeListSyntax> attributeLists)
    {
        foreach (AttributeListSyntax attributeList in attributeLists)
        {
            foreach (AttributeSyntax attribute in attributeList.Attributes)
            {
                if (!attribute.Name.ToString().Equals(AutoImplementAttributeName) || attribute.ArgumentList is null)
                    continue;

                return attribute.ArgumentList.Arguments
                    .Select(x => GetInterfaceName(x.Expression.ToString()))
                    .ToArray();
            }
        }

        return [];
    }
    private static string GetInterfaceName(string expression)
    {
        // Empty String
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        // Interface Name got by nameof()
        if (expression.StartsWith(NameOfString))
            return expression.Substring(NameOfString.Length, expression.Length - NameOfString.Length - 1);

        //Trimming ""
        string ret = expression.Trim('"');
        return ret;
    }
    private static INamedTypeSymbol? GetInterfaceSymbol(Compilation compilation, string[] usings, string implementedInterfaceName)
    {
        INamedTypeSymbol? interfaceSymbol = compilation.GetTypeByMetadataName(implementedInterfaceName);
        if (interfaceSymbol is not null)
            return interfaceSymbol;

        foreach (string usingString in usings)
        {
            interfaceSymbol = compilation.GetTypeByMetadataName($"{usingString}.{implementedInterfaceName}");
            if (interfaceSymbol is not null)
                return interfaceSymbol;
        }

        return null;
    }
    private static string GetClassNameSpace(SyntaxNode? parent)
    {
        return parent is NamespaceDeclarationSyntax namespaceDeclarationSyntax
            ? namespaceDeclarationSyntax.Name.ToString()
            : parent is FileScopedNamespaceDeclarationSyntax fileScopedNamespaceDeclarationSyntax
            ? fileScopedNamespaceDeclarationSyntax.Name.ToString()
            : AutoImplementAttributeNameSpace;
    }

    private record Model(string ClassNameSpace, string ClassName, InterfaceModel[] Interfaces);
    private record InterfaceModel(string Name, string NameSpace, string[] Usings, InterfacePropertyModel[] Properties);
    private record InterfacePropertyModel(string Type, string Name, bool HasSetter);

    private void Execute(SourceProductionContext context, ImmutableArray<Model> models)
    {
        foreach (Model model in models)
        {
            foreach (InterfaceModel interfaceModel in model.Interfaces)
            {
                string interfaceUsings = string.Join(string.Empty, interfaceModel.Usings);

                StringBuilder sourceBuilder = new($$"""
                    {{interfaceUsings}}
                    using {{interfaceModel.NameSpace}};

                    namespace {{model.ClassNameSpace}};

                    partial class {{model.ClassName}} : {{interfaceModel.Name}}
                    {

                    """);
                foreach (InterfacePropertyModel property in interfaceModel.Properties)
                {
                    //Check if property has a setter
                    string setter = property.HasSetter
                        ? "set; "
                        : string.Empty;

                    sourceBuilder.AppendLine($$"""
                            public {{property.Type}} {{property.Name}} { get; {{setter}}}
                        """);
                }
                sourceBuilder.AppendLine("""
                    }
                    """);

                //Concat class name and interface name to have unique file name if a class implements two interfaces with AutoImplement Attribute
                string generatedFileName = $"{model.ClassName}_{interfaceModel.Name}.g.cs";
                context.AddSource(generatedFileName, sourceBuilder.ToString());
            }
        }
    }
}