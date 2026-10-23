using Talleres.Api.Features.Workshops;

namespace Talleres.Api.Tests;

/// <summary>
/// Almacén de reservas (RES-14 atomicidad secuencial y RES-10 duplicado vs cupo).
/// Cada escenario usa su propia instancia; las entradas se envían ya normalizadas.
/// </summary>
public sealed class ReservationStoreTests
{
    [Fact]
    public void TryCreate_OnEmptySeedWorkshop_ReturnsCreatedAndUpdatesLaterReads()
    {
        // RES-14 — una operación registra y las lecturas posteriores reflejan el conteo.
        var store = new ReservationStore();
        var before = DateTimeOffset.UtcNow;

        var result = store.TryCreate(ReservationStore.SeedWorkshopId, "Ana Pérez", "ana@example.com");
        var after = DateTimeOffset.UtcNow;

        AssertCreated(result, "Ana Pérez", "ana@example.com", before, after);

        var listed = Assert.Single(store.GetWorkshops());
        var detail = store.GetWorkshop(ReservationStore.SeedWorkshopId);
        Assert.NotNull(detail);
        AssertWorkshopCounts(listed, active: 1, available: 1);
        AssertWorkshopCounts(detail, active: 1, available: 1);
        Assert.Equal(listed.Capacity - listed.ActiveReservations, listed.AvailableSeats);
        Assert.Equal(detail.Capacity - detail.ActiveReservations, detail.AvailableSeats);
    }

    [Fact]
    public void TryCreate_DoesNotMutatePreviousWorkshopSnapshot()
    {
        // RES-14 — instantánea previa inalterada.
        var store = new ReservationStore();
        IReadOnlyList<WorkshopResponse> previousList = store.GetWorkshops();
        var previousDetail = store.GetWorkshop(ReservationStore.SeedWorkshopId);
        Assert.NotNull(previousDetail);

        var result = store.TryCreate(ReservationStore.SeedWorkshopId, "Ana Pérez", "ana@example.com");
        Assert.True(result is CreateReservationResult.Created);

        var previousListed = Assert.Single(previousList);
        AssertWorkshopCounts(previousListed, active: 0, available: 2);
        AssertWorkshopCounts(previousDetail, active: 0, available: 2);

        var current = store.GetWorkshop(ReservationStore.SeedWorkshopId);
        Assert.NotNull(current);
        AssertWorkshopCounts(current, active: 1, available: 1);
    }

    [Fact]
    public void TryCreate_ChecksAndInsertStayConsistentAfterSingleCall()
    {
        // RES-14 — existencia, duplicado, cupo y alta como una sola operación (lock implícito secuencial).
        var store = new ReservationStore();

        var first = store.TryCreate(ReservationStore.SeedWorkshopId, "Ana Pérez", "ana@example.com");
        if (first is not CreateReservationResult.Created firstCreated)
        {
            Assert.Fail($"Se esperaba Created, se obtuvo {first.GetType().Name}.");
            return;
        }

        var afterFirst = store.GetWorkshop(ReservationStore.SeedWorkshopId);
        Assert.NotNull(afterFirst);
        Assert.Equal(afterFirst.Capacity - afterFirst.ActiveReservations, afterFirst.AvailableSeats);
        AssertWorkshopCounts(afterFirst, active: 1, available: 1);

        var second = store.TryCreate(ReservationStore.SeedWorkshopId, "Luis Soto", "luis@example.com");
        if (second is not CreateReservationResult.Created secondCreated)
        {
            Assert.Fail($"Se esperaba Created, se obtuvo {second.GetType().Name}.");
            return;
        }

        Assert.NotEqual(firstCreated.Reservation.Id, secondCreated.Reservation.Id);

        var afterSecond = store.GetWorkshop(ReservationStore.SeedWorkshopId);
        Assert.NotNull(afterSecond);
        Assert.Equal(afterSecond.Capacity - afterSecond.ActiveReservations, afterSecond.AvailableSeats);
        AssertWorkshopCounts(afterSecond, active: 2, available: 0);
        Assert.Equal(Assert.Single(store.GetWorkshops()).ActiveReservations, afterSecond.ActiveReservations);
    }

    [Fact]
    public void TryCreate_WhenEmailAlreadyReserved_ReturnsDuplicateAndDoesNotAdd()
    {
        // RES-10 / rechazos sin mutar — duplicado con entradas ya normalizadas.
        var store = new ReservationStore();
        var created = store.TryCreate(ReservationStore.SeedWorkshopId, "Ana Pérez", "ana@example.com");
        Assert.True(created is CreateReservationResult.Created);

        var duplicate = store.TryCreate(ReservationStore.SeedWorkshopId, "Ana María", "ana@example.com");

        Assert.True(duplicate is CreateReservationResult.Duplicate);
        Assert.False(duplicate is CreateReservationResult.Created);
        AssertWorkshopCounts(store.GetWorkshop(ReservationStore.SeedWorkshopId)!, active: 1, available: 1);
    }

    [Fact]
    public void TryCreate_WhenCapacityReached_ReturnsWorkshopFullAndDoesNotAdd()
    {
        // RES-14 — cupo: tercera reserva distinta no muta.
        var store = new ReservationStore();
        Assert.True(
            store.TryCreate(ReservationStore.SeedWorkshopId, "Ana Pérez", "ana@example.com")
                is CreateReservationResult.Created);
        Assert.True(
            store.TryCreate(ReservationStore.SeedWorkshopId, "Luis Soto", "luis@example.com")
                is CreateReservationResult.Created);

        var full = store.TryCreate(ReservationStore.SeedWorkshopId, "María López", "maria@example.com");

        Assert.True(full is CreateReservationResult.WorkshopFull);
        Assert.False(full is CreateReservationResult.Created);
        AssertWorkshopCounts(store.GetWorkshop(ReservationStore.SeedWorkshopId)!, active: 2, available: 0);
    }

    [Fact]
    public void TryCreate_WhenFullAndEmailDuplicate_ReturnsDuplicateNotFull()
    {
        // RES-10 — lleno + duplicado = Duplicate, no WorkshopFull.
        var store = new ReservationStore();
        Assert.True(
            store.TryCreate(ReservationStore.SeedWorkshopId, "Ana Pérez", "ana@example.com")
                is CreateReservationResult.Created);
        Assert.True(
            store.TryCreate(ReservationStore.SeedWorkshopId, "Luis Soto", "luis@example.com")
                is CreateReservationResult.Created);

        var result = store.TryCreate(ReservationStore.SeedWorkshopId, "Ana otra", "ana@example.com");

        Assert.True(result is CreateReservationResult.Duplicate);
        Assert.False(result is CreateReservationResult.WorkshopFull);
        Assert.False(result is CreateReservationResult.Created);
        AssertWorkshopCounts(store.GetWorkshop(ReservationStore.SeedWorkshopId)!, active: 2, available: 0);
    }

    [Fact]
    public void TryCreate_UnknownWorkshop_ReturnsNotFoundWithoutMutation()
    {
        // RES-14 — rechazo de taller inexistente no crea registro ni consume cupo.
        var store = new ReservationStore();
        var missingId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var result = store.TryCreate(missingId, "Ana Pérez", "ana@example.com");

        Assert.True(result is CreateReservationResult.WorkshopNotFound);
        Assert.False(result is CreateReservationResult.Created);
        Assert.Null(store.GetWorkshop(missingId));
        AssertWorkshopCounts(store.GetWorkshop(ReservationStore.SeedWorkshopId)!, active: 0, available: 2);
        Assert.Equal(2, Assert.Single(store.GetWorkshops()).AvailableSeats);
    }

    [Fact]
    public void TryCreate_RejectedCalls_DoNotConsumeSeats()
    {
        // RES-14 — rechazos (no encontrado, duplicado, cupo) sin mutar el estado aceptado.
        var store = new ReservationStore();
        var missingId = Guid.NewGuid();

        Assert.True(
            store.TryCreate(missingId, "Ana Pérez", "ghost@example.com")
                is CreateReservationResult.WorkshopNotFound);
        AssertWorkshopCounts(store.GetWorkshop(ReservationStore.SeedWorkshopId)!, active: 0, available: 2);

        Assert.True(
            store.TryCreate(ReservationStore.SeedWorkshopId, "Ana Pérez", "ana@example.com")
                is CreateReservationResult.Created);
        Assert.True(
            store.TryCreate(ReservationStore.SeedWorkshopId, "Ana Pérez", "ana@example.com")
                is CreateReservationResult.Duplicate);
        AssertWorkshopCounts(store.GetWorkshop(ReservationStore.SeedWorkshopId)!, active: 1, available: 1);

        Assert.True(
            store.TryCreate(ReservationStore.SeedWorkshopId, "Luis Soto", "luis@example.com")
                is CreateReservationResult.Created);
        Assert.True(
            store.TryCreate(ReservationStore.SeedWorkshopId, "María López", "maria@example.com")
                is CreateReservationResult.WorkshopFull);
        Assert.True(
            store.TryCreate(ReservationStore.SeedWorkshopId, "Luis Soto", "luis@example.com")
                is CreateReservationResult.Duplicate);

        var seed = store.GetWorkshop(ReservationStore.SeedWorkshopId);
        Assert.NotNull(seed);
        AssertWorkshopCounts(seed, active: 2, available: 0);
        Assert.Equal(seed.ActiveReservations, Assert.Single(store.GetWorkshops()).ActiveReservations);
    }

    [Fact]
    public void TryCancel_OfCreatedId_DecrementsLaterReadsByOne()
    {
        // CAN-01 almacén — TryCancel del ID creado: lecturas bajan 1.
        var store = new ReservationStore();
        var created = store.TryCreate(ReservationStore.SeedWorkshopId, "Ana Pérez", "ana@example.com");
        if (created is not CreateReservationResult.Created createdReservation)
        {
            Assert.Fail($"Se esperaba Created, se obtuvo {created.GetType().Name}.");
            return;
        }

        AssertWorkshopCounts(store.GetWorkshop(ReservationStore.SeedWorkshopId)!, active: 1, available: 1);

        var cancelled = store.TryCancel(createdReservation.Reservation.Id);

        Assert.True(cancelled is CancelReservationResult.Cancelled);
        Assert.False(cancelled is CancelReservationResult.NotFound);
        var listed = Assert.Single(store.GetWorkshops());
        var detail = store.GetWorkshop(ReservationStore.SeedWorkshopId);
        Assert.NotNull(detail);
        AssertWorkshopCounts(listed, active: 0, available: 2);
        AssertWorkshopCounts(detail, active: 0, available: 2);
    }

    [Fact]
    public void TryCancel_RepeatedOnSameId_ReturnsNotFoundAndDoesNotChangeCapacity()
    {
        // CAN-03 almacén — repetir TryCancel: NotFound y cupo igual.
        var store = new ReservationStore();
        var created = store.TryCreate(ReservationStore.SeedWorkshopId, "Ana Pérez", "ana@example.com");
        if (created is not CreateReservationResult.Created createdReservation)
        {
            Assert.Fail($"Se esperaba Created, se obtuvo {created.GetType().Name}.");
            return;
        }

        Assert.True(store.TryCancel(createdReservation.Reservation.Id) is CancelReservationResult.Cancelled);
        AssertWorkshopCounts(store.GetWorkshop(ReservationStore.SeedWorkshopId)!, active: 0, available: 2);

        var repeated = store.TryCancel(createdReservation.Reservation.Id);

        Assert.True(repeated is CancelReservationResult.NotFound);
        Assert.False(repeated is CancelReservationResult.Cancelled);
        AssertWorkshopCounts(store.GetWorkshop(ReservationStore.SeedWorkshopId)!, active: 0, available: 2);
        Assert.Equal(2, Assert.Single(store.GetWorkshops()).AvailableSeats);
    }

    [Fact]
    public void TryCancel_UnknownGuid_ReturnsNotFoundWithoutMutation()
    {
        // CAN-04 almacén — GUID desconocido: NotFound, sin mutar.
        var store = new ReservationStore();
        Assert.True(
            store.TryCreate(ReservationStore.SeedWorkshopId, "Ana Pérez", "ana@example.com")
                is CreateReservationResult.Created);
        var unknownId = Guid.Parse("33333333-3333-3333-3333-333333333333");

        var result = store.TryCancel(unknownId);

        Assert.True(result is CancelReservationResult.NotFound);
        Assert.False(result is CancelReservationResult.Cancelled);
        AssertWorkshopCounts(store.GetWorkshop(ReservationStore.SeedWorkshopId)!, active: 1, available: 1);
    }

    [Fact]
    public void TryCancel_WorkshopId_ReturnsNotFoundWithoutMutation()
    {
        // CAN-04 almacén — ID de taller: NotFound, sin mutar.
        var store = new ReservationStore();
        var created = store.TryCreate(ReservationStore.SeedWorkshopId, "Ana Pérez", "ana@example.com");
        if (created is not CreateReservationResult.Created createdReservation)
        {
            Assert.Fail($"Se esperaba Created, se obtuvo {created.GetType().Name}.");
            return;
        }

        Assert.NotEqual(ReservationStore.SeedWorkshopId, createdReservation.Reservation.Id);

        var result = store.TryCancel(ReservationStore.SeedWorkshopId);

        Assert.True(result is CancelReservationResult.NotFound);
        Assert.False(result is CancelReservationResult.Cancelled);
        AssertWorkshopCounts(store.GetWorkshop(ReservationStore.SeedWorkshopId)!, active: 1, available: 1);
    }

    [Fact]
    public void TryCancel_ThenTryCreateSameEmail_ReturnsCreatedWithDifferentId()
    {
        // CAN-06 almacén — cancelar + TryCreate mismo correo: Created con otro ID.
        var store = new ReservationStore();
        var first = store.TryCreate(ReservationStore.SeedWorkshopId, "Ana Pérez", "ana@example.com");
        if (first is not CreateReservationResult.Created firstCreated)
        {
            Assert.Fail($"Se esperaba Created, se obtuvo {first.GetType().Name}.");
            return;
        }

        Assert.True(store.TryCancel(firstCreated.Reservation.Id) is CancelReservationResult.Cancelled);

        var second = store.TryCreate(ReservationStore.SeedWorkshopId, "Ana Pérez", "ana@example.com");
        if (second is not CreateReservationResult.Created secondCreated)
        {
            Assert.Fail($"Se esperaba Created, se obtuvo {second.GetType().Name}.");
            return;
        }

        Assert.NotEqual(firstCreated.Reservation.Id, secondCreated.Reservation.Id);
        AssertWorkshopCounts(store.GetWorkshop(ReservationStore.SeedWorkshopId)!, active: 1, available: 1);
    }

    [Fact]
    public void TryCancel_OneOfTwo_DoesNotTouchTheRemainingDuplicate()
    {
        // CAN-08 almacén — cancelar una de dos no toca la otra; la restante sigue provocando Duplicate.
        var store = new ReservationStore();
        var ana = store.TryCreate(ReservationStore.SeedWorkshopId, "Ana Pérez", "ana@example.com");
        var luis = store.TryCreate(ReservationStore.SeedWorkshopId, "Luis Soto", "luis@example.com");
        if (ana is not CreateReservationResult.Created anaCreated)
        {
            Assert.Fail($"Se esperaba Created para Ana, se obtuvo {ana.GetType().Name}.");
            return;
        }

        if (luis is not CreateReservationResult.Created luisCreated)
        {
            Assert.Fail($"Se esperaba Created para Luis, se obtuvo {luis.GetType().Name}.");
            return;
        }

        Assert.True(store.TryCancel(anaCreated.Reservation.Id) is CancelReservationResult.Cancelled);
        AssertWorkshopCounts(store.GetWorkshop(ReservationStore.SeedWorkshopId)!, active: 1, available: 1);

        var duplicateLuis = store.TryCreate(ReservationStore.SeedWorkshopId, "Luis Soto", "luis@example.com");
        Assert.True(duplicateLuis is CreateReservationResult.Duplicate);
        Assert.False(duplicateLuis is CreateReservationResult.Created);
        AssertWorkshopCounts(store.GetWorkshop(ReservationStore.SeedWorkshopId)!, active: 1, available: 1);

        Assert.True(store.TryCancel(luisCreated.Reservation.Id) is CancelReservationResult.Cancelled);
        AssertWorkshopCounts(store.GetWorkshop(ReservationStore.SeedWorkshopId)!, active: 0, available: 2);
    }

    private static void AssertCreated(
        CreateReservationResult result,
        string expectedName,
        string expectedEmail,
        DateTimeOffset beforeUtc,
        DateTimeOffset afterUtc)
    {
        if (result is not CreateReservationResult.Created created)
        {
            Assert.Fail($"Se esperaba Created, se obtuvo {result.GetType().Name}.");
            return;
        }

        var reservation = created.Reservation;
        Assert.NotEqual(Guid.Empty, reservation.Id);
        Assert.Equal(ReservationStore.SeedWorkshopId, reservation.WorkshopId);
        Assert.Equal(expectedName, reservation.ParticipantName);
        Assert.Equal(expectedEmail, reservation.Email);
        Assert.Equal(TimeSpan.Zero, reservation.CreatedAtUtc.Offset);
        Assert.True(
            reservation.CreatedAtUtc >= beforeUtc.AddSeconds(-1)
            && reservation.CreatedAtUtc <= afterUtc.AddSeconds(1),
            "CreatedAtUtc debe caer en el intervalo UTC de la operación, no en una hora literal fija.");
    }

    private static void AssertWorkshopCounts(WorkshopResponse workshop, int active, int available)
    {
        Assert.Equal(ReservationStore.SeedWorkshopId, workshop.Id);
        Assert.Equal(2, workshop.Capacity);
        Assert.Equal(active, workshop.ActiveReservations);
        Assert.Equal(available, workshop.AvailableSeats);
    }
}
