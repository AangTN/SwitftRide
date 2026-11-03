using SwiftRide.Payment.Domain;

namespace SwiftRide.Payment.Application;

public sealed class WalletPaymentGateway(IPaymentStore store) : IPaymentGateway
{
    public string Method => "Wallet";

    public async Task<PaymentGatewayChargeResult> ChargeAsync(PaymentChargeRequest request, CancellationToken ct = default)
    {
        var wallet = await store.FindAccountByCodeAndOwnerAsync(
            LedgerAccountCodes.RiderWallet, request.RiderId, request.Currency, ct);

        if (wallet is null)
        {
            return new PaymentGatewayChargeResult(false, null, "wallet_not_found", "Rider does not have an active wallet.", false);
        }

        var balance = await store.GetAccountBalanceAsync(wallet.Id, ct);
        if (balance < request.Amount)
        {
            return new PaymentGatewayChargeResult(
                false,
                null,
                "insufficient_funds",
                $"Wallet balance ({balance:N0} {request.Currency}) is less than required amount ({request.Amount:N0} {request.Currency}).",
                false);
        }

        var refCode = $"WALLET-{Guid.NewGuid():N}"[..24].ToUpperInvariant();
        return new PaymentGatewayChargeResult(true, refCode);
    }

    public Task<PaymentGatewayChargeResult> RefundAsync(PaymentRefundRequest request, CancellationToken ct = default)
    {
        var refCode = $"WALLET-REF-{Guid.NewGuid():N}"[..24].ToUpperInvariant();
        return Task.FromResult(new PaymentGatewayChargeResult(true, refCode));
    }
}

public sealed class MockExternalCardGateway : IPaymentGateway
{
    public string Method => "MockCard";

    public Task<PaymentGatewayChargeResult> ChargeAsync(PaymentChargeRequest request, CancellationToken ct = default)
    {
        // Special test amount hooks for simulated error cases
        if (request.Amount == 999999m)
        {
            return Task.FromResult(new PaymentGatewayChargeResult(
                false,
                null,
                "gateway_timeout",
                "Payment gateway request timed out.",
                IsTransient: true));
        }

        if (request.Amount == 888888m)
        {
            return Task.FromResult(new PaymentGatewayChargeResult(
                false,
                null,
                "card_declined",
                "The card was declined by the issuing bank.",
                IsTransient: false));
        }

        var refCode = $"CARD-{Guid.NewGuid():N}"[..24].ToUpperInvariant();
        return Task.FromResult(new PaymentGatewayChargeResult(true, refCode));
    }

    public Task<PaymentGatewayChargeResult> RefundAsync(PaymentRefundRequest request, CancellationToken ct = default)
    {
        var refCode = $"CARD-REF-{Guid.NewGuid():N}"[..24].ToUpperInvariant();
        return Task.FromResult(new PaymentGatewayChargeResult(true, refCode));
    }
}

public sealed class PaymentGatewayFactory(IEnumerable<IPaymentGateway> gateways) : IPaymentGatewayFactory
{
    private readonly Dictionary<string, IPaymentGateway> _gateways = gateways
        .ToDictionary(g => g.Method, g => g, StringComparer.OrdinalIgnoreCase);

    public IPaymentGateway GetGateway(string method)
    {
        if (string.IsNullOrWhiteSpace(method))
            method = "Wallet";

        if (_gateways.TryGetValue(method, out var gateway))
            return gateway;

        throw new ArgumentException($"Unsupported payment method: '{method}'. Supported methods: {string.Join(", ", _gateways.Keys)}");
    }
}
