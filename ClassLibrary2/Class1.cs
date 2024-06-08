using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace ClassLibrary2;
#nullable enable
[Generator]
public class AutoImplementGenerator : IIncrementalGenerator
{
    private const string AutoImplementAttributeNameSpace = "AttributeGenerator";
    private const string AutoImplementAttributeClassName = "AutoImplementAttribute";
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(ctx =>
        {
            //Generate the AutoImplement Attribute
            const string autoImplementAttributeDeclarationCode = $$"""
using System;
namespace {{AutoImplementAttributeNameSpace}};

[AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = false)]
sealed class {{AutoImplementAttributeClassName}} : Attribute
{
    public {{AutoImplementAttributeClassName}}()
    {
    }
}
""";
            ctx.AddSource($"{AutoImplementAttributeClassName}.g.cs", autoImplementAttributeDeclarationCode);
        });

        IncrementalValuesProvider<Model> provider = context.SyntaxProvider.CreateSyntaxProvider(
            static (node, cancellationToken_) => node is ClassDeclarationSyntax classDeclarationSyntax && classDeclarationSyntax.BaseList is not null && classDeclarationSyntax.BaseList.Types.Count > 0,
            static (ctx, cancellationToken) =>
            {
                ClassDeclarationSyntax classDeclarationSyntax = (ClassDeclarationSyntax)ctx.Node;
                return new Model(
                    classDeclarationSyntax.BaseList!.Types,
                    classDeclarationSyntax.Parent,
                    classDeclarationSyntax.Identifier.ValueText,
                    classDeclarationSyntax.SyntaxTree.GetRoot()
                    );
            }).Where(m => m is not null);

        IncrementalValueProvider<ImmutableArray<Model>> collection = provider.Collect();
        IncrementalValueProvider<(Compilation Left, ImmutableArray<Model> Right)> compilation = context.CompilationProvider.Combine(collection);

        context.RegisterSourceOutput(compilation, Execute);
    }

    private record Model(SeparatedSyntaxList<BaseTypeSyntax> Types, SyntaxNode? Parent, string ClassName, SyntaxNode Root);
    private void Execute(SourceProductionContext context, (Compilation Left, ImmutableArray<Model> Right) tuple)
    {
        foreach (Model model in tuple.Right)
        {
            List<string> implementedInterfacesNames = GetImplementedInterfacesNames(model);
            if (implementedInterfacesNames.Count == 0)
                continue;

            string classNameSpace = GetClassNameSpace(model);

            foreach (string implementedInterfaceName in implementedInterfacesNames)
            {
                INamedTypeSymbol? interfaceSymbol = GetInterfaceSymbol(tuple.Left, model, implementedInterfaceName);
                if (interfaceSymbol is null)
                    continue;

                //Skip if interface doesn't have the AutoImplement Attribute
                if (!interfaceSymbol
                    .GetAttributes()
                    .Select(attribute => attribute.AttributeClass?.Name)
                    .Contains(AutoImplementAttributeClassName))
                    continue;

                //Get interface usings, in order to be sure to have the needed usings for the properties that will be generated
                string interfaceUsings = string.Join(string.Empty, interfaceSymbol
                    .DeclaringSyntaxReferences
                    .FirstOrDefault()?
                    .GetSyntax()
                    .SyntaxTree
                    .GetRoot()
                    .DescendantNodes()
                    .OfType<UsingDirectiveSyntax>()
                    .Select(x => x.ToString()));

                IPropertySymbol[] interfaceProperties = interfaceSymbol
                    .GetMembers()
                    .OfType<IPropertySymbol>()
                    .ToArray();

                StringBuilder sourceBuilder = new($$"""
                    {{interfaceUsings}}

                    namespace {{classNameSpace}};

                    partial class {{model.ClassName}}
                    {

                    """);
                foreach (IPropertySymbol interfaceProperty in interfaceProperties)
                {
                    //Check if property has a setter
                    string setter = interfaceProperty.SetMethod is null
                        ? string.Empty
                        : "set; ";

                    /*Using "interfaceProperty.Type" instead of "interfaceProperty.Type.Name" in order to not have error in specific cases
                    Es. the type "int" has Name "Int32", but writing "Int32" casue compilation error if we are not using "System" namespace*/
                    sourceBuilder.AppendLine($$"""
                            public {{interfaceProperty.Type}} {{interfaceProperty.Name}} { get; {{setter}}}
                        """);
                }
                sourceBuilder.AppendLine("""
                    }
                    """);

                //Concat class name and interface name to have unique file name if a class implements two interfaces with AutoImplement Attribute
                string generatedFileName = $"{model.ClassName}_{implementedInterfaceName}.g.cs";
                context.AddSource(generatedFileName, sourceBuilder.ToString());
            }
        }
    }
    private static List<string> GetImplementedInterfacesNames(Model model)
    {
        List<string> implementedInterfacesNames = [];
        foreach (BaseTypeSyntax typeSintax in model.Types)
        {
            TypeSyntax type = typeSintax.Type;
            if (type is IdentifierNameSyntax identifierNameSyntax)
                implementedInterfacesNames.Add(identifierNameSyntax.Identifier.ValueText);
            else if (type is QualifiedNameSyntax qualifiedNameSyntax)
                implementedInterfacesNames.Add(qualifiedNameSyntax.ToString());
        }

        return implementedInterfacesNames;
    }
    private static string GetClassNameSpace(Model model)
    {
        return model.Parent is NamespaceDeclarationSyntax namespaceDeclarationSyntax
            ? namespaceDeclarationSyntax.Name.ToString()
            : model.Parent is FileScopedNamespaceDeclarationSyntax fileScopedNamespaceDeclarationSyntax
            ? fileScopedNamespaceDeclarationSyntax.Name.ToString()
            : AutoImplementAttributeNameSpace;
    }
    private static INamedTypeSymbol? GetInterfaceSymbol(Compilation compilation, Model model, string implementedInterfaceName)
    {
        INamedTypeSymbol? interfaceSymbol = compilation.GetTypeByMetadataName(implementedInterfaceName);
        if (interfaceSymbol is null)
        {
            //Get class usings in order to build full interface name if needed
            IEnumerable<UsingDirectiveSyntax> usingDirectives = model.Root
                .DescendantNodes()
                .OfType<UsingDirectiveSyntax>();
            foreach (UsingDirectiveSyntax usingDirective in usingDirectives)
            {
                //get string 'System' from string 'using System;'
                string usingString = usingDirective.ToString().Split(' ').Last().TrimEnd(';');
                interfaceSymbol = compilation.GetTypeByMetadataName($"{usingString}.{implementedInterfaceName}");
                if (interfaceSymbol is not null)
                    break;
            }
        }

        return interfaceSymbol;
    }
}