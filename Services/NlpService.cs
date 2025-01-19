using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CsvHelper;
using System.Linq;
using FuzzySharp;

public class NlpService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NlpService> _logger;
    private const string HF_API_URL = "https://api-inference.huggingface.co/models/";
    private const string NEWS_API_URL = "https://newsapi.org/v2/everything";
    private const string NER_MODEL = "dslim/bert-base-NER";
    private const string SIMILARITY_MODEL = "sentence-transformers/all-MiniLM-L6-v2";
    private static List<AllSidesRating> AllSidesDataset;

    public NlpService(HttpClient httpClient, IConfiguration configuration, ILogger<NlpService> logger)
    {
        AllSidesDataset = LoadAllSidesDataset();
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", configuration["HuggingFace:ApiKey"]);
    }

    public async Task<ArticleAnalysis> AnalyzeArticleAsync(ArticleInput input)
    {
        var entities = await ExtractEntitiesAsync(input.Content);
        var similarArticles = await SearchNewsArticlesAsync(entities);
        var consistencyScore = await CalculateConsistencyScore(input.Content, similarArticles);

        return new ArticleAnalysis
        {
            ExtractedEntities = entities,
            SimilarArticles = similarArticles,
            ConsistencyScore = consistencyScore
        };
    }

    private async Task<List<Entity>> ExtractEntitiesAsync(string text)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{HF_API_URL}{NER_MODEL}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _configuration["HuggingFace:ApiKey"]);

            var payload = new { inputs = text };
            var jsonContent = JsonSerializer.Serialize(payload);
            request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            _logger.LogInformation("Sending NER request to Hugging Face API: {Model} with payload: {Payload}", NER_MODEL, jsonContent);

            var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogError("Hugging Face API error: Status {StatusCode} - Response: {Error}",
                    response.StatusCode, error);
                throw new Exception($"Hugging Face API error ({response.StatusCode}): {error}");
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            _logger.LogInformation("Received response: {Response}", responseContent);

            return ParseEntitiesResponse(responseContent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract entities from text");
            throw;
        }
    }

    private List<Entity> ParseEntitiesResponse(string jsonString)
    {
        var entities = new List<Entity>();
        var jsonDoc = JsonDocument.Parse(jsonString);
        const double CONFIDENCE_THRESHOLD = 0.60; // 60% confid

        if (jsonDoc.RootElement.ValueKind != JsonValueKind.Array)
        {
            _logger.LogError("Unexpected response format: {Response}", jsonString);
            throw new Exception("Unexpected response format from Hugging Face API");
        }

        foreach (var item in jsonDoc.RootElement.EnumerateArray())
        {
            try
            {
                var entityGroup = item.GetProperty("entity_group").GetString();
                var word = item.GetProperty("word").GetString();
                var score = item.GetProperty("score").GetDouble();

                if (!string.IsNullOrEmpty(entityGroup) && !string.IsNullOrEmpty(word) && score >= CONFIDENCE_THRESHOLD)
                {
                    _logger.LogInformation("Adding entity: {Word} ({Type}) with confidence {Score}", word, entityGroup, score);
                    entities.Add(new Entity(word, entityGroup, score));
                }
                else
                {
                    _logger.LogDebug("Skipping entity {Word} due to low confidence ({Score})", word, score);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing entity item: {Item}", item.ToString());
            }
        }

        return entities;
    }

    private async Task<List<SimilarArticle>> SearchNewsArticlesAsync(List<Entity> entities)
    {
        try
        {
            var searchTerms = string.Join(" OR ", entities.Select(e => e.Text));
            var newsApiKey = _configuration["NewsApi:ApiKey"];
            var url = $"{NEWS_API_URL}?q={Uri.EscapeDataString(searchTerms)}&apiKey={newsApiKey}&language=en&sortBy=relevancy&pageSize=5";

            _logger.LogInformation("Searching NewsAPI with terms: {SearchTerms}", searchTerms);

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "FaussesNouvelles/1.0");

            var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogError("NewsAPI error: {StatusCode} - {Error}",
                    response.StatusCode, error);
                throw new Exception($"NewsAPI error: {error}");
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            _logger.LogInformation("Received NewsAPI response: {Response}", responseContent);

            var newsResponse = JsonSerializer.Deserialize<NewsApiResponse>(responseContent);
            var similarArticles = new List<SimilarArticle>();

            foreach (var article in newsResponse?.articles ?? Enumerable.Empty<NewsArticle>())
            {
                if (string.IsNullOrEmpty(article.description)) continue;

                _logger.LogInformation("Processing article: {Title}\nSource: {Source}\nDescription: {Description}",
                    article.title, article.source?.name, article.description);

                var similarityScore = await CalculateSimilarityScore(article.description, article.description);
                if (similarityScore > 0.3) // Only include articles with meaningful similarity
                {
                    similarArticles.Add(new SimilarArticle(
                        article.title ?? "Untitled",
                        article.url ?? "",
                        article.source?.name ?? "Unknown Source",
                        similarityScore
                    ));
                }
            }

            return similarArticles.OrderByDescending(x => x.SimilarityScore).Take(5).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search news articles");
            return new List<SimilarArticle>();
        }
    }

    private async Task<double> CalculateSimilarityScore(string text1, string text2)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{HF_API_URL}{SIMILARITY_MODEL}");

            var payload = new
            {
                inputs = new
                {
                    source_sentence = text1,
                    sentences = new[] { text2 }
                }
            };

            request.Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            );

            _logger.LogInformation("Calculating similarity between texts");
            var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogError("Similarity calculation failed: {Error}", error);
                return 0;
            }

            var scores = JsonSerializer.Deserialize<double[]>(
                await response.Content.ReadAsStringAsync()
            );
            return scores?[0] ?? 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating similarity score");
            return 0;
        }
    }

    private async Task<double> CalculateConsistencyScore(string originalText, List<SimilarArticle> articles)
    {
        if (!articles.Any()) return 0;
        return articles.Count(a => a.SimilarityScore >= 0.8) / (double)articles.Count;
    }

    /*private static int LevenshteinDistance(string s, string t) {
        // Special cases
        if (s == t) return 0;
        if (s.Length == 0) return t.Length;
        if (t.Length == 0) return s.Length;
        // Initialize the distance matrix
        int[, ] distance = new int[s.Length + 1, t.Length + 1];
        for (int i = 0; i <= s.Length; i++) distance[i, 0] = i;
        for (int j = 0; j <= t.Length; j++) distance[0, j] = j;
        // Calculate the distance
        for (int i = 1; i <= s.Length; i++) {
            for (int j = 1; j <= t.Length; j++) {
                int cost = (s[i - 1] == t[j - 1]) ? 0 : 1;
                distance[i, j] = Math.Min(Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1), distance[i - 1, j - 1] + cost);
            }
        }
        // Return the distance
        return distance[s.Length, t.Length];
    }*/

    public List<Tuple<AllSidesRating, int>> GetSourceRating(string sourceInput)
    {
        int maxDistance = 70;

        // fuzzy find the closest search to given source
        var matches = from rating in AllSidesDataset
                      let distance = Fuzz.PartialRatio(rating.news_source, sourceInput)
                      where distance >= maxDistance
                      select new Tuple<AllSidesRating, int>(rating, distance);
        var matchesList = matches.OrderBy(x => x.Item2).ToList();
        matchesList.Reverse();

        try
        {
            return matchesList[..5];
        }
        catch (Exception e)
        {
            return matchesList;
        }
    }

    public List<AllSidesRating> LoadAllSidesDataset()
    {
        using (var reader = new StreamReader("./datasets/allsides_data.csv"))
        using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
        {
            // Read the records into a list
            return csv.GetRecords<AllSidesRating>().ToList();
        }
    }

    public async Task<CohereResponse> ClassifyArticleAsync(string content)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.cohere.com/v1/classify");

            var payload = new
            {
                model = "6232087b-d2f6-4e96-8b04-dc5f2e4de918-ft",
                inputs = new[] { content }
            };

            request.Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            );

            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                _configuration["CohereApi:ApiKey"]
            );

            _logger.LogInformation("Sending classification request to Cohere API");
            var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogError("Cohere API error: {StatusCode} - {Error}",
                    response.StatusCode, error);
                throw new Exception($"Cohere API error: {error}");
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            _logger.LogInformation("Received Cohere response: {Response}", jsonResponse);

            var cohereResponse = JsonSerializer.Deserialize<CohereApiResponse>(jsonResponse);
            var classification = cohereResponse?.classifications.FirstOrDefault();

            return new CohereResponse
            {
                Prediction = classification?.prediction ?? "unknown",
                Confidences = classification?.labels.ToDictionary(
                    x => x.Key,
                    x => x.Value.confidence
                ) ?? new Dictionary<string, double>()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to classify article");
            throw;
        }
    }
}

public class NewsApiResponse
{
    public string status { get; set; }
    public int totalResults { get; set; }
    public List<NewsArticle> articles { get; set; } = new();
}

public class NewsArticle
{
    public NewsSource source { get; set; }
    public string author { get; set; }
    public string title { get; set; }
    public string description { get; set; }
    public string url { get; set; }
    public string urlToImage { get; set; }
    public string publishedAt { get; set; }
    public string content { get; set; }
}

public class NewsSource
{
    public string id { get; set; }
    public string name { get; set; }
}

public class AllSidesRating
{
    public string news_source { get; set; }
    public string rating { get; set; }
    public string rating_num { get; set; }
    public string type { get; set; }
    public string agree { get; set; }
    public string disagree { get; set; }
    public string perc_agree { get; set; }
    public string url { get; set; }
    public string editorial_review { get; set; }
    public string blind_survey { get; set; }
    public string third_party_analysis { get; set; }
    public string independent_research { get; set; }
    public string confidence_level { get; set; }
    public string twitter { get; set; }
    public string wiki { get; set; }
    public string facebook { get; set; }
    public string screen_name { get; set; }
}

public class CohereApiResponse
{
    public List<CohereClassification> classifications { get; set; }
}

public class CohereClassification
{
    public string prediction { get; set; }
    public Dictionary<string, CohereLabel> labels { get; set; }
}

public class CohereLabel
{
    public double confidence { get; set; }
}