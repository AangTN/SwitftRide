using System.Text.Json;
using MassTransit;
using SwiftRide.Contracts.Trips;
using SwiftRide.Matching.Application;
using SwiftRide.Matching.Infrastructure;

namespace SwiftRide.Matching.Api;

public sealed class MatchingJobDispatcher(MatchingJobStore jobs, MatchingCoordinator coordinator, IMatchingStore store, IPublishEndpoint bus, MatchingRoutes routes, ILogger<MatchingJobDispatcher>? logger = null)
{
    public async Task<bool> DispatchOneAsync(CancellationToken ct)
    {
        var job = await jobs.ClaimAsync(ct);
        if (job is null) return false;
        try
        {
            var tripId = Guid.Parse(job.TripId);
            var proposalId = Guid.Parse(job.Id);
            if (job.Released)
            {
                if (job.DriverId is not null)
                    await coordinator.ReleaseReservationAsync(job.DriverId, tripId, proposalId, ct);
                await routes.CleanOfferAsync(tripId, proposalId, ct);
                await jobs.CompleteAsync(job, job.ResultSent, ct);
                return true;
            }
            if (job.Confirmed)
            {
                if (job.DriverId is not null)
                    await coordinator.ConfirmReservationAsync(job.DriverId, tripId, proposalId, ct);
                await jobs.CompleteAsync(job, job.ResultSent, ct);
                return true;
            }
            if (job.RequestJson is null) return false;
            if (job.ResultJson is null)
            {
                var request = JsonSerializer.Deserialize<TripMatchingRequested>(job.RequestJson)!;
                TripMatchingResult result;
                try
                {
                    // Price first: a route-provider failure must not strand a reservation.
                    var quoteRequest = new FareQuoteRequest(tripId, proposalId, request.PickupLatitude,
                        request.PickupLongitude, request.DestinationLatitude, request.DestinationLongitude, request.VehicleType);
                    var route = await routes.GetQuoteRouteAsync(quoteRequest, ct);
                    var quote = coordinator.CalculateFareQuote(quoteRequest, route);
                    await PublishPreviewAsync(tripId, proposalId, route, ct);
                    var driver = await coordinator.FindAndReserveNearestAsync(new DriverMatchRequest(tripId, proposalId,
                        request.PickupLatitude, request.PickupLongitude, request.VehicleType, request.ExcludedDriverIds.ToArray()), ct);
                    if (driver is not null) await routes.PrepareOfferPickupAsync(request, driver.DriverId, ct);
                    result = new TripMatchingResult(tripId, proposalId, driver is null ? null :
                        new TripProposalCandidate(proposalId, Guid.Parse(driver.DriverId), quote.Id, quote.TotalAmount,
                            quote.Currency, driver.LegalName, driver.PlateNumber, driver.VehicleName, driver.VehicleImageKey, driver.AvatarImageKey,
                            new FareBreakdown(quote.DistanceMeters, quote.EstimatedDurationSeconds,
                                quote.PricingSnapshot.PerKilometer, quote.PricingSnapshot.PerMinute,
                                quote.PricingSnapshot.Surge, quote.PricingSnapshot.Toll,
                                quote.PricingSnapshot.PromotionFactor, quote.PricingSnapshot.TaxRate)),
                        driver is null ? "no_driver_available" : null);
                }
                catch (MatchingFailureException e) when (e.Code != "trip_already_assigned" && (e.StatusCode < 500 || job.Attempts >= 3))
                {
                    result = new TripMatchingResult(tripId, proposalId, null, e.Code);
                }
                catch (Exception) when (!ct.IsCancellationRequested && job.Attempts >= 3)
                {
                    result = new TripMatchingResult(tripId, proposalId, null, "matching_processing_failed");
                }
                if (result.Candidate is null)
                {
                    var held = await store.FindActiveReservationAsync(job.Id, DateTime.UtcNow, ct);
                    if (held is not null)
                        await coordinator.ReleaseReservationAsync(held.Id, tripId, proposalId, ct);
                }
                await jobs.SaveResultAsync(job, result, ct);
            }
            var current = await jobs.ReloadAsync(job.Id, ct);
            if (current.Released || current.Confirmed) return true; // Next pass applies the newer command.
            if (!current.ResultSent && current.ResultJson is not null)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await bus.Publish(JsonSerializer.Deserialize<TripMatchingResult>(current.ResultJson)!,
                    c => c.MessageId = proposalId, timeout.Token);
            }
            await jobs.CompleteAsync(job, true, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            await jobs.RetryAsync(job, e, ct);
        }
        finally { await jobs.UnlockAsync(job, ct); }
        return true;
    }

    private async Task PublishPreviewAsync(Guid tripId, Guid proposalId, RouteMetrics route, CancellationToken ct)
    {
        if (MatchingRoutes.ToLeg(route) is not { } leg) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try { await bus.Publish(new TripRoutePreview(tripId, proposalId, leg), timeout.Token); }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger?.LogDebug("Route preview for trip {TripId} could not be published: {ErrorType}", tripId, e.GetType().Name);
        }
    }
}

public sealed class MatchingJobWorker(IServiceScopeFactory scopes, ILogger<MatchingJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (await scope.ServiceProvider.GetRequiredService<MatchingJobDispatcher>().DispatchOneAsync(stoppingToken)) continue;
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            { logger.LogWarning("Matching worker error: {ErrorType}", e.GetType().Name); }
            await Task.Delay(250, stoppingToken);
        }
    }
}
