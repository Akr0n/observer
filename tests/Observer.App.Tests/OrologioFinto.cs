namespace Observer.App.Tests;

/// <summary>
/// Un orologio che avanza solo quando il test lo dice.
/// </summary>
/// <remarks>
/// I tick stanno in un <c>long</c> letto e scritto con <see cref="Volatile"/>: il ciclo di
/// aggiornamento gira su un thread del pool, il test avanza l'orologio dal proprio, e una
/// struttura da sedici byte si potrebbe leggere a meta' scrittura.
/// </remarks>
internal sealed class OrologioFinto
{
    private long istante = new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero).UtcTicks;

    public DateTimeOffset Adesso() => new(Volatile.Read(ref istante), TimeSpan.Zero);

    public void Avanza(TimeSpan quanto) =>
        Volatile.Write(ref istante, Volatile.Read(ref istante) + quanto.Ticks);
}