namespace FHSMS.Infrastructure.PaymentProviders;

/// <summary>
/// One entry per configured provider: the shared secret used to validate its
/// webhook signature, and which header carries that signature. Real
/// deployments set these via user-secrets/environment variables, never
/// committed to appsettings.json.
/// </summary>
public class PaymentProviderSettings
{
    public const string SectionName = "PaymentProviders";

    /// <summary>This API's own public base URL (e.g. https://fhsms-api.onrender.com), no trailing slash needed - used to build the callback_url passed to providers that need to know where to send their webhook (Chapa's /transaction/initialize call, for example).</summary>
    public string? ApiPublicBaseUrl { get; set; }
    /// <summary>The frontend's public base URL - used to build the return_url a customer is redirected to once they've finished paying.</summary>
    public string? FrontendPublicBaseUrl { get; set; }

    public ProviderCredential BankTransfer { get; set; } = new();
    public TelebirrCredential Telebirr { get; set; } = new();
    public ProviderCredential Chapa { get; set; } = new();
    public ProviderCredential CbeBirr { get; set; } = new();

    public class ProviderCredential
    {
        public string? Secret { get; set; }
        public string SignatureHeader { get; set; } = "X-Signature";
    }

    /// <summary>
    /// Telebirr's H5 C2B Web Payment flow: apply for a short-lived "Fabric"
    /// token, use it to create an RSA-signed pre-order (which returns a
    /// prepay_id), redirect the payer to Telebirr's hosted checkout page with
    /// a second signed request, then confirm the result both from Telebirr's
    /// notify callback AND a server-to-server queryOrder call (see
    /// TelebirrPaymentProvider's remarks for why both). All of these values
    /// come from Ethio Telecom's developer portal after your merchant team
    /// and product are approved - there is no public self-service signup
    /// like Chapa's.
    /// </summary>
    public class TelebirrCredential
    {
        /// <summary>"Fabric App ID" (a UUID) from the developer portal - identifies your app when requesting a Fabric token. Sent as the X-APP-Key header on every API call.</summary>
        public string? FabricAppId { get; set; }
        /// <summary>"App Secret" from the developer portal - exchanged for a Fabric token. Keep this as secret as a password.</summary>
        public string? AppSecret { get; set; }
        /// <summary>Merchant App ID from the developer portal - this is the "appid" field inside every signed request body (distinct from FabricAppId above).</summary>
        public string? MerchantAppId { get; set; }
        /// <summary>Merchant code ("merch_code") from the developer portal - your business's short code with Telebirr.</summary>
        public string? MerchantCode { get; set; }
        /// <summary>PEM-encoded RSA private key issued to this merchant - signs every outgoing request. Ethio Telecom sometimes issues this as bare base64 DER (no -----BEGIN/END----- lines); if so, wrap it in PEM headers before setting this value.</summary>
        public string? MerchantPrivateKeyPem { get; set; }
        /// <summary>PEM-encoded RSA public key Telebirr provides - verifies the signature on incoming notify callbacks so a forged webhook can't credit an invoice.</summary>
        public string? TelebirrPublicKeyPem { get; set; }
        /// <summary>Base URL for the API calls (token / preOrder / queryOrder), e.g. https://developerportal.ethiotelebirr.et:38443/apiaccess/payment/gateway for the test environment. Different for sandbox vs production - given at onboarding.</summary>
        public string? ApiBaseUrl { get; set; }
        /// <summary>Base URL of the hosted H5 checkout page the payer is redirected to, e.g. https://developerportal.ethiotelebirr.et:38443/payment/web/paygate for the test environment. Different for sandbox vs production.</summary>
        public string? WebCheckoutBaseUrl { get; set; }
        /// <summary>"business_type" sent on every order - "BuyGoods" is the commonly used default; change only if Ethio Telecom tells you to use something else for your contract.</summary>
        public string BusinessType { get; set; } = "BuyGoods";
        public int TimeoutExpressMinutes { get; set; } = 30;
    }
}
