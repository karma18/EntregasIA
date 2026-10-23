using Microsoft.AspNetCore.Http.HttpResults;

namespace Talleres.Api.Features.Workshops;

public static class WorkshopEndpoints
{
    public static IEndpointRouteBuilder MapWorkshopEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/workshops", ListWorkshops)
            .WithName("GetWorkshops")
            .WithSummary("Consultar el catálogo de talleres")
            .Produces<IReadOnlyList<WorkshopResponse>>(StatusCodes.Status200OK);

        app.MapGet("/api/workshops/{id:guid}", GetWorkshop)
            .WithName("GetWorkshop")
            .WithSummary("Consultar el detalle de un taller")
            .Produces<WorkshopResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPost("/api/workshops/{id:guid}/reservations", CreateReservation)
            .WithName("CreateReservation")
            .WithSummary("Reservar un lugar en un taller")
            .Produces<ReservationResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapDelete("/api/reservations/{id:guid}", CancelReservation)
            .WithName("CancelReservation")
            .WithSummary("Cancelar una reserva")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static Ok<IReadOnlyList<WorkshopResponse>> ListWorkshops(ReservationStore store) =>
        TypedResults.Ok(store.GetWorkshops());

    private static Results<Ok<WorkshopResponse>, ProblemHttpResult> GetWorkshop(
        Guid id,
        ReservationStore store)
    {
        var workshop = store.GetWorkshop(id);
        if (workshop is null)
        {
            return WorkshopNotFound();
        }

        return TypedResults.Ok(workshop);
    }

    private static Results<Created<ReservationResponse>, ProblemHttpResult> CreateReservation(
        Guid id,
        CreateReservationRequest request,
        ReservationStore store)
    {
        var result = store.TryCreate(id, request.ParticipantName!, request.Email!);

        return result switch
        {
            CreateReservationResult.Created created =>
                TypedResults.Created($"/api/reservations/{created.Reservation.Id}", created.Reservation),
            CreateReservationResult.WorkshopNotFound => WorkshopNotFound(),
            CreateReservationResult.Duplicate => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Conflicto de reserva",
                detail: "Ese correo ya tiene una reserva activa en el taller.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "duplicate_reservation"
                }),
            CreateReservationResult.WorkshopFull => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Conflicto de reserva",
                detail: "El taller no tiene plazas disponibles.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "workshop_full"
                }),
            _ => throw new InvalidOperationException("Resultado de reserva no reconocido.")
        };
    }

    private static Results<NoContent, ProblemHttpResult> CancelReservation(
        Guid id,
        ReservationStore store)
    {
        var result = store.TryCancel(id);

        return result switch
        {
            CancelReservationResult.Cancelled => TypedResults.NoContent(),
            CancelReservationResult.NotFound => ReservationNotFound(),
            _ => throw new InvalidOperationException("Resultado de cancelación no reconocido.")
        };
    }

    private static ProblemHttpResult WorkshopNotFound() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Recurso no encontrado",
            detail: "No se encontró el taller.",
            extensions: new Dictionary<string, object?>
            {
                    ["code"] = "workshop_not_found"
            });

    private static ProblemHttpResult ReservationNotFound() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Recurso no encontrado",
            detail: "No se encontró una reserva activa con ese identificador.",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "reservation_not_found"
            });
}
