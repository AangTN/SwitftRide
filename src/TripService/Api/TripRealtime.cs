using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SwiftRide.Contracts.Realtime;
using SwiftRide.Trip.Domain;
using SwiftRide.Trip.Infrastructure;

namespace SwiftRide.Trip.Api;

public interface ITripRealtimePublisher
{
    Task PublishTripChangedAsync(Trip trip, CancellationToken ct);
    Task PublishDriverInvitationAsync(Trip trip, TripProposal proposal, CancellationToken ct);
    Task PublishRideCompletedAsync(Trip trip, CancellationToken ct);
}

public sealed class TripRealtimePublisher(IHubContext<TripHub> hub) : ITripRealtimePublisher
{
    public Task PublishTripChangedAsync(Trip trip, CancellationToken ct) => Task.WhenAll(
        hub.Clients.User(trip.RiderId.ToString()).SendAsync("TripChanged", new TripChanged(trip.Id, trip.Status.ToString(), trip.StateVersion), ct),
        trip.DriverId is Guid driver ? hub.Clients.User(driver.ToString()).SendAsync("TripChanged", new TripChanged(trip.Id, trip.Status.ToString(), trip.StateVersion), ct) : Task.CompletedTask);

    public Task PublishDriverInvitationAsync(Trip trip, TripProposal proposal, CancellationToken ct) =>
        hub.Clients.User(proposal.DriverId!.Value.ToString()).SendAsync("DriverInvitation", new DriverInvitation(trip.Id, proposal.Id, proposal.DriverResponseDeadline!.Value), ct);

    public Task PublishRideCompletedAsync(Trip trip, CancellationToken ct) =>
        Task.WhenAll(hub.Clients.User(trip.RiderId.ToString()).SendAsync("RideCompleted", new RideCompleted(trip.Id, trip.PaymentId!), ct),
            trip.DriverId is Guid driver ? hub.Clients.User(driver.ToString()).SendAsync("RideCompleted", new RideCompleted(trip.Id, trip.PaymentId!), ct) : Task.CompletedTask);
}

public sealed class SubjectUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => connection.User?.FindFirst("sub")?.Value;
}

[Authorize]
public sealed class TripHub(TripDbContext db) : Hub
{
    internal static string TripGroup(Guid tripId) => $"trip:{tripId:N}";

    public async Task SubscribeTrip(Guid tripId)
    {
        if (!Guid.TryParse(Context.User?.FindFirst("sub")?.Value, out var userId)) throw new HubException("invalid_subject");
        var permitted = await db.Trips.AnyAsync(x => x.Id == tripId && (x.RiderId == userId || x.DriverId == userId ||
            x.Proposals.Any(p => p.DriverId == userId && p.Status == ProposalStatus.AwaitingDriverResponse)), Context.ConnectionAborted);
        if (!permitted) throw new HubException("trip_forbidden");
        await Groups.AddToGroupAsync(Context.ConnectionId, TripGroup(tripId), Context.ConnectionAborted);
    }

    public Task UnsubscribeTrip(Guid tripId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, TripGroup(tripId), Context.ConnectionAborted);
}
