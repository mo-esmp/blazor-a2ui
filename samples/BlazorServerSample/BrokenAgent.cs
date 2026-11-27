using System.Text;
using Microsoft.AspNetCore.Diagnostics;

namespace BlazorA2UI.BlazorServerSample;

/// <summary>
/// A pretend agent whose answers break, so the Errors page can show how the client reports a broken stream. It
/// starts an answer and then either stops in the middle of a message or drops the connection.
/// </summary>
internal static class BrokenAgent
{
    /// <summary>
    /// The route, with the kind of break as its last segment.
    /// </summary>
    public const string Route = "/broken-agent/{mode}";

    /// <summary>
    /// The id of the surface the answer creates before it breaks.
    /// </summary>
    public const string SurfaceId = "broken";

    /// <summary>
    /// The break that ends the answer in the middle of a message.
    /// </summary>
    public const string Cut = "cut";

    /// <summary>
    /// The break that drops the connection.
    /// </summary>
    public const string Drop = "drop";

    /// <summary>
    /// The first, complete line of every answer.
    /// </summary>
    private static readonly byte[] s_firstLine = Encoding.UTF8.GetBytes(
        """{"version":"1.0","createSurface":{"surfaceId":"broken","catalogId":"https://a2ui.org/specification/v1_0/catalogs/basic/catalog.json","components":[{"id":"root","component":"Text","text":"The first part of the answer arrived. The rest never will."}]}}""" + "\n");

    /// <summary>
    /// The start of a message the answer never finishes.
    /// </summary>
    private static readonly byte[] s_partialLine = Encoding.UTF8.GetBytes("""{"version":"1.0","updateComponents":{"surfaceId":"broken","comp""");

    /// <summary>
    /// Answers a request: a complete message, half of the next, then the break.
    /// </summary>
    /// <returns>A task that completes when the answer ends.</returns>
    /// <param name="context">The request.</param>
    /// <param name="mode"><see cref="Cut"/> or <see cref="Drop"/>.</param>
    public static async Task HandleAsync(HttpContext context, string mode)
    {
        if (mode is not (Cut or Drop))
        {
            // A plain 404 for the client, not the app's not-found page.
            if (context.Features.Get<IStatusCodePagesFeature>() is { } pages)
            {
                pages.Enabled = false;
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = "application/jsonl";
        await context.Response.Body.WriteAsync(s_firstLine, context.RequestAborted);
        await context.Response.Body.WriteAsync(s_partialLine, context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
        if (mode == Drop)
        {
            context.Abort();
        }
    }
}
