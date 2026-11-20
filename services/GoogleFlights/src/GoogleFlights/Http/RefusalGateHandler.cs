namespace Multiplex.Sites.GoogleFlights.Http;

/// <summary>
/// Checks the refusal cool-down again on every attempt, after the request has waited its turn under the pacing
/// and before it reaches the network: a request that queued before a refusal must not go out after it.
/// </summary>
internal sealed class RefusalGateHandler(RefusalGate gate) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        gate.ThrowIfCoolingDown();
        return base.SendAsync(request, cancellationToken);
    }
}
