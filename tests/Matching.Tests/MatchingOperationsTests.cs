using SwiftRide.Matching.Application;
using SwiftRide.Matching.Domain;
using Xunit;

namespace SwiftRide.Matching.Tests;

public sealed class MatchingOperationsTests
{
    [Fact]
    public void Fare_is_calculated_from_route_and_pricing_snapshot()
    {
        var amount = CreateFareCalculator().Calculate(new RouteMetrics(1200, 120), new PricingOptions
        {
            PerKilometer = 5000,
            PerMinute = 500,
            Toll = 200,
            Surge = 1.5m,
            PromotionFactor = 0.9m,
            TaxRate = 0.1m
        });

        Assert.Equal(10593m, amount);
    }

    [Fact]
    public void Half_VND_amount_rounds_up()
    {
        var amount = CreateFareCalculator().Calculate(new RouteMetrics(1, 0), new PricingOptions
        {
            PerKilometer = 500
        });

        Assert.Equal(1m, amount);
    }

    [Fact]
    public async Task Matching_retries_the_next_nearest_driver_if_atomic_reservation_loses_a_race()
    {
        var nearest = AvailableDriver("driver-1", 0.001);
        var next = AvailableDriver("driver-2", 0.002);
        var store = new FakeMatchingStore([nearest, next]) { FailedDriverId = nearest.Id };
        var profiles = new FakeProfiles();
        var coordinator = CreateCoordinator(store, profiles, new FakeGoong());

        var match = await coordinator.FindAndReserveNearestAsync(new DriverMatchRequest(
            Guid.NewGuid(), Guid.NewGuid(), 0, 0), CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal(next.Id, match.DriverId);
        Assert.Equal(2, store.ReservationAttempts);
        Assert.Equal("Reserved", store.Reservation!.Status);
    }

    [Fact]
    public async Task Matching_excludes_drivers_rejected_by_the_previous_proposal()
    {
        var excludedDriverId = Guid.NewGuid();
        var excludedDriver = AvailableDriver(excludedDriverId.ToString(), 0.001);
        var nextDriver = AvailableDriver("driver-2", 0.002);
        var store = new FakeMatchingStore([excludedDriver, nextDriver]);
        var coordinator = CreateCoordinator(store, new FakeProfiles(), new FakeGoong());

        var match = await coordinator.FindAndReserveNearestAsync(new DriverMatchRequest(
            Guid.NewGuid(), Guid.NewGuid(), 0, 0, ExcludedDriverIds: [excludedDriverId]), CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal(nextDriver.Id, match.DriverId);
        Assert.Equal([excludedDriverId.ToString()], store.ExcludedDriverIds);
    }

    [Fact]
    public async Task Matching_does_not_assign_a_second_driver_to_an_already_assigned_trip()
    {
        var existingAssignment = new DriverAvailability
        {
            Id = "driver-0",
            Status = "Busy",
            VehicleType = "Motorbike",
            Location = new GeoPoint { Coordinates = [0.001, 0] },
            CurrentTripId = Guid.NewGuid().ToString(),
            CurrentProposalId = Guid.NewGuid().ToString()
        };
        var store = new FakeMatchingStore([AvailableDriver("driver-1", 0.002)])
        {
            TripAssignment = existingAssignment
        };
        var coordinator = CreateCoordinator(store, new FakeProfiles(), new FakeGoong());

        var exception = await Assert.ThrowsAsync<MatchingFailureException>(() =>
            coordinator.FindAndReserveNearestAsync(new DriverMatchRequest(
                Guid.Parse(existingAssignment.CurrentTripId), Guid.NewGuid(), 0, 0), CancellationToken.None));

        Assert.Equal("trip_already_assigned", exception.Code);
        Assert.Equal(0, store.ReservationAttempts);
    }

    [Fact]
    public async Task Fare_quote_uses_Goong_route_metrics_and_persists_its_pricing_snapshot()
    {
        var store = new FakeMatchingStore([]);
        var coordinator = CreateCoordinator(store, new FakeProfiles(), new FakeGoong());

        var quote = await coordinator.CreateFareQuoteAsync(new FareQuoteRequest(
            Guid.NewGuid(), Guid.NewGuid(), 10, 106, 10.1, 106.1), CancellationToken.None);

        Assert.Equal(10500m, quote.TotalAmount);
        Assert.Equal(2000, quote.DistanceMeters);
        Assert.Equal("Motorbike", quote.VehicleType);
        Assert.Same(quote, store.SavedQuote);
        Assert.Equal(5000m, quote.PricingSnapshot.PerKilometer);
    }

    [Fact]
    public async Task Proposal_quote_cannot_be_reused_for_another_trip_or_vehicle_type()
    {
        var existingQuote = new FareQuote
        {
            TripId = Guid.NewGuid().ToString(),
            ProposalId = Guid.NewGuid().ToString(),
            VehicleType = "Motorbike"
        };
        var store = new FakeMatchingStore([]) { ExistingQuote = existingQuote };
        var coordinator = CreateCoordinator(store, new FakeProfiles(), new FakeGoong());

        var exception = await Assert.ThrowsAsync<MatchingFailureException>(() =>
            coordinator.CreateFareQuoteAsync(new FareQuoteRequest(
                Guid.NewGuid(), Guid.Parse(existingQuote.ProposalId), 10, 106, 10.1, 106.1, "Car4Seats"),
                CancellationToken.None));

        Assert.Equal("proposal_quote_conflict", exception.Code);
    }

    private static MatchingCoordinator CreateCoordinator(
        FakeMatchingStore store, FakeProfiles profiles, FakeGoong goong) =>
        new(store, profiles, goong, CreateFareCalculator(), new MatchingOptions(), new PricingOptions());

    private static FareCalculator CreateFareCalculator() =>
        new(new StandardFareStrategy(), new SurgeFareStrategy(), new PromotionFareStrategy());
    private static DriverAvailability AvailableDriver(string id, double longitude) => new()
    {
        Id = id,
        Status = "Available",
        VehicleType = "Motorbike",
        Location = new GeoPoint { Coordinates = [longitude, 0] },
        LocationUpdatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private sealed class FakeMatchingStore(IReadOnlyList<DriverAvailability> candidates) : IMatchingStore
    {
        public Task<DriverAvailability?> UpdateGpsAsync(string driverId, DriverGpsRequest gps, DateTime now, CancellationToken ct) =>
            Task.FromResult<DriverAvailability?>(null);
        public string? FailedDriverId { get; init; }
        public int ReservationAttempts { get; private set; }
        public IReadOnlyCollection<string> ExcludedDriverIds { get; private set; } = [];
        public DriverAvailability? Reservation { get; private set; }
        public DriverAvailability? TripAssignment { get; init; }
        public FareQuote? ExistingQuote { get; init; }
        public FareQuote? SavedQuote { get; private set; }
        public Task<bool> UpdateAvailabilityAsync(
            string driverId, string vehicleType, DriverLocationUpdate update, DateTime now, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<DriverAvailability>> FindNearbyAvailableDriversAsync(
            GeoPoint pickup, string vehicleType, DateTime locationUpdatedAfter, double maxDistanceMeters,
            int maxCandidates, DateTime now, IReadOnlyCollection<string> excludedDriverIds, CancellationToken ct)
        {
            ExcludedDriverIds = excludedDriverIds;
            return Task.FromResult<IReadOnlyList<DriverAvailability>>(
                candidates.Where(candidate => !excludedDriverIds.Contains(candidate.Id, StringComparer.Ordinal)).ToArray());
        }

        public Task<DriverAvailability?> FindActiveReservationAsync(string proposalId, DateTime now, CancellationToken ct) =>
            Task.FromResult(Reservation is not null && Reservation.ReservedUntil > now ? Reservation : null);

        public Task<DriverAvailability?> FindActiveAssignmentForTripAsync(
            string tripId, DateTime now, CancellationToken ct) =>
            Task.FromResult(TripAssignment is not null && TripAssignment.CurrentTripId == tripId
                ? TripAssignment
                : Reservation is not null && Reservation.CurrentTripId == tripId &&
                    (Reservation.Status == "Busy" || Reservation.ReservedUntil > now)
                    ? Reservation
                    : null);

        public Task<bool> TryReserveDriverAsync(
            string driverId, string vehicleType, GeoPoint expectedLocation, string tripId, string proposalId,
            DateTime locationUpdatedAfter, DateTime now, DateTime reservedUntil, CancellationToken ct)
        {
            ReservationAttempts++;
            if (driverId == FailedDriverId) return Task.FromResult(false);
            var candidate = candidates.Single(x => x.Id == driverId);
            Reservation = new DriverAvailability
            {
                Id = driverId,
                Status = "Reserved",
                VehicleType = vehicleType,
                Location = candidate.Location,
                LocationUpdatedAt = candidate.LocationUpdatedAt,
                CurrentTripId = tripId,
                CurrentProposalId = proposalId,
                ReservedUntil = reservedUntil,
                UpdatedAt = now
            };
            return Task.FromResult(true);
        }

        public Task<bool> ConfirmReservationAsync(
            string driverId, string tripId, string proposalId, DateTime now, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<bool> ReleaseReservationAsync(
            string driverId, string tripId, string proposalId, DateTime now, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<FareQuote> SaveQuoteAsync(FareQuote quote, CancellationToken ct)
        {
            if (ExistingQuote is not null) return Task.FromResult(ExistingQuote);
            SavedQuote = quote;
            return Task.FromResult(quote);
        }
    }

    private sealed class FakeProfiles : IDriverProfileReader
    {
        public Task<DriverProfile?> FindAsync(string userId, CancellationToken ct) =>
            Task.FromResult<DriverProfile?>(new DriverProfile
            {
                Id = userId,
                LegalName = userId,
                PlateNumber = $"{userId}-plate",
                VehicleName = "Test motorbike",
                VehicleType = "Motorbike"
            });
    }

    private sealed class FakeGoong : IGoongClient
    {
        public Task<RouteMetrics> GetRouteAsync(
            double originLatitude, double originLongitude,
            double destinationLatitude, double destinationLongitude,
            string vehicleType, CancellationToken ct) =>
            Task.FromResult(new RouteMetrics(2000, 60));

        public Task<GeocodedAddress?> GeocodeAsync(string address, CancellationToken ct) =>
            Task.FromResult<GeocodedAddress?>(null);

        public Task<GeocodedAddress?> ReverseGeocodeAsync(double latitude, double longitude, CancellationToken ct) =>
            Task.FromResult<GeocodedAddress?>(null);

        public Task<IReadOnlyList<PlaceSuggestion>> SearchPlacesAsync(string input, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PlaceSuggestion>>([]);
    }
}
