using Microsoft.AspNetCore.Mvc;

namespace Didar.Api.Features.Experts;

public static class ExpertEndpoints
{
    public static IEndpointRouteBuilder MapExpertEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPatch("/experts/{expertId}/capacity", async (
            string expertId,
            CapacityInput? input,
            ChangeExpertAvailability change,
            CancellationToken cancellationToken) =>
        {
            if (!Guid.TryParse(expertId, out var id))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid expert ID.");
            }

            if (input?.Capacity is null || input.Capacity < 0)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Capacity must be a non-negative integer.");
            }

            var result = await change.SetCapacityAsync(id, input.Capacity.Value, cancellationToken);
            return result.Outcome switch
            {
                ChangeAvailabilityOutcome.Success => Results.Ok(new CapacityResponse(id, input.Capacity.Value, result.Status)),
                ChangeAvailabilityOutcome.NotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Expert not found."),
                _ => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Unable to change capacity."),
            };
        });

        endpoints.MapPatch("/experts/{expertId}/status", async (
            string expertId,
            StatusInput? input,
            ChangeExpertAvailability change,
            CancellationToken cancellationToken) =>
        {
            if (!Guid.TryParse(expertId, out var id))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid expert ID.");
            }

            if (input?.Status is not ("Active" or "Inactive"))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Status must be Active or Inactive.");
            }

            var result = await change.SetExpertStatusAsync(id, input.Status, cancellationToken);
            return result.Outcome switch
            {
                ChangeAvailabilityOutcome.Success => Results.Ok(new ExpertStatusResponse(id, result.Status)),
                ChangeAvailabilityOutcome.Conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Status conflict."),
                ChangeAvailabilityOutcome.NotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Expert not found."),
                _ => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Unable to change status."),
            };
        });

        endpoints.MapPatch("/experts/{expertId}/specialties/{specialtyId}/status", async (
            string expertId,
            string specialtyId,
            StatusInput? input,
            ChangeExpertAvailability change,
            CancellationToken cancellationToken) =>
        {
            if (!Guid.TryParse(expertId, out var id))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid expert ID.");
            }

            if (!Guid.TryParse(specialtyId, out var specialty))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid specialty ID.");
            }

            if (input?.Status is not ("Active" or "Inactive"))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Status must be Active or Inactive.");
            }

            var result = await change.SetSpecialtyStatusAsync(id, specialty, input.Status, cancellationToken);
            return result.Outcome switch
            {
                ChangeAvailabilityOutcome.Success => Results.Ok(new SpecialtyStatusResponse(id, specialty, result.Status)),
                ChangeAvailabilityOutcome.Conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Status conflict."),
                ChangeAvailabilityOutcome.NotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Expert or specialty relation not found."),
                _ => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Unable to change status."),
            };
        });

        return endpoints;
    }
}

public sealed record CapacityInput(int? Capacity);
public sealed record StatusInput(string? Status);
public sealed record CapacityResponse(Guid ExpertId, int Capacity, string Status);
public sealed record ExpertStatusResponse(Guid ExpertId, string Status);
public sealed record SpecialtyStatusResponse(Guid ExpertId, Guid SpecialtyId, string Status);
