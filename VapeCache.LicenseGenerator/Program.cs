using System.Security.Cryptography;
using VapeCache.Licensing;

namespace VapeCache.LicenseGenerator;

/// <summary>
/// Internal utility for generating VapeCache license keys.
/// NOT FOR DISTRIBUTION - For VapeCache team use only.
/// </summary>
internal class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("=== VapeCache License Key Generator ===");
        Console.WriteLine("FOR INTERNAL USE ONLY - DO NOT DISTRIBUTE\n");

        if (args.Length > 0 && args[0] == "--create-keypair")
        {
            CreateKeyPair();
            return;
        }

        if (args.Length > 0 && args[0] == "--validate")
        {
            ValidateLicenseKey();
            return;
        }

        GenerateLicenseKey();
    }

    private static void GenerateLicenseKey()
    {
        Console.WriteLine("=== VapeCache Enterprise License Generator ===");
        Console.WriteLine("Application-based licensing: $499/month per organization");
        Console.WriteLine("Unlimited deployments, unlimited Redis topology\n");

        string signingPrivateKeyPem;
        try
        {
            signingPrivateKeyPem = LicenseValidationOptions.ResolveSigningPrivateKeyPem();
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine(ex.Message);
            Console.WriteLine($"Set {LicenseValidationOptions.SigningPrivateKeyEnvironmentVariable} before generating licenses.");
            Console.WriteLine("Use --create-keypair to generate an ES256 key pair.");
            return;
        }

        var keyId = LicenseValidationOptions.ResolveVerificationKeyId();

        Console.Write("Enter Organization ID (e.g., acme, contoso): ");
        var organizationId = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            Console.WriteLine("Organization ID cannot be empty. Exiting.");
            return;
        }

        Console.Write("Enter expiration date (YYYY-MM-DD) or leave blank for 1 year: ");
        var expiryInput = Console.ReadLine()?.Trim();
        DateTimeOffset expiresAt;

        if (string.IsNullOrWhiteSpace(expiryInput))
        {
            expiresAt = DateTimeOffset.UtcNow.AddYears(1);
        }
        else if (!DateTimeOffset.TryParse(expiryInput, out expiresAt))
        {
            Console.WriteLine("Invalid date format. Exiting.");
            return;
        }

        var issuer = new LicenseTokenIssuer(signingPrivateKeyPem, keyId);
        var licenseKey = issuer.GenerateEnterpriseLicenseKey(
            organizationId,
            expiresAt,
            features: new[]
            {
                LicenseFeatures.Persistence,
                LicenseFeatures.Reconciliation
            });

        Console.WriteLine();
        Console.WriteLine("=== ENTERPRISE LICENSE KEY GENERATED ===");
        Console.WriteLine("Tier:             Enterprise");
        Console.WriteLine($"Organization:     {organizationId}");
        Console.WriteLine($"Key ID (kid):     {keyId}");
        Console.WriteLine($"Expires:          {expiresAt:yyyy-MM-dd}");
        Console.WriteLine("Deployments:      Unlimited");
        Console.WriteLine("Redis Topology:   Any (standalone/sentinel/cluster)");
        Console.WriteLine("Features:         persistence, reconciliation");
        Console.WriteLine();
        Console.WriteLine("License Key:");
        Console.WriteLine(licenseKey);
        Console.WriteLine();
        Console.WriteLine("=== USAGE ===");
        Console.WriteLine("Add to appsettings.json or set environment variable:");
        Console.WriteLine($"  VAPECACHE_LICENSE_KEY={licenseKey}");
        Console.WriteLine();
        Console.WriteLine("For Persistence:");
        Console.WriteLine($"  services.AddVapeCachePersistence(\"{licenseKey}\");");
        Console.WriteLine();
        Console.WriteLine("For Reconciliation:");
        Console.WriteLine($"  services.AddVapeCacheRedisReconciliation(\"{licenseKey}\");");
        Console.Out.Flush();
    }

    private static void ValidateLicenseKey()
    {
        Console.WriteLine($"Verifier key id: {LicenseValidationOptions.ResolveVerificationKeyId()}");
        Console.Write("Enter license key to validate: ");
        var licenseKey = Console.ReadLine()?.Trim();

        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            Console.WriteLine("License key cannot be empty. Exiting.");
            return;
        }

        var validator = new LicenseValidator();
        var result = validator.Validate(licenseKey);

        Console.WriteLine("\n=== VALIDATION RESULT ===");
        Console.WriteLine($"Valid:      {result.IsValid}");

        if (result.IsValid)
        {
            Console.WriteLine($"Tier:       {result.Tier}");
            Console.WriteLine($"Customer:   {result.CustomerId}");
            Console.WriteLine($"Key ID:     {result.KeyId}");
            Console.WriteLine($"License ID: {result.LicenseId}");
            Console.WriteLine($"Expires:    {result.ExpiresAt:yyyy-MM-dd}");
            Console.WriteLine($"Features:   {string.Join(", ", result.Features)}");
            Console.WriteLine($"Expired:    {result.IsExpired}");
        }
        else
        {
            Console.WriteLine($"Error:      {result.ErrorMessage}");
        }
    }

    private static void CreateKeyPair()
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.GenerateKey(ECCurve.NamedCurves.nistP256);

        var privateKeyPem = ecdsa.ExportPkcs8PrivateKeyPem();
        var publicKeyPem = ecdsa.ExportSubjectPublicKeyInfoPem();

        Console.WriteLine("=== NEW ES256 KEYPAIR ===");
        Console.WriteLine();
        Console.WriteLine("Private key (store in secure signing environment only):");
        Console.WriteLine(privateKeyPem);
        Console.WriteLine();
        Console.WriteLine("Public key (safe to distribute for validation):");
        Console.WriteLine(publicKeyPem);
        Console.WriteLine();
        Console.WriteLine("Set environment variables:");
        Console.WriteLine($"  {LicenseValidationOptions.SigningPrivateKeyEnvironmentVariable}=<PRIVATE PEM>");
        Console.WriteLine($"  {LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable}=<PUBLIC PEM>");
        Console.WriteLine($"  {LicenseValidationOptions.VerificationKeyIdEnvironmentVariable}=vc-main-2026");
    }
}
