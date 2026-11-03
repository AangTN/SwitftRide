using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SwiftRide.Contracts.Payments;
using SwiftRide.Payment.Application;
using SwiftRide.Payment.Infrastructure;
using SwiftRide.ServiceDefaults;

namespace SwiftRide.Payment.Api.Controllers;

[ApiController]
[Route("api/payments")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PaymentsController(
    IPaymentService paymentService,
    PaymentDbContext dbContext,
    IWebHostEnvironment environment) : ControllerBase
{
    private Guid? GetCurrentUserId()
    {
        var sub = User.FindFirst("sub")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    private List<string> GetCurrentUserRoles()
    {
        return User.FindAll("roles")
            .Concat(User.FindAll(ClaimTypes.Role))
            .Select(c => c.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    [HttpPost]
    [Authorize]
    public async Task<ActionResult<PaymentDto>> ProcessPayment(
        [FromBody] InitiatePaymentRequest request,
        CancellationToken ct)
    {
        var currentUserId = GetCurrentUserId();
        var roles = GetCurrentUserRoles();

        // Security check: Only internal services, admins, or the rider themselves can initiate payment
        if (currentUserId.HasValue &&
            !roles.Contains("Admin", StringComparer.OrdinalIgnoreCase) &&
            !roles.Contains("Service", StringComparer.OrdinalIgnoreCase) &&
            currentUserId.Value != request.RiderId)
        {
            throw new ApiFailure(403, "forbidden", "Cannot initiate payment on behalf of another user.");
        }

        var result = await paymentService.ProcessPaymentAsync(request, ct);
        return Ok(result);
    }

    [HttpGet("trips/{tripId:guid}")]
    [Authorize]
    public async Task<ActionResult<PaymentDto>> GetByTripId(
        [FromRoute] Guid tripId,
        CancellationToken ct)
    {
        var currentUserId = GetCurrentUserId()
            ?? throw new ApiFailure(401, "unauthorized", "Authentication is required.");
        var roles = GetCurrentUserRoles();

        var result = await paymentService.GetPaymentByTripIdAsync(tripId, currentUserId, roles, ct);
        return Ok(result);
    }

    [HttpGet("{paymentId:guid}")]
    [Authorize]
    public async Task<ActionResult<PaymentDto>> GetById(
        [FromRoute] Guid paymentId,
        CancellationToken ct)
    {
        var currentUserId = GetCurrentUserId()
            ?? throw new ApiFailure(401, "unauthorized", "Authentication is required.");
        var roles = GetCurrentUserRoles();

        var result = await paymentService.GetPaymentByIdAsync(paymentId, currentUserId, roles, ct);
        return Ok(result);
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<IReadOnlyList<PaymentDto>>> GetMyPayments(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var currentUserId = GetCurrentUserId()
            ?? throw new ApiFailure(401, "unauthorized", "Authentication is required.");

        var result = await paymentService.GetRiderHistoryAsync(currentUserId, page, pageSize, ct);
        return Ok(result);
    }

    [HttpGet("me/wallet")]
    [Authorize]
    public async Task<ActionResult<WalletBalanceDto>> GetMyWallet(
        [FromQuery] string currency = "VND",
        CancellationToken ct = default)
    {
        var currentUserId = GetCurrentUserId()
            ?? throw new ApiFailure(401, "unauthorized", "Authentication is required.");

        var result = await paymentService.GetRiderWalletAsync(currentUserId, currency, ct);
        return Ok(result);
    }

    [HttpGet("drivers/me/earnings")]
    [Authorize(Roles = "Driver,Admin")]
    public async Task<ActionResult<DriverEarningsDto>> GetDriverEarnings(CancellationToken ct)
    {
        var currentUserId = GetCurrentUserId()
            ?? throw new ApiFailure(401, "unauthorized", "Authentication is required.");

        var result = await paymentService.GetDriverEarningsAsync(currentUserId, ct);
        return Ok(result);
    }

    [HttpPost("{paymentId:guid}/refund")]
    [Authorize(Roles = "Admin,Service")]
    public async Task<ActionResult<PaymentDto>> RefundPayment(
        [FromRoute] Guid paymentId,
        [FromBody] RefundPaymentRequest request,
        CancellationToken ct)
    {
        var result = await paymentService.RefundPaymentAsync(paymentId, request, ct);
        return Ok(result);
    }

    [HttpPost("seed")]
    [AllowAnonymous]
    public async Task<IActionResult> TriggerSeed(CancellationToken ct)
    {
        if (!environment.IsDevelopment())
            return Forbid();

        await PaymentSeed.RunAsync(dbContext, ct);
        return Ok(new { message = "Payment seed data applied successfully." });
    }
}
