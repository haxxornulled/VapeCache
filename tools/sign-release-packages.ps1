[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$PackageOutput = "artifacts/packages",
    [string]$PackageVersion = "",
    [string]$CertificatePath = $env:NUGET_SIGNING_CERT_PATH,
    [string]$CertificateKeyPath = $env:NUGET_SIGNING_CERT_KEY_PATH,
    [string]$CertificatePassword = $env:NUGET_SIGNING_CERT_PASSWORD,
    [string]$TimestampServer = $env:NUGET_TIMESTAMP_SERVER,
    [ValidateSet("SHA256", "SHA384", "SHA512")]
    [string]$HashAlgorithm = "SHA256"
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "release-package-manifest.ps1")
. (Join-Path $PSScriptRoot "release-common.ps1")

$repoRoot = Get-ReleaseRepoRoot
$PackageOutput = Resolve-ReleaseAbsolutePath -Path $PackageOutput -BasePath $repoRoot

if ([string]::IsNullOrWhiteSpace($TimestampServer))
{
    $TimestampServer = "https://timestamp.digicert.com"
}

function Assert-RequiredValue
{
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value))
    {
        throw "$Name is required."
    }
}

function New-CertificateFromPemFiles
{
    param(
        [Parameter(Mandatory = $true)][string]$CertPath,
        [string]$KeyPath = ""
    )

    if ([string]::IsNullOrWhiteSpace($KeyPath))
    {
        return [System.Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPemFile($CertPath)
    }

    return [System.Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPemFile($CertPath, $KeyPath)
}

function Assert-CodeSigningCertificate
{
    param(
        [Parameter(Mandatory = $true)][System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($Certificate)
    if ($null -eq $rsa)
    {
        throw "NuGet package signing requires an RSA certificate. The supplied certificate uses $($Certificate.PublicKey.Oid.FriendlyName) instead."
    }

    if ($rsa.KeySize -lt 2048)
    {
        throw "NuGet package signing requires an RSA certificate with a 2048-bit key or larger. The supplied certificate is $($rsa.KeySize) bits."
    }

    $eku = $Certificate.Extensions |
        Where-Object { $_ -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } |
        Select-Object -First 1

    $ekuValues = @()
    if ($null -ne $eku)
    {
        $ekuValues = @($eku.EnhancedKeyUsages | ForEach-Object { $_.Value })
    }

    if (-not ($ekuValues -contains "1.3.6.1.5.5.7.3.3"))
    {
        throw "NuGet package signing requires a certificate valid for code signing (1.3.6.1.5.5.7.3.3). The supplied certificate is not a code-signing certificate."
    }

    if ($Certificate.NotAfter -lt (Get-Date).ToUniversalTime())
    {
        throw "The signing certificate is expired."
    }
}

Assert-RequiredValue -Name "PackageOutput" -Value $PackageOutput
Assert-RequiredValue -Name "CertificatePath" -Value $CertificatePath

$resolvedPackageVersion = Resolve-ReleasePackageVersion -PackageVersion $PackageVersion
$artifacts = Get-ReleasePackageArtifacts -Packages (Get-ReleasePackageVersionInfo) -PackageOutput $PackageOutput -PackageVersion $resolvedPackageVersion

$certificate = $null
$tempPfx = $null
try
{
    if ([string]::IsNullOrWhiteSpace($CertificateKeyPath))
    {
        if ($CertificatePath.EndsWith(".pfx", [System.StringComparison]::OrdinalIgnoreCase))
        {
            $tempPfx = $CertificatePath
            $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
                $CertificatePath,
                $CertificatePassword,
                [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable)
        }
        else
        {
            throw "When CertificateKeyPath is not supplied, CertificatePath must point to a PFX file."
        }
    }
    else
    {
        $certificate = New-CertificateFromPemFiles -CertPath $CertificatePath -KeyPath $CertificateKeyPath
        $tempPfx = Join-Path ([System.IO.Path]::GetTempPath()) ("vapecache-signing-" + [Guid]::NewGuid().ToString("N") + ".pfx")
        $pfxBytes = $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $CertificatePassword)
        [System.IO.File]::WriteAllBytes($tempPfx, $pfxBytes)
    }

    Assert-CodeSigningCertificate -Certificate $certificate

    $signArgs = @(
        "nuget", "sign"
    )

    foreach ($artifact in $artifacts)
    {
        $signArgs += $artifact.PackageFile
    }

    $signArgs += @(
        "--certificate-path", $tempPfx,
        "--hash-algorithm", $HashAlgorithm,
        "--timestamper", $TimestampServer,
        "--overwrite"
    )

    if (-not [string]::IsNullOrWhiteSpace($CertificatePassword))
    {
        $signArgs += @("--certificate-password", $CertificatePassword)
    }

    if ($PSCmdlet.ShouldProcess($PackageOutput, "Sign release packages"))
    {
        & dotnet @signArgs
        if ($LASTEXITCODE -ne 0)
        {
            throw "dotnet nuget sign failed."
        }
    }
}
finally
{
    if (-not [string]::IsNullOrWhiteSpace($tempPfx) -and $tempPfx -ne $CertificatePath -and (Test-Path -LiteralPath $tempPfx))
    {
        Remove-Item -LiteralPath $tempPfx -Force
    }
}
