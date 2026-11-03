using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using SwiftRide.Trip.Domain;

namespace SwiftRide.Trip.Api;

public sealed class TripExceptionFilter : IAsyncExceptionFilter
{
    public Task OnExceptionAsync(ExceptionContext context)
    {
        var (status, code, detail) = context.Exception switch
        {
            TripRuleViolation e => (409, e.Code, e.Message),
            DbUpdateConcurrencyException => (409, "stale_trip_version", "The trip changed concurrently; reload it before retrying."),
            DbUpdateException => (409, "trip_write_conflict", "The trip changed concurrently; reload it before retrying."),
            _ => (0, "", "")
        };
        if (status != 0)
        {
            context.Result = new ObjectResult(new ProblemDetails { Status = status, Title = code, Detail = detail }) { StatusCode = status };
            context.ExceptionHandled = true;
        }
        return Task.CompletedTask;
    }
}
