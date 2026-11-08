using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwiftRide.Trip.Api;
using SwiftRide.Trip.Domain;
using SwiftRide.Trip.Infrastructure;
using Xunit;
using TripEntity = SwiftRide.Trip.Domain.Trip;

namespace SwiftRide.Trip.Tests;

public sealed class RiderBookingTests
{
    [Theory]
    [InlineData(TripStatus.Requested)]
    [InlineData(TripStatus.Matching)]
    [InlineData(TripStatus.AwaitingRiderApproval)]
    [InlineData(TripStatus.AwaitingDriverResponse)]
    [InlineData(TripStatus.DriverAccepted)]
    [InlineData(TripStatus.EnRoute)]
    [InlineData(TripStatus.PickedUp)]
    public async Task New_key_is_rejected_until_the_rider_is_dropped_off(TripStatus status)
    {
        await using var f = new Fixture();
        var active = await f.Seed(status);
        var result = Assert.IsType<ConflictObjectResult>(await f.Controller().Create(Request, "new", default));
        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal("active_trip_exists", problem.Title);
        Assert.Equal(active.Id, problem.Extensions["tripId"]);
        Assert.Single(await f.Db.Trips.ToListAsync());
        Assert.Empty(await f.Db.OutboxMessages.ToListAsync());
    }

    [Theory]
    [InlineData(TripStatus.DroppedOff, null)]
    [InlineData(TripStatus.PaymentPending, "Pending")]
    [InlineData(TripStatus.PaymentPending, "Failed")]
    [InlineData(TripStatus.Paid, "Succeeded")]
    [InlineData(TripStatus.Cancelled, null)]
    [InlineData(TripStatus.Failed, null)]
    public async Task Finished_journey_allows_booking_even_with_unsettled_payment(TripStatus status, string? paymentStatus)
    {
        await using var f = new Fixture();
        await f.Seed(status, paymentStatus);
        Assert.IsType<NoContentResult>(await f.Controller().Current(default));
        Assert.IsType<CreatedAtActionResult>(await f.Controller().Create(Request, "new", default));
        Assert.Equal(2, await f.Db.Trips.CountAsync());
        Assert.Single(await f.Db.TripProposals.ToListAsync());
        Assert.Single(await f.Db.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task Replaying_old_key_still_returns_its_trip_when_a_new_trip_is_active()
    {
        await using var f = new Fixture();
        await f.Seed(TripStatus.Paid);
        var current = new TripEntity { RiderId = f.Rider, IdempotencyKey = "second", Status = TripStatus.PickedUp };
        f.Db.Add(current);
        await f.Db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await f.Controller().Create(Request, "original", default));
        Assert.Equal(2, await f.Db.Trips.CountAsync());
    }

    [Fact]
    public async Task Concurrent_different_keys_create_one_trip_proposal_and_outbox()
    {
        var bothArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        async Task WaitForBoth()
        {
            if (Interlocked.Increment(ref arrived) == 2) bothArrived.SetResult();
            await bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var name = "booking_" + Guid.NewGuid().ToString("N");
        await using var first = new Fixture(name, WaitForBoth);
        await using var second = new Fixture(name, WaitForBoth, first.Rider);
        var results = await Task.WhenAll(
            first.Controller().Create(Request, "first", default),
            second.Controller().Create(Request, "second", default));
        Assert.Single(results, x => x is CreatedAtActionResult);
        Assert.Single(results, x => x is ConflictObjectResult);
        Assert.Single(await first.Db.Trips.AsNoTracking().ToListAsync());
        Assert.Single(await first.Db.TripProposals.AsNoTracking().ToListAsync());
        Assert.Single(await first.Db.OutboxMessages.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Database_rejects_a_second_serving_trip_even_without_controller_checks()
    {
        await using var f = new Fixture();
        await f.Seed(TripStatus.Matching);
        f.Db.Add(new TripEntity { RiderId = f.Rider, IdempotencyKey = "other", Status = TripStatus.PickedUp });
        await Assert.ThrowsAsync<DbUpdateException>(() => f.Db.SaveChangesAsync());
    }

    private static readonly CreateTripRequest Request = new("Pickup", 10.78, 106.69, "Destination", 10.79, 106.70);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly HttpClient http;
        private readonly ServiceProvider services;
        public Guid Rider { get; }
        public TripDbContext Db { get; }

        public Fixture(string? name = null, Func<Task>? beforeProfile = null, Guid? rider = null)
        {
            Rider = rider ?? Guid.NewGuid();
            connection = new SqliteConnection($"Data Source={name ?? Guid.NewGuid().ToString("N")};Mode=Memory;Cache=Shared");
            connection.Open();
            Db = new TripDbContext(new DbContextOptionsBuilder<TripDbContext>().UseSqlite(connection)
                .ReplaceService<IModelCustomizer, MatchingWorkflowTests.SqliteUtcModelCustomizer>().Options);
            Db.Database.EnsureCreated();
            http = new HttpClient(new ProfileHandler(Rider, beforeProfile)) { BaseAddress = new Uri("http://auth/") };
            services = new ServiceCollection().AddSingleton(new RiderProfileClient(http)).BuildServiceProvider();
        }

        public async Task<TripEntity> Seed(TripStatus status, string? paymentStatus = null)
        {
            var trip = new TripEntity { RiderId = Rider, IdempotencyKey = "original", Status = status, PaymentStatus = paymentStatus };
            Db.Add(trip);
            await Db.SaveChangesAsync();
            return trip;
        }

        public TripsController Controller()
        {
            var context = new DefaultHttpContext
            {
                RequestServices = services,
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("sub", Rider.ToString()), new Claim(ClaimTypes.Role, "Rider")], "test"))
            };
            context.Request.Headers.Authorization = "Bearer test";
            return new TripsController(Db, new SilentRealtime(), new TripMatchingNotifier(Db, TimeProvider.System),
                new TripPaymentNotifier(Db, TimeProvider.System), TimeProvider.System, Options.Create(new TripWorkflowOptions()))
            { ControllerContext = new ControllerContext { HttpContext = context } };
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
            await services.DisposeAsync();
            http.Dispose();
        }
    }

    private sealed class ProfileHandler(Guid rider, Func<Task>? beforeProfile) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (beforeProfile is not null) await beforeProfile();
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(new RiderProfile(rider, "Test rider", "+84900000001")) };
        }
    }

    private sealed class SilentRealtime : ITripRealtimePublisher
    {
        public Task PublishTripChangedAsync(TripEntity trip, CancellationToken ct) => Task.CompletedTask;
        public Task PublishDriverInvitationAsync(TripEntity trip, TripProposal proposal, CancellationToken ct) => Task.CompletedTask;
        public Task PublishRideCompletedAsync(TripEntity trip, CancellationToken ct) => Task.CompletedTask;
    }
}
