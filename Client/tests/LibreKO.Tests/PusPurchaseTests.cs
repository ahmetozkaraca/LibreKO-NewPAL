using LibreKO.Domain;
using Xunit;

namespace LibreKO.Tests;

public class PusPurchaseTests
{
    private const int Potion = 7;
    private const int Scroll = 9;
    private const int Overflow = 10;

    [Fact]
    public void EachBasketLineBecomesOneRequest()
    {
        var plan = PusPurchase.Plan([new PusPurchaseLine(Potion, 3), new PusPurchaseLine(Scroll, 1)]);

        Assert.Equal([new PusPurchaseLine(Potion, 3), new PusPurchaseLine(Scroll, 1)], plan);
    }

    [Fact]
    public void ALineAboveTheWireCountIsSplitSoNothingIsLost()
    {
        var plan = PusPurchase.Plan([new PusPurchaseLine(Potion, PusPurchase.MaxCountPerRequest + Overflow)]);

        Assert.Equal(
            [new PusPurchaseLine(Potion, PusPurchase.MaxCountPerRequest), new PusPurchaseLine(Potion, Overflow)],
            plan);
    }
}
