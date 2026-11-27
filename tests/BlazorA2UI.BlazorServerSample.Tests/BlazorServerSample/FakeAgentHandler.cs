using System.Net;
using System.Text;

namespace BlazorA2UI.Tests.BlazorServerSample;

/// <summary>
/// An agent in memory: it answers every request with the same response and records the requests' bodies.
/// </summary>
/// <param name="respond">Builds the response.</param>
internal sealed class FakeAgentHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
{
    /// <summary>
    /// Gets the bodies of the requests, in order.
    /// </summary>
    public List<string> Requests { get; } = [];

    /// <summary>
    /// Builds a server-sent events answer: some text, then A2UI messages.
    /// </summary>
    /// <returns>The response.</returns>
    /// <param name="text">The agent's text.</param>
    /// <param name="messages">The messages, each a JSON line.</param>
    public static HttpResponseMessage EventStream(string text, params string[] messages)
    {
        StringBuilder body = new();
        body.Append("event: text\ndata: ").Append(new JsonObject { ["text"] = text }.ToJsonString()).Append("\n\n");
        foreach (string message in messages)
        {
            body.Append("event: a2ui\ndata: ").Append(message).Append("\n\n");
        }

        HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new StringContent(body.ToString(), Encoding.UTF8, "text/event-stream") };
        response.Headers.Add(A2UIAgentClient.ConversationHeader, "conv1");
        return response;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Every request the client sends has a body.
        string body = await request.Content!.ReadAsStringAsync(cancellationToken);
        lock (Requests)
        {
            Requests.Add(body);
        }

        return respond();
    }
}
