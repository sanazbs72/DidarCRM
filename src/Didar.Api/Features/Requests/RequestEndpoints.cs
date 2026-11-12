using Microsoft.AspNetCore.Mvc;

namespace Didar.Api.Features.Requests;

public static class RequestEndpoints
{
    public static IEndpointRouteBuilder MapRequestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/requests", async (
            RegisterRequestInput? input,
            RegisterRequest registration,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var errors = RegistrationValidation.Validate(input);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var result = await registration.ExecuteAsync(input!, cancellationToken);
            if (result.ValidationErrors.Count > 0)
            {
                return Results.ValidationProblem(result.ValidationErrors);
            }

            if (!result.Created)
            {
                return Results.Ok(new RequestResponse(result.RequestId, result.Status));
            }

            var location = $"/requests/{result.RequestId:D}";
            context.Response.Headers.Location = location;
            return Results.Created(location, new RequestResponse(result.RequestId, result.Status));
        });

        endpoints.MapGet("/requests/{requestId}", async (
            string requestId,
            GetRequest reader,
            CancellationToken cancellationToken) =>
        {
            if (!Guid.TryParse(requestId, out var id))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request ID.");
            }

            var result = await reader.ExecuteAsync(id, cancellationToken);
            return result is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Request not found.")
                : Results.Ok(new RequestResponse(result.RequestId, result.Status));
        });

        endpoints.MapPost("/requests/{requestId}/close", async (
            string requestId,
            ChangeRequestStatus change,
            CancellationToken cancellationToken) =>
        {
            if (!Guid.TryParse(requestId, out var id))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request ID.");
            }

            return MapChange(await change.CloseAsync(id, cancellationToken), id);
        });

        endpoints.MapPost("/requests/{requestId}/cancel", async (
            string requestId,
            ChangeRequestStatus change,
            CancellationToken cancellationToken) =>
        {
            if (!Guid.TryParse(requestId, out var id))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request ID.");
            }

            return MapChange(await change.CancelAsync(id, cancellationToken), id);
        });

        return endpoints;
    }

    private static IResult MapChange(ChangeResult result, Guid requestId) => result.Outcome switch
    {
        ChangeOutcome.Success => Results.Ok(new RequestResponse(requestId, result.Status)),
        ChangeOutcome.Conflict => Results.Problem(
            statusCode: StatusCodes.Status409Conflict, title: "Request status conflict."),
        ChangeOutcome.NotFound => Results.Problem(
            statusCode: StatusCodes.Status404NotFound, title: "Request not found."),
        _ => Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable, title: "Unable to change the request status."),
    };
}

public sealed record RegisterRequestInput(string? RequestKey, Guid? SpecialtyId, string? Priority);
public sealed record RequestResponse(Guid RequestId, string Status);

internal static class RegistrationValidation
{
    public static Dictionary<string, string[]> Validate(RegisterRequestInput? input)
    {
        var errors = new Dictionary<string, string[]>();
        if (input is null)
        {
            errors["request"] = ["A JSON request body is required."];
            return errors;
        }

        if (string.IsNullOrWhiteSpace(input.RequestKey))
        {
            errors["requestKey"] = ["A nonblank request key is required."];
        }
        else
        {
            var byteCount = System.Text.Encoding.UTF8.GetByteCount(input.RequestKey);
            if (input.RequestKey.Contains('\0'))
            {
                errors["requestKey"] = ["The request key cannot contain U+0000."];
            }
            else if (byteCount > 1024)
            {
                errors["requestKey"] = ["The request key cannot exceed 1024 UTF-8 bytes."];
            }
        }

        if (input.SpecialtyId is null || input.SpecialtyId == Guid.Empty)
        {
            errors["specialtyId"] = ["A valid specialty ID is required."];
        }

        if (input.Priority is not ("Normal" or "Urgent"))
        {
            errors["priority"] = ["Priority must be Normal or Urgent."];
        }

        return errors;
    }
}
