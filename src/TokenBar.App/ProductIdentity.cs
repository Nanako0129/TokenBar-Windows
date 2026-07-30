using System.Reflection;

namespace TokenBar.App;

internal static class ProductIdentity
{
    internal static string Name { get; } = GetName();

    private static string GetName()
    {
        var product = typeof(ProductIdentity).Assembly
            .GetCustomAttribute<AssemblyProductAttribute>()?.Product;

        return string.IsNullOrWhiteSpace(product)
            ? throw new InvalidOperationException("Assembly product identity is missing.")
            : product;
    }
}
