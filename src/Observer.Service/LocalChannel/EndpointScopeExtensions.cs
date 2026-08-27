namespace Observer.Service.LocalChannel;

/// <summary>Marcatore: questo endpoint accetta solo il canale locale.</summary>
/// <remarks>
/// Una classe vuota e non un attributo, perche' gli endpoint minimal-API si marcano con
/// metadati e non con attributi sui metodi.
/// </remarks>
public sealed class SoloDaLocaleMetadata;

/// <summary>Come si dichiara e come si legge la portata di un endpoint.</summary>
public static class EndpointScopeExtensions
{
    /// <summary>Dichiara che questo endpoint esiste solo per il canale locale.</summary>
    /// <typeparam name="TBuilder">Il tipo del costruttore di rotte.</typeparam>
    /// <param name="builder">L'endpoint o il gruppo di rotte da marcare.</param>
    /// <returns>Lo stesso costruttore, per concatenare.</returns>
    /// <remarks>
    /// Funziona anche su un gruppo di rotte, ed e' il modo previsto di usarla: gli endpoint di
    /// appaiamento nasceranno insieme e vanno marcati una volta sola, non uno per uno.
    /// </remarks>
    public static TBuilder SoloDaLocale<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithMetadata(new SoloDaLocaleMetadata());

        return builder;
    }

    /// <summary>La portata dell'endpoint che sta servendo questa richiesta.</summary>
    /// <param name="contesto">La richiesta in corso.</param>
    /// <returns>La portata dichiarata, oppure <see cref="EndpointScope.Ovunque"/>.</returns>
    /// <remarks>
    /// Restituisce Ovunque quando il marcatore manca, cioe' la restrizione e' a OPT-IN: e' il
    /// comportamento di tutti gli endpoint esistenti, e non richiede di toccarli.
    /// <para>
    /// Richiede che il middleware giri DOPO UseRouting: prima, GetEndpoint() e' null e ogni
    /// endpoint risulterebbe Ovunque, cioe' la restrizione sparirebbe in silenzio.
    /// </para>
    /// </remarks>
    public static EndpointScope PortataDi(HttpContext contesto)
    {
        ArgumentNullException.ThrowIfNull(contesto);

        return contesto.GetEndpoint()?.Metadata.GetMetadata<SoloDaLocaleMetadata>() is null
            ? EndpointScope.Ovunque
            : EndpointScope.SoloLocale;
    }
}