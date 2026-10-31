namespace SwiftRide.Auth.Domain;

public static class RoleNames
{
    public const string Rider = "Rider", Driver = "Driver", Admin = "Admin";
}

public static class ClientApps
{
    public const string Rider = "RiderApp", Driver = "DriverApp", Admin = "AdminApp";

    public static string? RoleFor(string app) => app switch
    {
        Rider => RoleNames.Rider,
        Driver => RoleNames.Driver,
        Admin => RoleNames.Admin,
        _ => null
    };
}

public static class RoleIds
{
    public static readonly Guid Rider = Guid.Parse("10000000-0000-0000-0000-000000000001");
    public static readonly Guid Driver = Guid.Parse("10000000-0000-0000-0000-000000000002");
    public static readonly Guid Admin = Guid.Parse("10000000-0000-0000-0000-000000000003");
}

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PhoneNumber { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Status { get; set; } = "Active";
    public DateTimeOffset PhoneVerifiedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<UserRole> UserRoles { get; set; } = [];
}

public sealed class Role
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
}

public sealed class UserRole
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public Role Role { get; set; } = null!;
}

public sealed class OtpChallenge
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PhoneNumber { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string ClientApp { get; set; } = "";
    public string? RegistrationFullName { get; set; }
    public string CodeHash { get; set; } = "";
    public string RequestIp { get; set; } = "";
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset? InvalidatedAt { get; set; }
}

public sealed class AuthSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string ClientApp { get; set; } = "";
    public string? DeviceName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevocationReason { get; set; }
}

public sealed class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public Guid? ReplacedByTokenId { get; set; }
}
