using Euro2024Rag.Web.Ingestion;

namespace Euro2024Rag.Tests.Unit;

public class CountWithRateTests
{
    [Fact]
    public void Parse_reads_count_and_percent()
    {
        Assert.Equal(new CountWithRate(19, 68), CountWithRate.Parse("19(68%)"));
        Assert.Equal(new CountWithRate(5, 100), CountWithRate.Parse(" 5 ( 100% ) "));
        Assert.Throws<FormatException>(() => CountWithRate.Parse("abc"));
    }
}
