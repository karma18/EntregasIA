using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Talleres.Api.Features.Workshops;

namespace Talleres.Api.Tests;

/// <summary>
/// Catálogo de talleres (CAT-01 a CAT-08). Cada escenario HTTP crea y dispone su propio host.
/// </summary>
public sealed class CatalogTests
{
    private static readonly Guid SeedWorkshopId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid MissingWorkshopId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly string[] WorkshopFieldNames =
    [
        "id",
        "title",
        "capacity",
        "activeReservations",
        "availableSeats"
    ];

    [Fact]
    public async Task GetWorkshops_OnFreshHost_ReturnsSingleSeedWorkshop()
    {
        // CAT-01
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/workshops");
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal(1, root.GetArrayLength());
        AssertSeedWorkshopJson(root[0]);
    }

    [Fact]
    public async Task GetWorkshop_WithSeedId_ReturnsSameFiveFields()
    {
        // CAT-02
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/workshops/{SeedWorkshopId}");
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(payload);
        AssertSeedWorkshopJson(document.RootElement);
    }

    [Fact]
    public async Task GetWorkshop_WithUnknownGuid_ReturnsWorkshopNotFoundProblem()
    {
        // CAT-03
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/workshops/{MissingWorkshopId}");
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.True(root.TryGetProperty("status", out var status), "ProblemDetails debe usar camelCase: status.");
        Assert.Equal(404, status.GetInt32());
        Assert.True(root.TryGetProperty("code", out var code), "ProblemDetails debe incluir code.");
        Assert.Equal("workshop_not_found", code.GetString());
        Assert.True(root.TryGetProperty("title", out var title), "ProblemDetails debe usar camelCase: title.");
        Assert.Equal("Recurso no encontrado", title.GetString());
        Assert.True(root.TryGetProperty("detail", out var detail), "ProblemDetails debe usar camelCase: detail.");
        Assert.Equal("No se encontró el taller.", detail.GetString());
    }

    [Fact]
    public async Task GetWorkshop_WithNonGuidId_ReturnsNotFoundNeverServerError()
    {
        // CAT-04
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/workshops/no-es-guid");
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True((int)response.StatusCode < 500, "Un ID que no es GUID nunca debe producir 500.");
        Assert.DoesNotContain(".cs:line", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RepeatingCatalogAndDetailReads_DoesNotChangeValuesOrCreateReservation()
    {
        // CAT-05 — dos lecturas en el mismo host; consultar no crea reserva.
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var firstCatalog = await client.GetAsync("/api/workshops");
        using var secondCatalog = await client.GetAsync("/api/workshops");
        using var firstDetail = await client.GetAsync($"/api/workshops/{SeedWorkshopId}");
        using var secondDetail = await client.GetAsync($"/api/workshops/{SeedWorkshopId}");

        Assert.Equal(HttpStatusCode.OK, firstCatalog.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondCatalog.StatusCode);
        Assert.Equal(HttpStatusCode.OK, firstDetail.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondDetail.StatusCode);

        using var firstCatalogDoc = JsonDocument.Parse(await firstCatalog.Content.ReadAsStringAsync());
        using var secondCatalogDoc = JsonDocument.Parse(await secondCatalog.Content.ReadAsStringAsync());
        using var firstDetailDoc = JsonDocument.Parse(await firstDetail.Content.ReadAsStringAsync());
        using var secondDetailDoc = JsonDocument.Parse(await secondDetail.Content.ReadAsStringAsync());

        Assert.Equal(1, firstCatalogDoc.RootElement.GetArrayLength());
        Assert.Equal(1, secondCatalogDoc.RootElement.GetArrayLength());
        AssertSeedWorkshopJson(firstCatalogDoc.RootElement[0]);
        AssertSeedWorkshopJson(secondCatalogDoc.RootElement[0]);
        AssertSeedWorkshopJson(firstDetailDoc.RootElement);
        AssertSeedWorkshopJson(secondDetailDoc.RootElement);

        AssertJsonEquals(firstCatalogDoc.RootElement, secondCatalogDoc.RootElement);
        AssertJsonEquals(firstDetailDoc.RootElement, secondDetailDoc.RootElement);
        AssertJsonEquals(firstCatalogDoc.RootElement[0], firstDetailDoc.RootElement);
    }

    [Fact]
    public async Task WorkshopJson_HasExactlyFiveCamelCaseFields_WithoutPersonalData()
    {
        // CAT-06
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var catalogResponse = await client.GetAsync("/api/workshops");
        using var detailResponse = await client.GetAsync($"/api/workshops/{SeedWorkshopId}");

        using var catalogDoc = JsonDocument.Parse(await catalogResponse.Content.ReadAsStringAsync());
        using var detailDoc = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());

        AssertExactlyFiveWorkshopFields(catalogDoc.RootElement[0]);
        AssertExactlyFiveWorkshopFields(detailDoc.RootElement);
        AssertDoesNotExposePersonalData(catalogDoc.RootElement);
        AssertDoesNotExposePersonalData(detailDoc.RootElement);
    }

    [Fact]
    public void ReservationStore_AvailableSeatsEqualsCapacityMinusActiveReservations()
    {
        // CAT-07
        var store = new ReservationStore();

        var fromList = Assert.Single(store.GetWorkshops());
        var fromDetail = store.GetWorkshop(ReservationStore.SeedWorkshopId);

        Assert.Equal(SeedWorkshopId, ReservationStore.SeedWorkshopId);
        Assert.NotNull(fromDetail);
        Assert.Equal(fromList.Capacity - fromList.ActiveReservations, fromList.AvailableSeats);
        Assert.Equal(fromDetail.Capacity - fromDetail.ActiveReservations, fromDetail.AvailableSeats);
        Assert.Equal(fromList.Id, fromDetail.Id);
        Assert.Equal(fromList.Title, fromDetail.Title);
        Assert.Equal(fromList.Capacity, fromDetail.Capacity);
        Assert.Equal(fromList.ActiveReservations, fromDetail.ActiveReservations);
        Assert.Equal(fromList.AvailableSeats, fromDetail.AvailableSeats);
        Assert.Equal(2, fromList.Capacity);
        Assert.Equal(0, fromList.ActiveReservations);
        Assert.Equal(2, fromList.AvailableSeats);
    }

    [Fact]
    public void ReservationStore_GetWorkshops_ReturnsMaterializedSnapshot()
    {
        // CAT-07 — no IEnumerable perezoso; mutar la lista devuelta no cambia la siguiente lectura.
        var store = new ReservationStore();
        IReadOnlyList<WorkshopResponse> first = store.GetWorkshops();

        Assert.IsAssignableFrom<IReadOnlyList<WorkshopResponse>>(first);
        Assert.False(first is IQueryable, "GetWorkshops no debe devolver una consulta diferida.");
        Assert.False(first is IQueryable<WorkshopResponse>, "GetWorkshops no debe devolver IQueryable perezoso.");

        var firstPass = first.ToArray();
        var secondPass = first.ToArray();
        Assert.Equal(first.Count, firstPass.Length);
        Assert.Equal(firstPass, secondPass);

        TryMutateReturnedCollection(first);

        IReadOnlyList<WorkshopResponse> afterMutation = store.GetWorkshops();
        Assert.Equal(1, afterMutation.Count);
        var workshop = Assert.Single(afterMutation);
        Assert.Equal(SeedWorkshopId, workshop.Id);
        Assert.Equal("Taller de Cursor", workshop.Title);
        Assert.Equal(2, workshop.Capacity);
        Assert.Equal(0, workshop.ActiveReservations);
        Assert.Equal(workshop.Capacity - workshop.ActiveReservations, workshop.AvailableSeats);
    }

    [Fact]
    public void ReservationStore_TwoInstances_DoNotShareMutableState()
    {
        // CAT-07
        var firstStore = new ReservationStore();
        var secondStore = new ReservationStore();

        var firstList = firstStore.GetWorkshops();
        TryMutateReturnedCollection(firstList);

        Assert.Equal(1, secondStore.GetWorkshops().Count);
        Assert.Equal(1, firstStore.GetWorkshops().Count);
        Assert.Equal(SeedWorkshopId, Assert.Single(secondStore.GetWorkshops()).Id);
        Assert.Equal(SeedWorkshopId, Assert.Single(firstStore.GetWorkshops()).Id);
    }

    [Fact]
    public async Task GetOpenApi_InDevelopment_DocumentsCatalogAndDetail_AndKeepsHealth()
    {
        // CAT-08 — no sustituye ProjectBootstrapTests; comprueba catálogo y no rompe /health.
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var response = await client.GetAsync("/openapi/v1.json");
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        Assert.True(root.TryGetProperty("paths", out var paths), "El documento OpenAPI debe incluir paths.");
        Assert.True(paths.TryGetProperty("/health", out _), "OpenAPI debe seguir documentando /health.");
        Assert.True(paths.TryGetProperty("/api/workshops", out _), "OpenAPI debe documentar GET /api/workshops.");
        Assert.True(
            HasWorkshopDetailPath(paths),
            "OpenAPI debe documentar la ruta de detalle /api/workshops/{id}.");
    }

    private static WebApplicationFactory<Program> CreateHost(string environment) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
        });

    private static void AssertSeedWorkshopJson(JsonElement workshop)
    {
        Assert.Equal(JsonValueKind.Object, workshop.ValueKind);
        AssertExactlyFiveWorkshopFields(workshop);

        Assert.Equal(SeedWorkshopId, workshop.GetProperty("id").GetGuid());
        Assert.Equal("Taller de Cursor", workshop.GetProperty("title").GetString());
        Assert.Equal(2, workshop.GetProperty("capacity").GetInt32());
        Assert.Equal(0, workshop.GetProperty("activeReservations").GetInt32());
        Assert.Equal(2, workshop.GetProperty("availableSeats").GetInt32());
        Assert.Equal(
            workshop.GetProperty("capacity").GetInt32() - workshop.GetProperty("activeReservations").GetInt32(),
            workshop.GetProperty("availableSeats").GetInt32());
    }

    private static void AssertExactlyFiveWorkshopFields(JsonElement workshop)
    {
        var names = workshop.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(5, names.Length);
        Assert.Equal(WorkshopFieldNames, names);
    }

    private static void AssertDoesNotExposePersonalData(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var name = property.Name;
                    Assert.DoesNotContain("email", name, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("correo", name, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("participant", name, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("participante", name, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("attendee", name, StringComparison.OrdinalIgnoreCase);
                    Assert.False(
                        name.Equals("name", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("nombre", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("names", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("nombres", StringComparison.OrdinalIgnoreCase),
                        $"El catálogo no debe exponer datos personales: {name}.");
                    AssertDoesNotExposePersonalData(property.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AssertDoesNotExposePersonalData(item);
                }

                break;
        }
    }

    private static void AssertJsonEquals(JsonElement expected, JsonElement actual) =>
        Assert.True(JsonElement.DeepEquals(expected, actual), "Las lecturas repetidas deben conservar el mismo JSON.");

    private static void TryMutateReturnedCollection(IReadOnlyList<WorkshopResponse> workshops)
    {
        if (workshops is not IList<WorkshopResponse> list)
        {
            return;
        }

        try
        {
            list.Clear();
        }
        catch (NotSupportedException)
        {
        }
        catch (NotImplementedException)
        {
        }
    }

    private static bool HasWorkshopDetailPath(JsonElement paths)
    {
        foreach (var path in paths.EnumerateObject())
        {
            if (path.Name.StartsWith("/api/workshops/{", StringComparison.Ordinal)
                && path.Name.Contains('}', StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
