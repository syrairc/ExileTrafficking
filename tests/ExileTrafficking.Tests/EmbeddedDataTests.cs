using Xunit;

namespace ExileTrafficking.Tests;

public class EmbeddedDataTests
{
    [Fact]
    public void MercDataResourceIsEmbedded()
    {
        var asm = typeof(ExileTrafficking).Assembly;
        using var stream = asm.GetManifestResourceStream("ExileTrafficking.mercdata.json");
        Assert.NotNull(stream);
    }
}
