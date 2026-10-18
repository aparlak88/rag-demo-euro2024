using Euro2024Rag.Web.Rag;

namespace Euro2024Rag.Tests.Unit;

public class KnowledgeChunkTests
{
    [Fact]
    public void CreateKey_is_stable_and_distinct_per_id()
    {
        Assert.Equal(KnowledgeChunk.CreateKey("match-51"), KnowledgeChunk.CreateKey("match-51"));
        Assert.NotEqual(KnowledgeChunk.CreateKey("match-50"), KnowledgeChunk.CreateKey("match-51"));
    }

    [Fact]
    public void CreateKey_sets_version_8_and_rfc_variant_bits()
    {
        var bytes = KnowledgeChunk.CreateKey("match-51").ToByteArray(bigEndian: true);

        Assert.Equal(8, bytes[6] >> 4);
        Assert.Equal(0x80, bytes[8] & 0xC0);
    }
}
