namespace Talleres.Api.Features.Workshops;

public abstract record CreateReservationResult
{
    private CreateReservationResult()
    {
    }

    public sealed record Created(ReservationResponse Reservation) : CreateReservationResult;

    public sealed record WorkshopNotFound : CreateReservationResult;

    public sealed record Duplicate : CreateReservationResult;

    public sealed record WorkshopFull : CreateReservationResult;
}

public abstract record CancelReservationResult
{
    private CancelReservationResult()
    {
    }

    public sealed record Cancelled : CancelReservationResult;

    public sealed record NotFound : CancelReservationResult;
}

public sealed class ReservationStore
{
    public static readonly Guid SeedWorkshopId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly object _sync = new();
    private readonly Dictionary<Guid, WorkshopState> _workshops = new();
    private readonly List<ReservationState> _reservations = [];

    public ReservationStore()
    {
        _workshops[SeedWorkshopId] = new WorkshopState(
            SeedWorkshopId,
            "Taller de Cursor",
            Capacity: 2);
    }

    public IReadOnlyList<WorkshopResponse> GetWorkshops()
    {
        lock (_sync)
        {
            return _workshops.Values.Select(ToSnapshot).ToArray();
        }
    }

    public WorkshopResponse? GetWorkshop(Guid id)
    {
        lock (_sync)
        {
            return _workshops.TryGetValue(id, out var workshop)
                ? ToSnapshot(workshop)
                : null;
        }
    }

    public CreateReservationResult TryCreate(Guid workshopId, string participantName, string email)
    {
        lock (_sync)
        {
            if (!_workshops.TryGetValue(workshopId, out var workshop))
            {
                return new CreateReservationResult.WorkshopNotFound();
            }

            foreach (var reservation in _reservations)
            {
                if (reservation.IsActive
                    && reservation.WorkshopId == workshopId
                    && reservation.Email == email)
                {
                    return new CreateReservationResult.Duplicate();
                }
            }

            if (CountActiveReservations(workshopId) >= workshop.Capacity)
            {
                return new CreateReservationResult.WorkshopFull();
            }

            var created = new ReservationState(
                Guid.NewGuid(),
                participantName,
                email,
                DateTimeOffset.UtcNow,
                workshopId,
                IsActive: true);

            _reservations.Add(created);

            return new CreateReservationResult.Created(
                new ReservationResponse(
                    created.Id,
                    created.WorkshopId,
                    created.ParticipantName,
                    created.Email,
                    created.CreatedAtUtc));
        }
    }

    public CancelReservationResult TryCancel(Guid reservationId)
    {
        lock (_sync)
        {
            for (var i = 0; i < _reservations.Count; i++)
            {
                if (_reservations[i].Id != reservationId)
                {
                    continue;
                }

                _reservations.RemoveAt(i);
                return new CancelReservationResult.Cancelled();
            }

            return new CancelReservationResult.NotFound();
        }
    }

    private WorkshopResponse ToSnapshot(WorkshopState workshop)
    {
        var activeReservations = CountActiveReservations(workshop.Id);
        var availableSeats = workshop.Capacity - activeReservations;
        return new WorkshopResponse(
            workshop.Id,
            workshop.Title,
            workshop.Capacity,
            activeReservations,
            availableSeats);
    }

    private int CountActiveReservations(Guid workshopId)
    {
        var count = 0;
        foreach (var reservation in _reservations)
        {
            if (reservation.WorkshopId == workshopId && reservation.IsActive)
            {
                count++;
            }
        }

        return count;
    }

    private sealed record WorkshopState(Guid Id, string Title, int Capacity);

    private sealed record ReservationState(
        Guid Id,
        string ParticipantName,
        string Email,
        DateTimeOffset CreatedAtUtc,
        Guid WorkshopId,
        bool IsActive);
}
