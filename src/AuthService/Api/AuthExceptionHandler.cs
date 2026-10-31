using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SwiftRide.Auth.Application;

namespace SwiftRide.Auth.Api;

public sealed class AuthExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not AuthException failure) return false;
        context.Response.StatusCode = failure.Status;
        var details = new ProblemDetails { Status = failure.Status, Title = failure.Code, Detail = failure.Message,
            Extensions = { ["traceId"] = context.TraceIdentifier } };
        if (!await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = details
        })) await context.Response.WriteAsJsonAsync(details, ct);
        return true;
    }
}
