using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Article Analysis API", Version = "v1" });
});
builder.Services.AddHttpClient<NlpService>();
builder.Services.AddScoped<NlpService>();
builder.Services.AddLogging(logging =>
{
    logging.AddConsole();
    logging.AddDebug();
});

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.MapPost("/api/nlp/corroborate", async (NlpService nlpService, ArticleInput input) =>
{
    try
    {
        var analysis = await nlpService.AnalyzeArticleAsync(input);
        return Results.Ok(analysis);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Article Analysis Failed",
            detail: ex.Message,
            statusCode: StatusCodes.Status500InternalServerError
        );
    }
})
.WithName("CorroborateArticle")
.WithOpenApi();

app.MapPost("/api/nlp/rating", async (NlpService nlpService, string input) =>
{
    try
    {
        var response = nlpService.GetSourceRating(input);
        return Results.Ok(response);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Article Analysis Failed",
            detail: ex.Message,
            statusCode: StatusCodes.Status500InternalServerError
            );
    }
})
    .WithName("ArticleRating")
    .WithOpenApi();

app.Run();