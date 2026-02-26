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
        Console.Out.WriteLine("=== VapeCache License Key Generator ===");
        Console.Out.WriteLine("FOR INTERNAL USE ONLY - DO NOT DISTRIBUTE\n");

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
        Console.Out.WriteLine("=== VapeCache Enterprise License Generator ===");
        Console.Out.WriteLine("Application-based licensing: $499/month per organization");
        Console.Out.WriteLine("Unlimited deployments, unlimited Redis topology\n");

        string signingPrivateKeyPem;
        try
        {
            signingPrivateKeyPem = LicenseValidationOptions.ResolveSigningPrivateKeyPem();
        }
        catch (InvalidOperationException ex)
        {
            Console.Out.WriteLine(ex.Message);
            Console.Out.WriteLine($"Set {LicenseValidationOptions.SigningPrivateKeyEnvironmentVariable} before generating licenses.");
            Console.Out.WriteLine("Use --create-keypair to generate an ES256 key pair.");
            return;
        }

        var keyId = LicenseValidationOptions.ResolveVerificationKeyId();

        Console.Write("Enter Organization ID (e.g., acme, contoso): ");
        var organizationId = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            Console.Out.WriteLine("Organization ID cannot be empty. Exiting.");
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
            Console.Out.WriteLine("Invalid date format. Exiting.");
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

        Console.Out.WriteLine();
        Console.Out.WriteLine("=== ENTERPRISE LICENSE KEY GENERATED ===");
        Console.Out.WriteLine("Tier:             Enterprise");
        Console.Out.WriteLine($"Organization:     {organizationId}");
        Console.Out.WriteLine($"Key ID (kid):     {keyId}");
        Console.Out.WriteLine($"Expires:          {expiresAt:yyyy-MM-dd}");
        Console.Out.WriteLine("Deployments:      Unlimited");
        Console.Out.WriteLine("Redis Topology:   Any (standalone/sentinel/cluster)");
        Console.Out.WriteLine("Features:         persistence, reconciliation");
        Console.Out.WriteLine();
        Console.Out.WriteLine("License Key:");
        Console.Out.WriteLine(licenseKey);
        Console.Out.WriteLine();
        Console.Out.WriteLine("=== USAGE ===");
        Console.Out.WriteLine("Add to appsettings.json or set environment variable:");
        Console.Out.WriteLine($"  VAPECACHE_LICENSE_KEY={licenseKey}");
        Console.Out.WriteLine();
        Console.Out.WriteLine("For Persistence:");
        Console.Out.WriteLine($"  services.AddVapeCachePersistence(\"{licenseKey}\");");
        Console.Out.WriteLine();
        Console.Out.WriteLine("For Reconciliation:");
        Console.Out.WriteLine($"  services.AddVapeCacheRedisReconciliation(\"{licenseKey}\");");
        Console.Out.Flush();
    }

    private static void ValidateLicenseKey()
    {
        Console.Out.WriteLine($"Verifier key id: {LicenseValidationOptions.ResolveVerificationKeyId()}");
        Console.Write("Enter license key to validate: ");
        var licenseKey = Console.ReadLine()?.Trim();

        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            Console.Out.WriteLine("License key cannot be empty. Exiting.");
            return;
        }

        var validator = new LicenseValidator();
        var result = validator.Validate(licenseKey);

        Console.Out.WriteLine("\n=== VALIDATION RESULT ===");
        Console.Out.WriteLine($"Valid:      {result.IsValid}");

        if (result.IsValid)
        {
            Console.Out.WriteLine($"Tier:       {result.Tier}");
            Console.Out.WriteLine($"Customer:   {result.CustomerId}");
            Console.Out.WriteLine($"Key ID:     {result.KeyId}");
            Console.Out.WriteLine($"License ID: {result.LicenseId}");
            Console.Out.WriteLine($"Expires:    {result.ExpiresAt:yyyy-MM-dd}");
            Console.Out.WriteLine($"Features:   {string.Join(", ", result.Features)}");
            Console.Out.WriteLine($"Expired:    {result.IsExpired}");
        }
        else
        {
            Console.Out.WriteLine($"Error:      {result.ErrorMessage}");
        }
    }

    private static void CreateKeyPair()
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.GenerateKey(ECCurve.NamedCurves.nistP256);

        var privateKeyPem = ecdsa.ExportPkcs8PrivateKeyPem();
        var publicKeyPem = ecdsa.ExportSubjectPublicKeyInfoPem();

        Console.Out.WriteLine("=== NEW ES256 KEYPAIR ===");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Private key (store in secure signing environment only):");
        Console.Out.WriteLine(privateKeyPem);
        Console.Out.WriteLine();
        Console.Out.WriteLine("Public key (safe to distribute for validation):");
        Console.Out.WriteLine(publicKeyPem);
        Console.Out.WriteLine();
        Console.Out.WriteLine("Set environment variables:");
        Console.Out.WriteLine($"  {LicenseValidationOptions.SigningPrivateKeyEnvironmentVariable}=<PRIVATE PEM>");
        Console.Out.WriteLine($"  {LicenseValidationOptions.VerificationPublicKeyEnvironmentVariable}=<PUBLIC PEM>");
        Console.Out.WriteLine($"  {LicenseValidationOptions.VerificationKeyIdEnvironmentVariable}=vc-main-2026");
    }
}

