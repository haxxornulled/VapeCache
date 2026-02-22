using System.Security.Cryptography;

namespace VapeCache.Tests.Licensing;

internal static class LicenseTestKeys
{
    internal static (string PrivateKeyPem, string PublicKeyPem) GeneratePemKeyPair()
    {
        using var signingKey = ECDsa.Create();
        signingKey.GenerateKey(ECCurve.NamedCurves.nistP256);

        return (
            signingKey.ExportPkcs8PrivateKeyPem(),
            signingKey.ExportSubjectPublicKeyInfoPem());
    }
}
