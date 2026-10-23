using System.ComponentModel.DataAnnotations;

namespace Talleres.Api.Features.Workshops;

public sealed record WorkshopResponse(
    Guid Id,
    string Title,
    int Capacity,
    int ActiveReservations,
    int AvailableSeats);

public sealed class CreateReservationRequest
{
    private string? _participantName;
    private string? _email;

    [Required]
    [StringLength(100, MinimumLength = 2)]
    public string? ParticipantName
    {
        get => _participantName;
        set => _participantName = value?.Trim();
    }

    [Required]
    [MaxLength(254)]
    [EmailAddress]
    public string? Email
    {
        get => _email;
        set => _email = value?.Trim().ToLowerInvariant();
    }
}

public sealed record ReservationResponse(
    Guid Id,
    Guid WorkshopId,
    string ParticipantName,
    string Email,
    DateTimeOffset CreatedAtUtc);
