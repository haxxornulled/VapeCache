using VapeCache.Licensing;

namespace VapeCache.LicenseGenerator;

/// <summary>
/// Internal utility for generating and validating HMAC-based VapeCache license keys.
/// </summary>
internal class Program
{
    private const string LicenseSecretEnvironmentVariable = "VAPECACHE_LICENSE_SECRET";

    static void Main(string[] args)
    {
        Console.Out.WriteLine("=== VapeCache License Key Generator ===");
        Console.Out.WriteLine("HMAC generator utility for OSS licensing flows.\n");

        if (args.Length > 0 && args[0] == "--validate")
        {
            ValidateLicenseKey();
            return;
        }

        GenerateLicenseKey();
    }

    private static void GenerateLicenseKey()
    {
        Console.Out.WriteLine("=== VapeCache License Generator ===");
        Console.Out.WriteLine("Usage: generates VCPRO/VCENT keys using shared secret signing.\n");

        var secret = ResolveSecret();
        if (string.IsNullOrWhiteSpace(secret))
            return;

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

        Console.Write("Choose tier (pro|enterprise) [enterprise]: ");
        var tierInput = Console.ReadLine()?.Trim();
        var tier = string.Equals(tierInput, "pro", StringComparison.OrdinalIgnoreCase)
            ? LicenseTier.Pro
            : LicenseTier.Enterprise;

        var issuer = new LicenseValidator(secret);
        var licenseKey = issuer.GenerateLicenseKey(tier, organizationId, expiresAt);

        Console.Out.WriteLine();
        Console.Out.WriteLine("=== LICENSE KEY GENERATED ===");
        Console.Out.WriteLine($"Tier:             {tier}");
        Console.Out.WriteLine($"Organization:     {organizationId}");
        Console.Out.WriteLine($"Expires:          {expiresAt:yyyy-MM-dd}");
        Console.Out.WriteLine($"Deployments:      {(tier == LicenseTier.Enterprise ? "Unlimited" : "Max 5")}");
        Console.Out.WriteLine();
        Console.Out.WriteLine("License Key:");
        Console.Out.WriteLine(licenseKey);
        Console.Out.WriteLine();
        Console.Out.WriteLine("=== USAGE ===");
        Console.Out.WriteLine("Add to appsettings.json or set environment variable:");
        Console.Out.WriteLine($"  VAPECACHE_LICENSE_KEY={licenseKey}");
        Console.Out.Flush();
    }

    private static void ValidateLicenseKey()
    {
        var secret = ResolveSecret();
        if (string.IsNullOrWhiteSpace(secret))
            return;

        Console.Write("Enter license key to validate: ");
        var licenseKey = Console.ReadLine()?.Trim();

        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            Console.Out.WriteLine("License key cannot be empty. Exiting.");
            return;
        }

        var validator = new LicenseValidator(secret);
        var result = validator.Validate(licenseKey);

        Console.Out.WriteLine("\n=== VALIDATION RESULT ===");
        Console.Out.WriteLine($"Valid:      {result.IsValid}");

        if (result.IsValid)
        {
            Console.Out.WriteLine($"Tier:       {result.Tier}");
            Console.Out.WriteLine($"Customer:   {result.CustomerId}");
            Console.Out.WriteLine($"Expires:    {result.ExpiresAt:yyyy-MM-dd}");
            Console.Out.WriteLine($"Instances:  {result.MaxInstances}");
            Console.Out.WriteLine($"Expired:    {result.IsExpired}");
        }
        else
        {
            Console.Out.WriteLine($"Error:      {result.ErrorMessage}");
        }
    }

    private static string? ResolveSecret()
    {
        var secret = Environment.GetEnvironmentVariable(LicenseSecretEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(secret))
            return secret;

        Console.Write($"Enter signing secret ({LicenseSecretEnvironmentVariable}): ");
        secret = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(secret))
        {
            Console.Out.WriteLine($"Secret is required. Set {LicenseSecretEnvironmentVariable} or provide input.");
            return null;
        }

        return secret;
    }
}

