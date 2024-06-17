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
    private const string AutoImplementAttributeName = "AutoImplementProperties";
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
            predicate: static (node, cancellationToken_) => node is ClassDeclarationSyntax,
            transform: static (ctx, cancellationToken) =>
            {
                ClassDeclarationSyntax classDeclarationSyntax = (ClassDeclarationSyntax)ctx.TargetNode;
                
                string className = classDeclarationSyntax.Identifier.ValueText;
                string classNameSpace = GetClassNameSpace(classDeclarationSyntax.Parent);
                InterfaceModel[] interfaces = GetInterfaceModels(ctx.SemanticModel.Compilation, classDeclarationSyntax);

                return new Model(
                    className,
                    classNameSpace,
                    interfaces
                    );
            }).Where(m => m is not null);

        context.RegisterSourceOutput(provider, Execute);
    }
    private static string GetClassNameSpace(SyntaxNode? parent)
    {
        return parent is NamespaceDeclarationSyntax namespaceDeclarationSyntax
            ? namespaceDeclarationSyntax.Name.ToString()
            : parent is FileScopedNamespaceDeclarationSyntax fileScopedNamespaceDeclarationSyntax
            ? fileScopedNamespaceDeclarationSyntax.Name.ToString()
            : AutoImplementAttributeNameSpace;
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

            InterfacePropertyModel[] propertyModels = interfaceSymbol
                .GetMembers()
                .OfType<IPropertySymbol>()
                .Select(interfaceProperty =>
                {
                    string type = interfaceProperty.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    return new InterfacePropertyModel(type, interfaceProperty.Name, interfaceProperty.SetMethod is not null);
                })
                .ToArray();

            string containingNamespace = interfaceSymbol.ContainingNamespace.ToString();

            ret.Add(new InterfaceModel(interfaceName, containingNamespace, propertyModels));
        }

        return [.. ret];
    }
    private static string[] GetUsings(SyntaxNode root)
    {
        return root.DescendantNodes()
                .OfType<UsingDirectiveSyntax>()
                .Select(usingDirective =>
                {
                    //get string "System" from string "using System;"
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
    private static INamedTypeSymbol? GetInterfaceSymbol(Compilation compilation, string[] nameSpaces, string implementedInterfaceName)
    {
        // Try Get without NameSpace if interface was already Fully Qualified
        INamedTypeSymbol? interfaceSymbol = compilation.GetTypeByMetadataName(implementedInterfaceName);
        if (interfaceSymbol is not null)
            return interfaceSymbol;

        foreach (string nameSpace in nameSpaces)
        {
            interfaceSymbol = compilation.GetTypeByMetadataName($"{nameSpace}.{implementedInterfaceName}");
            if (interfaceSymbol is not null)
                return interfaceSymbol;
        }

        return null;
    }

    private record Model(string ClassName, string ClassNameSpace, InterfaceModel[] Interfaces);
    private record InterfaceModel(string Name, string NameSpace, InterfacePropertyModel[] Properties);
    private record InterfacePropertyModel(string Type, string Name, bool HasSetter);

    private void Execute(SourceProductionContext context, Model model)
    {
        foreach (InterfaceModel interfaceModel in model.Interfaces)
        {
            StringBuilder sourceBuilder = new($$"""
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