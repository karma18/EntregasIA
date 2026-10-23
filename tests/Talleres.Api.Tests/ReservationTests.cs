using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace Talleres.Api.Tests;

/// <summary>
/// Creación de reservas (RES-01 a RES-13 HTTP y parte HTTP de RES-14).
/// Cada escenario HTTP crea y dispone su propio host; no usa IClassFixture global.
/// </summary>
public sealed class ReservationTests
{
    private static readonly Guid SeedWorkshopId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid MissingWorkshopId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly string[] ReservationFieldNames =
    [
        "id",
        "workshopId",
        "participantName",
        "email",
        "createdAtUtc"
    ];

    [Fact]
    public async Task PostReservation_OnEmptyWorkshop_Returns201NormalizedJsonAndLocation()
    {
        // RES-01
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();
        var before = DateTimeOffset.UtcNow;

        using var response = await PostJsonAsync(
            client,
            SeedWorkshopId,
            """{"participantName":"  Ana Pérez  ","email":" ANA@EXAMPLE.COM "}""");
        var after = DateTimeOffset.UtcNow;
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(payload);
        var created = AssertCreatedReservationJson(
            document.RootElement,
            SeedWorkshopId,
            "Ana Pérez",
            "ana@example.com",
            before,
            after);
        AssertLocationMatchesReservation(response, created);
    }

    [Fact]
    public async Task PostReservation_ThenCatalogAndDetail_ShowUpdatedCounts_PreviousSnapshotUnchanged()
    {
        // RES-02
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var beforeCatalog = await client.GetAsync("/api/workshops");
        using var beforeDetail = await client.GetAsync($"/api/workshops/{SeedWorkshopId}");
        var beforeCatalogJson = await beforeCatalog.Content.ReadAsStringAsync();
        var beforeDetailJson = await beforeDetail.Content.ReadAsStringAsync();

        using var create = await PostJsonAsync(
            client,
            SeedWorkshopId,
            ReservationJson("Ana Pérez", "ana@example.com"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var afterCatalog = await client.GetAsync("/api/workshops");
        using var afterDetail = await client.GetAsync($"/api/workshops/{SeedWorkshopId}");
        using var afterCatalogDoc = JsonDocument.Parse(await afterCatalog.Content.ReadAsStringAsync());
        using var afterDetailDoc = JsonDocument.Parse(await afterDetail.Content.ReadAsStringAsync());

        using var beforeCatalogDoc = JsonDocument.Parse(beforeCatalogJson);
        using var beforeDetailDoc = JsonDocument.Parse(beforeDetailJson);
        AssertWorkshopCounts(beforeCatalogDoc.RootElement[0], active: 0, available: 2);
        AssertWorkshopCounts(beforeDetailDoc.RootElement, active: 0, available: 2);

        Assert.Equal(HttpStatusCode.OK, afterCatalog.StatusCode);
        Assert.Equal(HttpStatusCode.OK, afterDetail.StatusCode);
        AssertWorkshopCounts(afterCatalogDoc.RootElement[0], active: 1, available: 1);
        AssertWorkshopCounts(afterDetailDoc.RootElement, active: 1, available: 1);
    }

    [Theory]
    [InlineData("omit")]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("spaces")]
    public async Task PostReservation_InvalidParticipantNameAlone_Returns400WithErrorsAndDoesNotReserve(string kind)
    {
        // RES-03 — cada campo por separado; nombre omitido, null, vacío o solo espacios.
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();
        var email = $"res03-name-{kind}@example.com";
        var json = kind switch
        {
            "omit" => $$"""{"email":"{{email}}"}""",
            "null" => $$"""{"participantName":null,"email":"{{email}}"}""",
            "empty" => $$"""{"participantName":"","email":"{{email}}"}""",
            "spaces" => $$"""{"participantName":"   ","email":"{{email}}"}""",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        using var response = await PostJsonAsync(client, SeedWorkshopId, json);
        var payload = await response.Content.ReadAsStringAsync();

        AssertValidationProblem(response, payload);
        AssertHasFieldError(payload, "participantName");
        await AssertSeedWorkshopCountsAsync(client, active: 0, available: 2);
    }

    [Theory]
    [InlineData("omit")]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("spaces")]
    public async Task PostReservation_InvalidEmailAlone_Returns400WithErrorsAndDoesNotReserve(string kind)
    {
        // RES-03 — cada campo por separado; correo omitido, null, vacío o solo espacios.
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();
        var json = kind switch
        {
            "omit" => """{"participantName":"Ana Pérez"}""",
            "null" => """{"participantName":"Ana Pérez","email":null}""",
            "empty" => """{"participantName":"Ana Pérez","email":""}""",
            "spaces" => """{"participantName":"Ana Pérez","email":"   "}""",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        using var response = await PostJsonAsync(client, SeedWorkshopId, json);
        var payload = await response.Content.ReadAsStringAsync();

        AssertValidationProblem(response, payload);
        AssertHasFieldError(payload, "email");
        await AssertSeedWorkshopCountsAsync(client, active: 0, available: 2);
    }

    [Fact]
    public async Task PostReservation_NameLength1_Returns400AndDoesNotReserve()
    {
        // RES-04 — escenario independiente, longitud normalizada 1.
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var response = await PostJsonAsync(
            client,
            SeedWorkshopId,
            ReservationJson("A", "res04-len1@example.com"));
        var payload = await response.Content.ReadAsStringAsync();

        AssertValidationProblem(response, payload);
        AssertHasFieldError(payload, "participantName");
        AssertDoesNotHaveFieldError(payload, "email");
        await AssertSeedWorkshopCountsAsync(client, active: 0, available: 2);
    }

    [Fact]
    public async Task PostReservation_NameLength2_Returns201()
    {
        // RES-04 — escenario independiente, longitud normalizada 2.
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();
        var before = DateTimeOffset.UtcNow;

        using var response = await PostJsonAsync(
            client,
            SeedWorkshopId,
            ReservationJson("Li", "res04-len2@example.com"));
        var after = DateTimeOffset.UtcNow;

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertCreatedReservationJson(
            document.RootElement,
            SeedWorkshopId,
            "Li",
            "res04-len2@example.com",
            before,
            after);
        await AssertSeedWorkshopCountsAsync(client, active: 1, available: 1);
    }

    [Fact]
    public async Task PostReservation_NameLength100_Returns201()
    {
        // RES-04 — escenario independiente, longitud normalizada 100.
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();
        var name = new string('N', 100);
        var before = DateTimeOffset.UtcNow;

        using var response = await PostJsonAsync(
            client,
            SeedWorkshopId,
            ReservationJson(name, "res04-len100@example.com"));
        var after = DateTimeOffset.UtcNow;

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertCreatedReservationJson(
            document.RootElement,
            SeedWorkshopId,
            name,
            "res04-len100@example.com",
            before,
            after);
        await AssertSeedWorkshopCountsAsync(client, active: 1, available: 1);
    }

    [Fact]
    public async Task PostReservation_NameLength101_Returns400AndDoesNotReserve()
    {
        // RES-04 — escenario independiente, longitud normalizada 101.
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var response = await PostJsonAsync(
            client,
            SeedWorkshopId,
            ReservationJson(new string('N', 101), "res04-len101@example.com"));
        var payload = await response.Content.ReadAsStringAsync();

        AssertValidationProblem(response, payload);
        AssertHasFieldError(payload, "participantName");
        AssertDoesNotHaveFieldError(payload, "email");
        await AssertSeedWorkshopCountsAsync(client, active: 0, available: 2);
    }

    [Fact]
    public async Task PostReservation_EmailWithoutAtSign_Returns400IndependentOfName()
    {
        // RES-05 — formato inválido; nombre válido independiente.
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var response = await PostJsonAsync(
            client,
            SeedWorkshopId,
            ReservationJson("Ana Pérez", "sin-arroba"));
        var payload = await response.Content.ReadAsStringAsync();

        AssertValidationProblem(response, payload);
        AssertHasFieldError(payload, "email");
        AssertDoesNotHaveFieldError(payload, "participantName");
        await AssertSeedWorkshopCountsAsync(client, active: 0, available: 2);
    }

    [Fact]
    public async Task PostReservation_EmailLength254_Returns201IndependentOfName()
    {
        // RES-05 — correo sintácticamente válido de 254 caracteres; nombre corto válido.
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();
        var email = EmailOfLength(254);
        var before = DateTimeOffset.UtcNow;

        using var response = await PostJsonAsync(
            client,
            SeedWorkshopId,
            ReservationJson("Ana", email));
        var after = DateTimeOffset.UtcNow;

        Assert.Equal(254, email.Length);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertCreatedReservationJson(document.RootElement, SeedWorkshopId, "Ana", email, before, after);
        await AssertSeedWorkshopCountsAsync(client, active: 1, available: 1);
    }

    [Fact]
    public async Task PostReservation_EmailLength255_Returns400IndependentOfName()
    {
        // RES-05 — 255 caracteres fallan; nombre válido independiente.
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();
        var email = EmailOfLength(255);

        using var response = await PostJsonAsync(
            client,
            SeedWorkshopId,
            ReservationJson("Ana Pérez", email));
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(255, email.Length);
        AssertValidationProblem(response, payload);
        AssertHasFieldError(payload, "email");
        AssertDoesNotHaveFieldError(payload, "participantName");
        await AssertSeedWorkshopCountsAsync(client, active: 0, available: 2);
    }

    [Fact]
    public async Task PostReservation_NameAndEmailExceedMaxTogether_Returns400WithBothErrors()
    {
        // RES-06
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();
        var name = new string('N', 101);
        var email = EmailOfLength(255);

        using var response = await PostJsonAsync(
            client,
            SeedWorkshopId,
            ReservationJson(name, email));
        var payload = await response.Content.ReadAsStringAsync();

        AssertValidationProblem(response, payload);
        AssertHasFieldError(payload, "participantName");
        AssertHasFieldError(payload, "email");
        await AssertSeedWorkshopCountsAsync(client, active: 0, available: 2);
    }

    [Theory]
    [InlineData("{", false)]
    [InlineData("null", false)]
    [InlineData("{}", true)]
    [InlineData("""{"participantName":1,"email":"tipo@example.com"}""", false)]
    public async Task PostReservation_MalformedOrIncompatibleJson_Returns400ProblemDetailsNever500(
        string json,
        bool requireErrors)
    {
        // RES-07 — `{`, `null`, `{}` o nombre numérico; nunca 500; estado sin cambios.
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var response = await PostJsonAsync(client, SeedWorkshopId, json);
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((int)response.StatusCode < 500, "JSON no enlazable nunca debe producir 500.");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.True(root.TryGetProperty("status", out var status), "ProblemDetails debe usar camelCase: status.");
        Assert.Equal(400, status.GetInt32());
        Assert.True(root.TryGetProperty("title", out var title), "ProblemDetails debe usar camelCase: title.");
        Assert.False(string.IsNullOrWhiteSpace(title.GetString()));
        if (requireErrors)
        {
            AssertHasNonEmptyErrors(root);
        }

        await AssertSeedWorkshopCountsAsync(client, active: 0, available: 2);
    }

    [Fact]
    public async Task PostReservation_RepeatNormalizedEmail_ReturnsDuplicateAndKeepsSingleActive()
    {
        // RES-08
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var first = await PostJsonAsync(
            client,
            SeedWorkshopId,
            ReservationJson("Ana Pérez", "ana@example.com"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var second = await PostJsonAsync(
            client,
            SeedWorkshopId,
            """{"participantName":"Ana María","email":" ANA@EXAMPLE.COM "}""");
        var payload = await second.Content.ReadAsStringAsync();

        AssertBusinessProblem(
            second,
            payload,
            HttpStatusCode.Conflict,
            "duplicate_reservation",
            "Conflicto de reserva",
            "Ese correo ya tiene una reserva activa en el taller.");
        await AssertSeedWorkshopCountsAsync(client, active: 1, available: 1);
    }

    [Fact]
    public async Task PostReservation_ThirdDistinctEmail_ReturnsWorkshopFullWithTwoActive()
    {
        // RES-09
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var first = await PostJsonAsync(
            client,
            SeedWorkshopId,
            ReservationJson("Ana Pérez", "ana@example.com"));
        using var second = await PostJsonAsync(
            client,
            SeedWorkshopId,
            ReservationJson("Luis Soto", "luis@example.com"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        using var firstDoc = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using var secondDoc = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        var firstId = firstDoc.RootElement.GetProperty("id").GetGuid();
        var secondId = secondDoc.RootElement.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, firstId);
        Assert.NotEqual(Guid.Empty, secondId);
        Assert.NotEqual(firstId, secondId);

        using var third = await PostJsonAsync(
            client,
            SeedWorkshopId,
            ReservationJson("María López", "maria@example.com"));
        var thirdPayload = await third.Content.ReadAsStringAsync();

        AssertBusinessProblem(
            third,
            thirdPayload,
            HttpStatusCode.Conflict,
            "workshop_full",
            "Conflicto de reserva",
            "El taller no tiene plazas disponibles.");
        await AssertSeedWorkshopCountsAsync(client, active: 2, available: 0);
    }

    [Fact]
    public async Task PostReservation_DuplicateWhenWorkshopIsFull_ReturnsDuplicateReservation()
    {
        // RES-10
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var first = await PostJsonAsync(
            client,
            SeedWorkshopId,
            ReservationJson("Ana Pérez", "ana@example.com"));
        using var second = await PostJsonAsync(
            client,
            SeedWorkshopId,
            ReservationJson("Luis Soto", "luis@example.com"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        using var duplicate = await PostJsonAsync(
            client,
            SeedWorkshopId,
            """{"participantName":"Ana otra","email":" ANA@EXAMPLE.COM "}""");
        var payload = await duplicate.Content.ReadAsStringAsync();

        AssertBusinessProblem(
            duplicate,
            payload,
            HttpStatusCode.Conflict,
            "duplicate_reservation",
            "Conflicto de reserva",
            "Ese correo ya tiene una reserva activa en el taller.");
        await AssertSeedWorkshopCountsAsync(client, active: 2, available: 0);
    }

    [Fact]
    public async Task PostReservation_UnknownGuid_ReturnsWorkshopNotFound_SeedUnchanged()
    {
        // RES-11 — GUID válido inexistente.
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var response = await PostJsonAsync(
            client,
            MissingWorkshopId,
            ReservationJson("Ana Pérez", "ana@example.com"));
        var payload = await response.Content.ReadAsStringAsync();

        AssertBusinessProblem(
            response,
            payload,
            HttpStatusCode.NotFound,
            "workshop_not_found",
            "Recurso no encontrado",
            "No se encontró el taller.");
        await AssertSeedWorkshopCountsAsync(client, active: 0, available: 2);
    }

    [Fact]
    public async Task PostReservation_NonGuidWorkshopId_ReturnsRouteNotFound_SeedUnchanged()
    {
        // RES-11 — ID que no es GUID: 404 de ruta, no 500.
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();
        using var content = new StringContent(
            ReservationJson("Ana Pérez", "ana@example.com"),
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync("/api/workshops/no-es-guid/reservations", content);
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True((int)response.StatusCode < 500, "Un ID que no es GUID nunca debe producir 500.");
        Assert.DoesNotContain(".cs:line", payload, StringComparison.OrdinalIgnoreCase);
        await AssertSeedWorkshopCountsAsync(client, active: 0, available: 2);
    }

    [Fact]
    public async Task PostReservation_InvalidDtoToUnknownGuid_Returns400BeforeStore()
    {
        // RES-12 — validación HTTP antes del almacén: 400, no 404 de negocio.
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var response = await PostJsonAsync(
            client,
            MissingWorkshopId,
            ReservationJson(" ", "no-es-correo"));
        var payload = await response.Content.ReadAsStringAsync();

        AssertValidationProblem(response, payload);
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.TryGetProperty("code", out var code))
        {
            Assert.NotEqual("workshop_not_found", code.GetString());
        }

        await AssertSeedWorkshopCountsAsync(client, active: 0, available: 2);
    }

    [Fact]
    public async Task PostReservation_BusinessErrors_ExposeAgreedStatusMediaTypeCodeTitleAndDetail()
    {
        // RES-13 — salidas de error de negocio; no hay 201 parcial.
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var missing = await PostJsonAsync(
            client,
            MissingWorkshopId,
            ReservationJson("Ana Pérez", "ana@example.com"));
        AssertBusinessProblem(
            missing,
            await missing.Content.ReadAsStringAsync(),
            HttpStatusCode.NotFound,
            "workshop_not_found",
            "Recurso no encontrado",
            "No se encontró el taller.");
        Assert.NotEqual(HttpStatusCode.Created, missing.StatusCode);

        using var first = await PostJsonAsync(
            client,
            SeedWorkshopId,
            ReservationJson("Ana Pérez", "ana@example.com"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var duplicate = await PostJsonAsync(
            client,
            SeedWorkshopId,
            ReservationJson("Otra Ana", "ana@example.com"));
        AssertBusinessProblem(
            duplicate,
            await duplicate.Content.ReadAsStringAsync(),
            HttpStatusCode.Conflict,
            "duplicate_reservation",
            "Conflicto de reserva",
            "Ese correo ya tiene una reserva activa en el taller.");
        Assert.NotEqual(HttpStatusCode.Created, duplicate.StatusCode);
        await AssertSeedWorkshopCountsAsync(client, active: 1, available: 1);

        using var second = await PostJsonAsync(
            client,
            SeedWorkshopId,
            ReservationJson("Luis Soto", "luis@example.com"));
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        using var full = await PostJsonAsync(
            client,
            SeedWorkshopId,
            ReservationJson("María López", "maria@example.com"));
        AssertBusinessProblem(
            full,
            await full.Content.ReadAsStringAsync(),
            HttpStatusCode.Conflict,
            "workshop_full",
            "Conflicto de reserva",
            "El taller no tiene plazas disponibles.");
        Assert.NotEqual(HttpStatusCode.Created, full.StatusCode);
        await AssertSeedWorkshopCountsAsync(client, active: 2, available: 0);
    }

    [Fact]
    public async Task GetHealthAndOpenApi_AfterReservation_KeepHealthCatalogAndDocumentPost()
    {
        // RES-14 — parte HTTP: salud, OpenAPI POST y catálogo conservados.
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var create = await PostJsonAsync(
            client,
            SeedWorkshopId,
            ReservationJson("Ana Pérez", "ana@example.com"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var catalog = await client.GetAsync("/api/workshops");
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        using var catalogDoc = JsonDocument.Parse(await catalog.Content.ReadAsStringAsync());
        Assert.Equal(1, catalogDoc.RootElement.GetArrayLength());
        AssertWorkshopCounts(catalogDoc.RootElement[0], active: 1, available: 1);

        using var openApi = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, openApi.StatusCode);
        using var openApiDoc = JsonDocument.Parse(await openApi.Content.ReadAsStringAsync());
        Assert.True(openApiDoc.RootElement.TryGetProperty("paths", out var paths), "OpenAPI debe incluir paths.");
        Assert.True(paths.TryGetProperty("/health", out _), "OpenAPI debe seguir documentando /health.");
        Assert.True(paths.TryGetProperty("/api/workshops", out _), "OpenAPI debe seguir documentando GET /api/workshops.");
        Assert.True(
            HasReservationPostPath(paths),
            "OpenAPI debe documentar POST /api/workshops/{id}/reservations.");
    }

    private static WebApplicationFactory<Program> CreateHost(string environment) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
        });

    private static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, Guid workshopId, string json) =>
        client.PostAsync(
            $"/api/workshops/{workshopId}/reservations",
            new StringContent(json, Encoding.UTF8, "application/json"));

    private static string ReservationJson(string participantName, string email)
    {
        var name = JsonSerializer.Serialize(participantName);
        var mail = JsonSerializer.Serialize(email);
        return $$"""{"participantName":{{name}},"email":{{mail}}}""";
    }

    private static string EmailOfLength(int length)
    {
        const string domain = "@example.com";
        Assert.True(length > domain.Length, "La longitud debe superar el dominio de example.com.");
        return new string('a', length - domain.Length) + domain;
    }

    private static Guid AssertCreatedReservationJson(
        JsonElement reservation,
        Guid workshopId,
        string expectedName,
        string expectedEmail,
        DateTimeOffset beforeUtc,
        DateTimeOffset afterUtc)
    {
        Assert.Equal(JsonValueKind.Object, reservation.ValueKind);
        var names = reservation.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(5, names.Length);
        Assert.Equal(ReservationFieldNames, names);

        var id = reservation.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(workshopId, reservation.GetProperty("workshopId").GetGuid());
        Assert.Equal(expectedName, reservation.GetProperty("participantName").GetString());
        Assert.Equal(expectedEmail, reservation.GetProperty("email").GetString());

        var createdAtElement = reservation.GetProperty("createdAtUtc");
        Assert.Equal(JsonValueKind.String, createdAtElement.ValueKind);
        var createdAt = createdAtElement.GetDateTimeOffset();
        Assert.Equal(TimeSpan.Zero, createdAt.Offset);
        Assert.True(
            createdAt >= beforeUtc.AddSeconds(-1) && createdAt <= afterUtc.AddSeconds(1),
            "createdAtUtc debe caer en el intervalo UTC capturado alrededor de la petición, no en una hora literal fija.");
        return id;
    }

    private static void AssertLocationMatchesReservation(HttpResponseMessage response, Guid reservationId)
    {
        Assert.NotNull(response.Headers.Location);
        var location = response.Headers.Location;
        var expectedPath = $"/api/reservations/{reservationId}";
        var actual = location.IsAbsoluteUri ? location.AbsolutePath : location.ToString();
        Assert.Equal(expectedPath, actual);
    }

    private static async Task AssertSeedWorkshopCountsAsync(HttpClient client, int active, int available)
    {
        using var catalog = await client.GetAsync("/api/workshops");
        using var detail = await client.GetAsync($"/api/workshops/{SeedWorkshopId}");
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);

        using var catalogDoc = JsonDocument.Parse(await catalog.Content.ReadAsStringAsync());
        using var detailDoc = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        AssertWorkshopCounts(catalogDoc.RootElement[0], active, available);
        AssertWorkshopCounts(detailDoc.RootElement, active, available);
    }

    private static void AssertWorkshopCounts(JsonElement workshop, int active, int available)
    {
        Assert.Equal(SeedWorkshopId, workshop.GetProperty("id").GetGuid());
        Assert.Equal(2, workshop.GetProperty("capacity").GetInt32());
        Assert.Equal(active, workshop.GetProperty("activeReservations").GetInt32());
        Assert.Equal(available, workshop.GetProperty("availableSeats").GetInt32());
        Assert.Equal(
            workshop.GetProperty("capacity").GetInt32() - workshop.GetProperty("activeReservations").GetInt32(),
            workshop.GetProperty("availableSeats").GetInt32());
    }

    private static void AssertValidationProblem(HttpResponseMessage response, string payload)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.True(root.TryGetProperty("status", out var status), "ProblemDetails debe usar camelCase: status.");
        Assert.Equal(400, status.GetInt32());
        Assert.True(root.TryGetProperty("title", out var title), "ProblemDetails debe usar camelCase: title.");
        Assert.False(string.IsNullOrWhiteSpace(title.GetString()));
        AssertHasNonEmptyErrors(root);
    }

    private static void AssertHasNonEmptyErrors(JsonElement root)
    {
        Assert.True(root.TryGetProperty("errors", out var errors), "El 400 de validación debe incluir errors.");
        Assert.Equal(JsonValueKind.Object, errors.ValueKind);
        Assert.True(errors.EnumerateObject().Any(), "errors no debe estar vacío.");
    }

    private static void AssertHasFieldError(string payload, string field)
    {
        using var document = JsonDocument.Parse(payload);
        Assert.True(
            TryFindFieldError(document.RootElement, field, out _),
            $"errors debe incluir el campo {field} (la capitalización de la clave no está fijada).");
    }

    private static void AssertDoesNotHaveFieldError(string payload, string field)
    {
        using var document = JsonDocument.Parse(payload);
        Assert.False(
            TryFindFieldError(document.RootElement, field, out var key),
            $"No se esperaba error de {field} (clave encontrada: {key}).");
    }

    private static bool TryFindFieldError(JsonElement root, string field, out string matchedKey)
    {
        matchedKey = string.Empty;
        if (!root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in errors.EnumerateObject())
        {
            if (property.Name.Contains(field, StringComparison.OrdinalIgnoreCase))
            {
                matchedKey = property.Name;
                return true;
            }
        }

        return false;
    }

    private static void AssertBusinessProblem(
        HttpResponseMessage response,
        string payload,
        HttpStatusCode expectedStatus,
        string expectedCode,
        string expectedTitle,
        string expectedDetail)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.True(root.TryGetProperty("status", out var status), "ProblemDetails debe usar camelCase: status.");
        Assert.Equal((int)expectedStatus, status.GetInt32());
        Assert.True(root.TryGetProperty("code", out var code), "ProblemDetails de negocio debe incluir code.");
        Assert.Equal(expectedCode, code.GetString());
        Assert.True(root.TryGetProperty("title", out var title), "ProblemDetails debe usar camelCase: title.");
        Assert.Equal(expectedTitle, title.GetString());
        Assert.True(root.TryGetProperty("detail", out var detail), "ProblemDetails debe usar camelCase: detail.");
        Assert.Equal(expectedDetail, detail.GetString());
    }

    private static bool HasReservationPostPath(JsonElement paths)
    {
        foreach (var path in paths.EnumerateObject())
        {
            var isWorkshopReservation = path.Name.StartsWith("/api/workshops/{", StringComparison.Ordinal)
                && path.Name.Contains("/reservations", StringComparison.Ordinal);
            if (isWorkshopReservation && path.Value.TryGetProperty("post", out _))
            {
                return true;
            }
        }

        return false;
    }
}
