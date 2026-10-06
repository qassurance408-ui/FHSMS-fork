using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FHSMS.Application.Common.Interfaces;
using FHSMS.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FHSMS.Infrastructure.PaymentProviders;

/// <summary>
/// Telebirr's H5 C2B Web Payment integration, built from Ethio Telecom's
/// official "H5 C2B Web Payment Integration Quick Guide"
/// (developer.ethiotelecom.et) and cross-checked against several published
/// third-party implementations of that same guide.
///
/// Flow implemented here:
///   1. ApplyFabricTokenAsync - POST {ApiBaseUrl}/payment/v1/token with the
///      AppSecret, using FabricAppId as the X-APP-Key header. Returns a
///      short-lived bearer token (already formatted "Bearer xxx").
///   2. InitiateAsync - signs a pre-order request with the merchant's RSA
///      private key and POSTs it to {ApiBaseUrl}/payment/v1/merchant/preOrder
///      using that token. Telebirr returns a prepay_id. A SECOND signature is
///      then built over just the fields the hosted checkout page expects, and
///      the payer is redirected to {WebCheckoutBaseUrl}?...&sign=...
///   3. ParseCallbackAsync - Telebirr POSTs a JSON notification to our
///      webhook once the payer finishes paying. The notify payload's own
///      signature is verified (fails closed if that doesn't check out), but
///      - following the same "never trust the webhook body alone" principle
///      ChapaPaymentProvider already uses - the actual payment is only
///      confirmed by a fresh, separately-signed queryOrder call
///      (QueryOrderAsync) straight to Telebirr. Only queryOrder's own
///      order_status and total_amount are ever recorded as the payment.
///
/// *** READ BEFORE GOING LIVE ***
/// Unlike Chapa (an open, self-service REST API this codebase was verified
/// directly against), Telebirr integration requires Ethio Telecom's merchant
/// onboarding and their developer-portal sandbox - there is no way to
/// compile-test this against a live account from here. Two things in
/// particular are "best effort, confirm before trusting with real money":
///   - The exact set of fields included in the RSA signature's canonical
///     string (see BuildCanonicalString's remarks).
///   - The exact field names/shape of the notify JSON body (see
///     TelebirrNotifyPayload's remarks) - this is why ParseCallbackAsync
///     insists on reconciling with queryOrder rather than trusting notify's
///     own amount/status, even once its signature passes.
/// Test every step against the developer portal's sandbox before processing
/// a single real transaction.
/// </summary>
public class TelebirrPaymentProvider : IPaymentProvider, IInitiatablePaymentProvider
{
    private const string SignType = "SHA256WithRSA";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly PaymentProviderSettings.TelebirrCredential _credential;
    private readonly PaymentProviderSettings _settings;
    private readonly ILogger<TelebirrPaymentProvider> _logger;

    public TelebirrPaymentProvider(HttpClient http, IOptions<PaymentProviderSettings> settings, ILogger<TelebirrPaymentProvider> logger)
    {
        _http = http;
        _settings = settings.Value;
        _credential = settings.Value.Telebirr;
        _logger = logger;
    }

    public PaymentMethod Method => PaymentMethod.Telebirr;

    private bool IsConfigured =>
        !string.IsNullOrEmpty(_credential.FabricAppId) &&
        !string.IsNullOrEmpty(_credential.AppSecret) &&
        !string.IsNullOrEmpty(_credential.MerchantAppId) &&
        !string.IsNullOrEmpty(_credential.MerchantCode) &&
        !string.IsNullOrEmpty(_credential.MerchantPrivateKeyPem) &&
        !string.IsNullOrEmpty(_credential.ApiBaseUrl) &&
        !string.IsNullOrEmpty(_credential.WebCheckoutBaseUrl);

    public async Task<InitiatePaymentResult> InitiateAsync(Guid invoiceId, decimal amount, string customerEmail, string customerName, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            return new InitiatePaymentResult(false, null, null,
                "Telebirr isn't configured (PaymentProviders:Telebirr needs FabricAppId/AppSecret/MerchantAppId/MerchantCode/MerchantPrivateKeyPem/ApiBaseUrl/WebCheckoutBaseUrl).");

        var token = await ApplyFabricTokenAsync(cancellationToken);
        if (token is null)
            return new InitiatePaymentResult(false, null, null, "Could not obtain a Telebirr Fabric token - check the configured FabricAppId/AppSecret.");

        // The invoice's own ID doubles as merch_order_id - same reasoning as
        // Chapa's tx_ref: it's how the notify callback and queryOrder get
        // tied back to one of our invoices, since Telebirr has no concept of
        // our InvoiceId. Telebirr requires merch_order_id to be alphanumeric
        // only, so "N" formatting (no dashes) is used, same as Chapa/Bank.
        var merchOrderId = invoiceId.ToString("N");
        var nonceStr = Guid.NewGuid().ToString("N");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        var bizContent = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["appid"] = _credential.MerchantAppId!,
            ["business_type"] = _credential.BusinessType,
            ["merch_code"] = _credential.MerchantCode!,
            ["merch_order_id"] = merchOrderId,
            ["notify_url"] = BuildUrl(_settings.ApiPublicBaseUrl, "api/webhooks/payments/telebirr") ?? "",
            ["redirect_url"] = BuildUrl(_settings.FrontendPublicBaseUrl, $"invoices/{invoiceId}") ?? "",
            ["timeout_express"] = $"{_credential.TimeoutExpressMinutes}m",
            ["title"] = $"Invoice {merchOrderId}",
            ["total_amount"] = amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            ["trade_type"] = "Checkout",
            ["trans_currency"] = "ETB"
        };

        PreOrderResponse? preOrder;
        string raw;
        try
        {
            (preOrder, raw) = await CallSignedApiAsync<PreOrderResponse>(
                "payment/v1/merchant/preOrder", "payment.preorder", bizContent, nonceStr, timestamp, token, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telebirr preOrder call failed for invoice {InvoiceId}", invoiceId);
            return new InitiatePaymentResult(false, null, null, "Could not reach Telebirr.");
        }

        if (preOrder?.BizContent?.PrepayId is not { Length: > 0 } prepayId)
        {
            _logger.LogError("Telebirr preOrder did not return a prepay_id for invoice {InvoiceId}: {Body}", invoiceId, raw);
            return new InitiatePaymentResult(false, null, null, preOrder?.Msg is { Length: > 0 } msg ? $"Telebirr rejected the request: {msg}" : "Telebirr did not accept the payment request.");
        }

        // Build the checkout redirect: a SECOND, smaller signed payload over
        // just the fields Telebirr's hosted H5 page expects (appid,
        // merch_code, nonce_str, prepay_id, timestamp, trade_type, version) -
        // distinct from the preOrder request's own signature above. The
        // payer is sent straight to Telebirr's page; we never see their
        // wallet credentials.
        var checkoutTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var checkoutNonce = Guid.NewGuid().ToString("N");
        var checkoutFields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["appid"] = _credential.MerchantAppId!,
            ["merch_code"] = _credential.MerchantCode!,
            ["nonce_str"] = checkoutNonce,
            ["prepay_id"] = prepayId,
            ["timestamp"] = checkoutTimestamp,
            ["trade_type"] = "Checkout",
            ["version"] = "1.0"
        };

        string checkoutSign;
        try
        {
            checkoutSign = Sign(checkoutFields, _credential.MerchantPrivateKeyPem!);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telebirr: failed to sign checkout redirect for invoice {InvoiceId} - check MerchantPrivateKeyPem is a valid PEM key", invoiceId);
            return new InitiatePaymentResult(false, null, null, "Could not sign the Telebirr checkout redirect - check the configured private key.");
        }

        var query = string.Join("&", checkoutFields.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"))
            + $"&sign={Uri.EscapeDataString(checkoutSign)}&sign_type={SignType}";
        var checkoutUrl = $"{_credential.WebCheckoutBaseUrl!.TrimEnd('/')}?{query}";

        return new InitiatePaymentResult(true, checkoutUrl, merchOrderId, null);
    }

    public async Task<PaymentCallbackResult> ParseCallbackAsync(string rawPayload, IDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        TelebirrNotifyPayload? payload;
        try
        {
            // Telebirr sometimes wraps the actual fields in a top-level
            // "data" envelope - try that first, fall back to the flat shape.
            using var doc = JsonDocument.Parse(rawPayload);
            var root = doc.RootElement.TryGetProperty("data", out var dataEl) ? dataEl : doc.RootElement;
            payload = JsonSerializer.Deserialize<TelebirrNotifyPayload>(root.GetRawText(), JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Telebirr webhook: malformed JSON payload");
            return new PaymentCallbackResult(false, null, 0, null, rawPayload, "Malformed payload.");
        }

        if (payload?.MerchOrderId is not { Length: > 0 } merchOrderId || !Guid.TryParse(merchOrderId, out var invoiceId))
        {
            _logger.LogWarning("Telebirr webhook: merch_order_id missing or not a recognizable invoice ID ({MerchOrderId})", payload?.MerchOrderId);
            return new PaymentCallbackResult(false, null, 0, payload?.MerchOrderId, rawPayload, "Unrecognized transaction reference.");
        }

        if (string.IsNullOrEmpty(_credential.TelebirrPublicKeyPem))
        {
            _logger.LogError("Telebirr webhook received but no TelebirrPublicKeyPem is configured - refusing to trust it unverified");
            return new PaymentCallbackResult(false, invoiceId, 0, merchOrderId, rawPayload, "Telebirr callback verification isn't configured.");
        }

        if (!VerifyNotifySignature(payload, _credential.TelebirrPublicKeyPem))
        {
            _logger.LogWarning("Telebirr webhook: signature verification failed for {MerchOrderId}", merchOrderId);
            return new PaymentCallbackResult(false, invoiceId, 0, merchOrderId, rawPayload, "Signature validation failed.");
        }

        // Signature passing only proves the notify came from Telebirr's key -
        // it does not, by itself, prove the field names above were parsed
        // correctly (see class remarks). So the amount/status that actually
        // gets recorded always comes from our own fresh, separately-signed
        // queryOrder call, never from the notify body - same principle as
        // ChapaPaymentProvider.VerifyAsync.
        var confirmed = await QueryOrderAsync(merchOrderId, cancellationToken);
        if (confirmed is not { Paid: true })
        {
            _logger.LogWarning("Telebirr webhook: queryOrder did not confirm success for {MerchOrderId} (status: {Status})", merchOrderId, confirmed?.OrderStatus ?? "unreachable");
            return new PaymentCallbackResult(false, invoiceId, 0, merchOrderId, rawPayload, "Payment not confirmed by Telebirr's queryOrder endpoint.");
        }

        return new PaymentCallbackResult(true, invoiceId, confirmed.TotalAmount, confirmed.TransId ?? merchOrderId, rawPayload, null);
    }

    /// <summary>
    /// POST {ApiBaseUrl}/payment/v1/token with the AppSecret, X-APP-Key:
    /// FabricAppId. Returns the token string exactly as Telebirr sends it
    /// (already includes the "Bearer " prefix per the documented response
    /// shape), ready to use as-is in an Authorization header.
    /// </summary>
    private async Task<string?> ApplyFabricTokenAsync(CancellationToken cancellationToken)
    {
        var url = BuildUrl(_credential.ApiBaseUrl, "payment/v1/token")!;
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new { appSecret = _credential.AppSecret })
        };
        request.Headers.TryAddWithoutValidation("X-APP-Key", _credential.FabricAppId);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Telebirr token request failed ({Status}): {Body}", response.StatusCode, raw);
                return null;
            }

            var parsed = JsonSerializer.Deserialize<TokenResponse>(raw, JsonOptions);
            if (parsed?.Token is not { Length: > 0 } token)
            {
                _logger.LogError("Telebirr token response had no token: {Body}", raw);
                return null;
            }

            return token;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telebirr token request threw");
            return null;
        }
    }

    /// <summary>Signs and POSTs a biz_content envelope to one of Telebirr's merchant endpoints, with the given Fabric token as Authorization.</summary>
    private async Task<(T? Parsed, string Raw)> CallSignedApiAsync<T>(
        string relativePath, string method, SortedDictionary<string, string> bizContent,
        string nonceStr, string timestamp, string fabricToken, CancellationToken cancellationToken)
        where T : class
    {
        var canonical = new SortedDictionary<string, string>(bizContent, StringComparer.Ordinal)
        {
            ["method"] = method,
            ["nonce_str"] = nonceStr,
            ["timestamp"] = timestamp,
            ["version"] = "1.0"
        };
        var sign = Sign(canonical, _credential.MerchantPrivateKeyPem!);

        var body = new
        {
            biz_content = bizContent,
            method,
            nonce_str = nonceStr,
            sign_type = SignType,
            timestamp,
            version = "1.0",
            sign
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(_credential.ApiBaseUrl, relativePath))
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.TryAddWithoutValidation("X-APP-Key", _credential.FabricAppId);
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(fabricToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? fabricToken : $"Bearer {fabricToken}");

        using var response = await _http.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Telebirr {Method} call failed ({Status}): {Body}", method, response.StatusCode, raw);
            return (null, raw);
        }

        return (JsonSerializer.Deserialize<T>(raw, JsonOptions), raw);
    }

    /// <summary>
    /// Server-to-server status check - POST {ApiBaseUrl}/payment/v1/merchant/queryOrder.
    /// This, not the notify webhook, is the authoritative source for whether
    /// an order actually succeeded (see class remarks).
    /// </summary>
    private async Task<QueryOrderResult?> QueryOrderAsync(string merchOrderId, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            return null;

        var token = await ApplyFabricTokenAsync(cancellationToken);
        if (token is null)
            return null;

        var bizContent = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["appid"] = _credential.MerchantAppId!,
            ["merch_code"] = _credential.MerchantCode!,
            ["merch_order_id"] = merchOrderId
        };
        var nonceStr = Guid.NewGuid().ToString("N");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        try
        {
            var (parsed, raw) = await CallSignedApiAsync<QueryOrderResponse>(
                "payment/v1/merchant/queryOrder", "payment.queryorder", bizContent, nonceStr, timestamp, token, cancellationToken);

            if (parsed?.BizContent is not { } content)
            {
                _logger.LogError("Telebirr queryOrder returned no biz_content for {MerchOrderId}: {Body}", merchOrderId, raw);
                return null;
            }

            // PAY_SUCCESS is the documented success value for queryOrder's
            // order_status (distinct from notify's "Completed" - see class
            // remarks on the three legs not sharing a vocabulary).
            var paid = string.Equals(content.OrderStatus, "PAY_SUCCESS", StringComparison.OrdinalIgnoreCase);
            return new QueryOrderResult(paid, content.OrderStatus, content.TotalAmount, content.TransId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telebirr queryOrder call failed for {MerchOrderId}", merchOrderId);
            return null;
        }
    }

    /// <summary>
    /// RSA-PSS/SHA-256 (MGF1-SHA256, salt length = hash length, i.e. .NET's
    /// default RSASignaturePadding.Pss) over "key1=value1&amp;key2=value2..."
    /// with keys sorted ordinally ascending - the canonicalization and
    /// padding scheme most consistently described for this flow. CONFIRM
    /// against your own sandbox before trusting with real money: if Telebirr
    /// rejects every signed request, the two most likely causes are (a) this
    /// should be PKCS#1 v1.5 padding instead of PSS, or (b) a different field
    /// subset participates in the canonical string than the one built here.
    /// </summary>
    private static string Sign(SortedDictionary<string, string> fields, string privateKeyPem)
    {
        var signableString = string.Join("&", fields.Select(kv => $"{kv.Key}={kv.Value}"));
        var bytes = Encoding.UTF8.GetBytes(signableString);

        using var rsa = RSA.Create();
        rsa.ImportFromPem(NormalizePem(privateKeyPem, "PRIVATE KEY"));
        var signatureBytes = rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return Convert.ToBase64String(signatureBytes);
    }

    /// <summary>
    /// Verifies the notify payload's signature. Telebirr signs the
    /// transaction id under the key "trans_id" even though the JSON field on
    /// the wire is "transId" - both spellings are tried (a quirk documented
    /// by multiple independent implementations of this same notify format),
    /// so a genuine notification isn't rejected over a naming mismatch.
    /// </summary>
    private bool VerifyNotifySignature(TelebirrNotifyPayload payload, string publicKeyPem)
    {
        if (string.IsNullOrEmpty(payload.Sign))
            return false;

        string[] transIdKeys = { "trans_id", "transId" };
        foreach (var transIdKey in transIdKeys)
        {
            var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["appid"] = payload.AppId ?? "",
                ["merch_code"] = payload.MerchCode ?? "",
                ["merch_order_id"] = payload.MerchOrderId ?? "",
                ["payment_order_id"] = payload.PaymentOrderId ?? "",
                [transIdKey] = payload.TransId ?? "",
                ["total_amount"] = payload.TotalAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                ["trans_currency"] = payload.TransCurrency ?? "",
                ["trade_status"] = payload.TradeStatus ?? ""
            };
            var signableString = string.Join("&", fields.Select(kv => $"{kv.Key}={kv.Value}"));
            var bytes = Encoding.UTF8.GetBytes(signableString);

            try
            {
                using var rsa = RSA.Create();
                rsa.ImportFromPem(NormalizePem(publicKeyPem, "PUBLIC KEY"));
                var signatureBytes = Convert.FromBase64String(payload.Sign.Replace(' ', '+'));
                if (rsa.VerifyData(bytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                    return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Telebirr: failed to verify notify signature - check TelebirrPublicKeyPem is a valid PEM key");
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Ethio Telecom sometimes issues keys as bare base64 (a single long line,
    /// no -----BEGIN/END----- armor). RSA.ImportFromPem needs proper PEM, so
    /// wrap a bare-base64 value automatically; a value that is already valid
    /// PEM is passed through untouched.
    /// </summary>
    private static string NormalizePem(string key, string label)
    {
        var trimmed = key.Trim();
        if (trimmed.StartsWith("-----BEGIN", StringComparison.Ordinal))
            return trimmed;

        var wrapped = string.Join("\n", Chunk(trimmed.Replace("\r", "").Replace("\n", ""), 64));
        return $"-----BEGIN {label}-----\n{wrapped}\n-----END {label}-----";
    }

    private static IEnumerable<string> Chunk(string value, int size)
    {
        for (var i = 0; i < value.Length; i += size)
            yield return value.Substring(i, Math.Min(size, value.Length - i));
    }

    private static string? BuildUrl(string? baseUrl, string path)
        => string.IsNullOrEmpty(baseUrl) ? null : $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

    private record QueryOrderResult(bool Paid, string? OrderStatus, decimal TotalAmount, string? TransId);

    private class TokenResponse
    {
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("expirationDate")] public string? ExpirationDate { get; set; }
    }

    private class PreOrderResponse
    {
        [JsonPropertyName("result")] public string? Result { get; set; }
        [JsonPropertyName("code")] public string? Code { get; set; }
        [JsonPropertyName("msg")] public string? Msg { get; set; }
        [JsonPropertyName("biz_content")] public PreOrderBizContent? BizContent { get; set; }
    }

    private class PreOrderBizContent
    {
        [JsonPropertyName("merch_order_id")] public string? MerchOrderId { get; set; }
        [JsonPropertyName("prepay_id")] public string? PrepayId { get; set; }
    }

    private class QueryOrderResponse
    {
        [JsonPropertyName("result")] public string? Result { get; set; }
        [JsonPropertyName("msg")] public string? Msg { get; set; }
        [JsonPropertyName("biz_content")] public QueryOrderBizContent? BizContent { get; set; }
    }

    private class QueryOrderBizContent
    {
        [JsonPropertyName("merch_order_id")] public string? MerchOrderId { get; set; }
        [JsonPropertyName("order_status")] public string? OrderStatus { get; set; }
        [JsonPropertyName("total_amount")] public decimal TotalAmount { get; set; }
        [JsonPropertyName("trans_id")] public string? TransId { get; set; }
        [JsonPropertyName("trans_time")] public string? TransTime { get; set; }
    }

    /// <summary>
    /// Best-effort shape for Telebirr's notify JSON body, based on multiple
    /// independent third-party implementations of this flow. CONFIRM the
    /// exact field names against your own sandbox before going live - this is
    /// exactly why ParseCallbackAsync never trusts these values directly,
    /// only the merch_order_id (to know which order to look up) and the
    /// signature (to decide whether to bother looking it up at all).
    /// </summary>
    private class TelebirrNotifyPayload
    {
        [JsonPropertyName("appid")] public string? AppId { get; set; }
        [JsonPropertyName("merch_code")] public string? MerchCode { get; set; }
        [JsonPropertyName("merch_order_id")] public string? MerchOrderId { get; set; }
        [JsonPropertyName("payment_order_id")] public string? PaymentOrderId { get; set; }
        [JsonPropertyName("transId")] public string? TransId { get; set; }
        [JsonPropertyName("trade_status")] public string? TradeStatus { get; set; }
        [JsonPropertyName("total_amount")] public decimal TotalAmount { get; set; }
        [JsonPropertyName("trans_currency")] public string? TransCurrency { get; set; }
        [JsonPropertyName("sign")] public string? Sign { get; set; }
        [JsonPropertyName("sign_type")] public string? SignType { get; set; }
    }
}
