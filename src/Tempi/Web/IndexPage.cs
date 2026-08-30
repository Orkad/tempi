using Microsoft.AspNetCore.Http;

namespace Tempi.Web;

/// <summary>
/// Interface web, embarquée dans l'assembly.
/// </summary>
/// <remarks>
/// Ressource embarquée et non fichier sur disque : le binaire livré est
/// self-contained, un fichier à côté trahirait cette promesse. Les 24 Ko sont
/// chargés une fois et servis sans allocation par requête.
/// </remarks>
internal static class IndexPage
{
    private const string ResourceName = "tempi.index.html";

    private static readonly byte[] Html = Load();

    private static byte[] Load()
    {
        // Le nom logique est fixé dans le csproj : sans cela il dépendrait de
        // l'arborescence, et un déplacement de fichier casserait la résolution à
        // l'exécution seulement.
        using var stream = typeof(IndexPage).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"ressource {ResourceName} absente de l'assembly");

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public static Task Write(HttpContext context)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength = Html.Length;

        return context.Request.Method == HttpMethods.Head
            ? Task.CompletedTask
            : context.Response.Body.WriteAsync(Html).AsTask();
    }
}
