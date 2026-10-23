using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace Talleres.Api.Tests;

/// <summary>
/// Cancelación de reservas (CAN-01 a CAN-10 HTTP).
/// Cada escenario HTTP crea y dispone su propio host; no usa IClassFixture global.
/// Los IDs de reserva se toman del POST; nunca se fijan GUIDs de reserva.
/// </summary>
public sealed class CancellationTests
{
    private static readonly Guid SeedWorkshopId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UnknownReservationId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task DeleteReservation_OfCreatedId_Returns204EmptyBodyAndFreesSeat()
    {
        // CAN-01 — crear una reserva y cancelar su ID: 204 sin cuerpo; activas 0, disponibles 2.
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var create = await PostReservationAsync(client, "Ana Pérez", "ana@example.com");
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var reservationId = await ReadReservationIdAsync(create);

        using var delete = await client.DeleteAsync($"/api/reservations/{reservationId}");
        var payload = await delete.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.True(string.IsNullOrEmpty(payload), "El 204 de cancelación debe ir con cuerpo vacío.");
        await AssertSeedWorkshopCountsAsync(client, active: 0, available: 2);
    }

    [Fact]
    public async Task DeleteReservation_WhenWorkshopIsFull_FreesOneSeatKeepsCapacity()
    {
        // CAN-02 — llenar (Ana + Luis) y cancelar una: 204; activas 1, disponibles 1, capacidad 2.
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var ana = await PostReservationAsync(client, "Ana Pérez", "ana@example.com");
        using var luis = await PostReservationAsync(client, "Luis Soto", "luis@example.com");
        Assert.Equal(HttpStatusCode.Created, ana.StatusCode);
        Assert.Equal(HttpStatusCode.Created, luis.StatusCode);
        var anaId = await ReadReservationIdAsync(ana);
        await AssertSeedWorkshopCountsAsync(client, active: 2, available: 0);

        using var delete = await client.DeleteAsync($"/api/reservations/{anaId}");
        var payload = await delete.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.True(string.IsNullOrEmpty(payload), "El 204 de cancelación debe ir con cuerpo vacío.");
        await AssertSeedWorkshopCountsAsync(client, active: 1, available: 1);
    }

    [Fact]
    public async Task DeleteReservation_RepeatedOnSameId_ReturnsNotFoundAndDoesNotChangeCounts()
    {
        // CAN-03 — repetir DELETE del mismo ID: 404 reservation_not_found; conteos sin cambios.
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var create = await PostReservationAsync(client, "Ana Pérez", "ana@example.com");
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var reservationId = await ReadReservationIdAsync(create);

        using var firstDelete = await client.DeleteAsync($"/api/reservations/{reservationId}");
        Assert.Equal(HttpStatusCode.NoContent, firstDelete.StatusCode);
        await AssertSeedWorkshopCountsAsync(client, active: 0, available: 2);

        using var secondDelete = await client.DeleteAsync($"/api/reservations/{reservationId}");
        var payload = await secondDelete.Content.ReadAsStringAsync();

        AssertReservationNotFound(secondDelete, payload);
        await AssertSeedWorkshopCountsAsync(client, active: 0, available: 2);
    }

    [Fact]
    public async Task DeleteReservation_UnknownGuid_ReturnsNotFoundAndKeepsExistingReservations()
    {
        // CAN-04 — GUID desconocido: 404 de negocio; las reservas existentes permanecen.
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var create = await PostReservationAsync(client, "Ana Pérez", "ana@example.com");
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var existingId = await ReadReservationIdAsync(create);
        Assert.NotEqual(UnknownReservationId, existingId);

        using var delete = await client.DeleteAsync($"/api/reservations/{UnknownReservationId}");
        var payload = await delete.Content.ReadAsStringAsync();

        AssertReservationNotFound(delete, payload);
        await AssertSeedWorkshopCountsAsync(client, active: 1, available: 1);
    }

    [Fact]
    public async Task DeleteReservation_WithWorkshopId_ReturnsNotFoundAndKeepsExistingReservations()
    {
        // CAN-04 — ID del taller no cancela reservas: 404 de negocio; las existentes permanecen.
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var create = await PostReservationAsync(client, "Ana Pérez", "ana@example.com");
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var reservationId = await ReadReservationIdAsync(create);
        Assert.NotEqual(SeedWorkshopId, reservationId);

        using var delete = await client.DeleteAsync($"/api/reservations/{SeedWorkshopId}");
        var payload = await delete.Content.ReadAsStringAsync();

        AssertReservationNotFound(delete, payload);
        await AssertSeedWorkshopCountsAsync(client, active: 1, available: 1);
    }

    [Fact]
    public async Task DeleteReservation_NonGuidId_ReturnsRouteNotFoundNeverServerError()
    {
        // CAN-05 — DELETE /api/reservations/no-es-guid: 404 de ruta, nunca 500.
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var delete = await client.DeleteAsync("/api/reservations/no-es-guid");
        var payload = await delete.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.True((int)delete.StatusCode < 500, "Un ID que no es GUID nunca debe producir 500.");
        Assert.DoesNotContain(".cs:line", payload, StringComparison.OrdinalIgnoreCase);
        await AssertSeedWorkshopCountsAsync(client, active: 0, available: 2);
    }

    [Fact]
    public async Task DeleteReservation_ThenPostNormalizedSameEmail_CreatesNewIdAndOccupiesOneSeat()
    {
        // CAN-06 — tras cancelar, POST mismo correo normalizado: 201, ID distinto; un solo lugar ocupado.
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var first = await PostReservationAsync(client, "Ana Pérez", "ana@example.com");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var cancelledId = await ReadReservationIdAsync(first);

        using var delete = await client.DeleteAsync($"/api/reservations/{cancelledId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using var second = await PostJsonAsync(
            client,
            """{"participantName":"Ana Pérez","email":" ANA@EXAMPLE.COM "}""");
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var newId = await ReadReservationIdAsync(second);

        Assert.NotEqual(cancelledId, newId);
        await AssertSeedWorkshopCountsAsync(client, active: 1, available: 1);
    }

    [Fact]
    public async Task DeleteReservation_OldIdAfterRebooking_ReturnsNotFoundAndKeepsNewReservation()
    {
        // CAN-07 — tras volver a reservar, DELETE del ID antiguo: 404; la nueva reserva sigue activa.
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var first = await PostReservationAsync(client, "Ana Pérez", "ana@example.com");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var oldId = await ReadReservationIdAsync(first);

        using var firstDelete = await client.DeleteAsync($"/api/reservations/{oldId}");
        Assert.Equal(HttpStatusCode.NoContent, firstDelete.StatusCode);

        using var second = await PostReservationAsync(client, "Ana Pérez", "ana@example.com");
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var newId = await ReadReservationIdAsync(second);
        Assert.NotEqual(oldId, newId);

        using var oldDelete = await client.DeleteAsync($"/api/reservations/{oldId}");
        var payload = await oldDelete.Content.ReadAsStringAsync();

        AssertReservationNotFound(oldDelete, payload);
        await AssertSeedWorkshopCountsAsync(client, active: 1, available: 1);
    }

    [Fact]
    public async Task DeleteReservation_OfAna_LeavesLuisDuplicateUntilLuisIsCancelled()
    {
        // CAN-08 — Ana + Luis; cancelar Ana; Luis sigue duplicate_reservation; cancelar Luis libera su lugar.
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var ana = await PostReservationAsync(client, "Ana Pérez", "ana@example.com");
        using var luis = await PostReservationAsync(client, "Luis Soto", "luis@example.com");
        Assert.Equal(HttpStatusCode.Created, ana.StatusCode);
        Assert.Equal(HttpStatusCode.Created, luis.StatusCode);
        var anaId = await ReadReservationIdAsync(ana);
        var luisId = await ReadReservationIdAsync(luis);

        using var cancelAna = await client.DeleteAsync($"/api/reservations/{anaId}");
        Assert.Equal(HttpStatusCode.NoContent, cancelAna.StatusCode);
        await AssertSeedWorkshopCountsAsync(client, active: 1, available: 1);

        using var duplicateLuis = await PostReservationAsync(client, "Luis Soto", "luis@example.com");
        AssertBusinessProblem(
            duplicateLuis,
            await duplicateLuis.Content.ReadAsStringAsync(),
            HttpStatusCode.Conflict,
            "duplicate_reservation",
            "Conflicto de reserva",
            "Ese correo ya tiene una reserva activa en el taller.");
        await AssertSeedWorkshopCountsAsync(client, active: 1, available: 1);

        using var cancelLuis = await client.DeleteAsync($"/api/reservations/{luisId}");
        Assert.Equal(HttpStatusCode.NoContent, cancelLuis.StatusCode);
        await AssertSeedWorkshopCountsAsync(client, active: 0, available: 2);
    }

    [Fact]
    public async Task DeleteReservation_AllThenRepeatDeletes_AvailableSeatsNeverExceedCapacity()
    {
        // CAN-09 — cancelar todas y repetir sus DELETE: disponibles termina en 2 y no supera la capacidad.
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var ana = await PostReservationAsync(client, "Ana Pérez", "ana@example.com");
        using var luis = await PostReservationAsync(client, "Luis Soto", "luis@example.com");
        Assert.Equal(HttpStatusCode.Created, ana.StatusCode);
        Assert.Equal(HttpStatusCode.Created, luis.StatusCode);
        var anaId = await ReadReservationIdAsync(ana);
        var luisId = await ReadReservationIdAsync(luis);

        using var cancelAna = await client.DeleteAsync($"/api/reservations/{anaId}");
        Assert.Equal(HttpStatusCode.NoContent, cancelAna.StatusCode);
        await AssertAvailableNeverExceedsCapacityAsync(client, expectedActive: 1, expectedAvailable: 1);

        using var cancelLuis = await client.DeleteAsync($"/api/reservations/{luisId}");
        Assert.Equal(HttpStatusCode.NoContent, cancelLuis.StatusCode);
        await AssertAvailableNeverExceedsCapacityAsync(client, expectedActive: 0, expectedAvailable: 2);

        using var repeatAna = await client.DeleteAsync($"/api/reservations/{anaId}");
        using var repeatLuis = await client.DeleteAsync($"/api/reservations/{luisId}");
        AssertReservationNotFound(repeatAna, await repeatAna.Content.ReadAsStringAsync());
        AssertReservationNotFound(repeatLuis, await repeatLuis.Content.ReadAsStringAsync());
        await AssertAvailableNeverExceedsCapacityAsync(client, expectedActive: 0, expectedAvailable: 2);
    }

    [Fact]
    public async Task DeleteReservation_ErrorJsonAndPostRegression_KeepNormalizationDuplicateAndCapacity()
    {
        // CAN-10 — JSON de 404 de cancelación + regresión POST (normalización, duplicado y cupo).
        await using var factory = CreateHost(Environments.Development);
        using var client = factory.CreateClient();

        using var missing = await client.DeleteAsync($"/api/reservations/{UnknownReservationId}");
        AssertReservationNotFound(missing, await missing.Content.ReadAsStringAsync());

        using var first = await PostJsonAsync(
            client,
            """{"participantName":"  Ana Pérez  ","email":" ANA@EXAMPLE.COM "}""");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var firstDoc = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Assert.Equal("Ana Pérez", firstDoc.RootElement.GetProperty("participantName").GetString());
        Assert.Equal("ana@example.com", firstDoc.RootElement.GetProperty("email").GetString());
        await AssertSeedWorkshopCountsAsync(client, active: 1, available: 1);

        using var duplicate = await PostJsonAsync(
            client,
            """{"participantName":"Ana María","email":" ANA@EXAMPLE.COM "}""");
        AssertBusinessProblem(
            duplicate,
            await duplicate.Content.ReadAsStringAsync(),
            HttpStatusCode.Conflict,
            "duplicate_reservation",
            "Conflicto de reserva",
            "Ese correo ya tiene una reserva activa en el taller.");
        await AssertSeedWorkshopCountsAsync(client, active: 1, available: 1);

        using var second = await PostReservationAsync(client, "Luis Soto", "luis@example.com");
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        await AssertSeedWorkshopCountsAsync(client, active: 2, available: 0);

        using var full = await PostReservationAsync(client, "María López", "maria@example.com");
        AssertBusinessProblem(
            full,
            await full.Content.ReadAsStringAsync(),
            HttpStatusCode.Conflict,
            "workshop_full",
            "Conflicto de reserva",
            "El taller no tiene plazas disponibles.");
        await AssertSeedWorkshopCountsAsync(client, active: 2, available: 0);
    }

    private static WebApplicationFactory<Program> CreateHost(string environment) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
        });

    private static Task<HttpResponseMessage> PostReservationAsync(
        HttpClient client,
        string participantName,
        string email) =>
        PostJsonAsync(client, ReservationJson(participantName, email));

    private static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string json) =>
        client.PostAsync(
            $"/api/workshops/{SeedWorkshopId}/reservations",
            new StringContent(json, Encoding.UTF8, "application/json"));

    private static string ReservationJson(string participantName, string email)
    {
        var name = JsonSerializer.Serialize(participantName);
        var mail = JsonSerializer.Serialize(email);
        return $$"""{"participantName":{{name}},"email":{{mail}}}""";
    }

    private static async Task<Guid> ReadReservationIdAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = document.RootElement.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, id);
        return id;
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

    private static async Task AssertAvailableNeverExceedsCapacityAsync(
        HttpClient client,
        int expectedActive,
        int expectedAvailable)
    {
        using var catalog = await client.GetAsync("/api/workshops");
        using var detail = await client.GetAsync($"/api/workshops/{SeedWorkshopId}");
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);

        using var catalogDoc = JsonDocument.Parse(await catalog.Content.ReadAsStringAsync());
        using var detailDoc = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        AssertWorkshopCounts(catalogDoc.RootElement[0], expectedActive, expectedAvailable);
        AssertWorkshopCounts(detailDoc.RootElement, expectedActive, expectedAvailable);
        Assert.True(
            catalogDoc.RootElement[0].GetProperty("availableSeats").GetInt32()
            <= catalogDoc.RootElement[0].GetProperty("capacity").GetInt32(),
            "Los asientos disponibles no deben superar la capacidad.");
        Assert.True(
            detailDoc.RootElement.GetProperty("availableSeats").GetInt32()
            <= detailDoc.RootElement.GetProperty("capacity").GetInt32(),
            "Los asientos disponibles no deben superar la capacidad.");
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

    private static void AssertReservationNotFound(HttpResponseMessage response, string payload) =>
        AssertBusinessProblem(
            response,
            payload,
            HttpStatusCode.NotFound,
            "reservation_not_found",
            "Recurso no encontrado",
            "No se encontró una reserva activa con ese identificador.");

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
}
