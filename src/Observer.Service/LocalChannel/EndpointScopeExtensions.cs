namespace Observer.Service.LocalChannel;

/// <summary>Marker: this endpoint accepts the local channel only.</summary>
/// <remarks>
/// An empty class and not an attribute, because minimal-API endpoints are marked with
/// metadata and not with attributes on methods.
/// </remarks>
public sealed class LocalOnlyMetadata;

/// <summary>How an endpoint's scope is declared and how it is read.</summary>
public static class EndpointScopeExtensions
{
    /// <summary>Declares that this endpoint exists only for the local channel.</summary>
    /// <typeparam name="TBuilder">The type of the route builder.</typeparam>
    /// <param name="builder">The endpoint or the route group to mark.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    /// It works on a route group too, and that is the intended way to use it: the pairing
    /// endpoints will be born together and are to be marked once, not one by one.
    /// <para>
    /// IT HIDES A ROUTE ONLY FROM THE VERB IT WAS MAPPED WITH, and neither marking the group nor
    /// marking each endpoint changes that. A request matching the PATH but not the METHOD selects
    /// a synthetic rejection endpoint whose metadata is empty, so there is no marker to read:
    /// <see cref="ScopeOf"/> answers Anywhere and a caller from the network gets a 405 carrying
    /// <c>Allow</c>, where every sibling path answers 404. Measured, twice, on the real pipeline.
    /// A route that must not be discoverable therefore has to be mapped for ALL verbs, with the
    /// method checked inside the handler - see <c>CredentialEndpoints</c>, which does exactly
    /// that and says why. The pairing endpoints this remark anticipates will need the same.
    /// </para>
    /// </remarks>
    public static TBuilder LocalOnly<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithMetadata(new LocalOnlyMetadata());

        return builder;
    }

    /// <summary>The scope of the endpoint that is serving this request.</summary>
    /// <param name="context">The request in progress.</param>
    /// <returns>The declared scope, or <see cref="EndpointScope.Anywhere"/>.</returns>
    /// <remarks>
    /// It returns Anywhere when the marker is missing, that is, the restriction is OPT-IN: it is
    /// the behaviour of every existing endpoint, and it does not require touching them.
    /// <para>
    /// It requires the middleware to run AFTER UseRouting: before that, GetEndpoint() is null and
    /// every endpoint would come out Anywhere, that is, the restriction would vanish silently.
    /// </para>
    /// </remarks>
    public static EndpointScope ScopeOf(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.GetEndpoint()?.Metadata.GetMetadata<LocalOnlyMetadata>() is null
            ? EndpointScope.Anywhere
            : EndpointScope.LocalOnly;
    }
}