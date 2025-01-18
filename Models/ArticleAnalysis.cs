
public record ArticleInput(string Content, string? Title = null);

public record ArticleAnalysis
{
    public List<Entity> ExtractedEntities { get; init; } = new();
    public List<SimilarArticle> SimilarArticles { get; init; } = new();
    public double ConsistencyScore { get; init; }
}

public record Entity(string Text, string Type, double Confidence);

public record SimilarArticle(string Title, string Url, double SimilarityScore);