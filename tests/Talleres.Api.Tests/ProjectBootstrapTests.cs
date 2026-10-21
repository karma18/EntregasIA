using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace Talleres.Api.Tests;

/// <summary>
/// Pruebas HTTP del arranque (PROY-03 a PROY-06). Cada escenario crea y dispone su propio host.
/// </summary>
public sealed class ProjectBootstrapTests
{
    [Fact]
    public async Task GetHealth_ReturnsOkJsonWithCamelCaseStatus()
    {
        // PROY-03, PROY-04
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.True(root.TryGetProperty("status", out var status), "El JSON debe usar camelCase: status.");
        Assert.Equal("ok", status.GetString());
        Assert.Single(root.EnumerateObject());
    }

    [Fact]
    public async Task GetOpenApi_InDevelopment_DocumentsHealth()
    {
        // PROY-03, PROY-05
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json");
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        Assert.True(root.TryGetProperty("paths", out var paths), "El documento OpenAPI debe incluir paths.");
        Assert.True(paths.TryGetProperty("/health", out _), "El documento OpenAPI debe contener /health.");
    }

    [Fact]
    public async Task GetOpenApi_InProduction_ReturnsNotFoundProblemDetails()
    {
        // PROY-03, PROY-06
        await using var factory = CreateHost(Environments.Production);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json");
        var payload = await response.Content.ReadAsStringAsync();

        AssertProblemDetailsNotFound(response, payload);
        AssertDoesNotExposeStackTrace(payload);
    }

    [Fact]
    public async Task GetMissingRoute_ReturnsNotFoundProblemDetailsWithoutStackTrace()
    {
        // PROY-03, PROY-06 — Development es el entorno donde un 404 mal configurado suele filtrar traza.
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/ruta-inexistente");
        var payload = await response.Content.ReadAsStringAsync();

        AssertProblemDetailsNotFound(response, payload);
        AssertDoesNotExposeStackTrace(payload);
    }

    private static WebApplicationFactory<Program> CreateHost(string environment) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
        });

    private static void AssertProblemDetailsNotFound(HttpResponseMessage response, string payload)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.True(root.TryGetProperty("status", out var status), "ProblemDetails debe usar camelCase: status.");
        Assert.Equal(JsonValueKind.Number, status.ValueKind);
        Assert.Equal(404, status.GetInt32());
        Assert.True(root.TryGetProperty("title", out var title), "ProblemDetails debe usar camelCase: title.");
        Assert.False(string.IsNullOrWhiteSpace(title.GetString()));
    }

    private static void AssertDoesNotExposeStackTrace(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        AssertDoesNotExposeStackTrace(document.RootElement);
    }

    private static void AssertDoesNotExposeStackTrace(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Assert.DoesNotContain("stacktrace", property.Name, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("exception", property.Name, StringComparison.OrdinalIgnoreCase);
                    AssertDoesNotExposeStackTrace(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AssertDoesNotExposeStackTrace(item);
                }
                break;
            case JsonValueKind.String:
                var value = element.GetString() ?? string.Empty;
                Assert.DoesNotContain(" at ", value, StringComparison.Ordinal);
                Assert.DoesNotContain(".cs:line", value, StringComparison.OrdinalIgnoreCase);
                break;
        }
    }
}
