using Autofac;
using Microsoft.Extensions.Hosting;
using VapeCache.Licensing.ControlPlane.Auth;
using VapeCache.Licensing.ControlPlane.Hosting;
using VapeCache.Licensing.ControlPlane.Revocation;

namespace VapeCache.Licensing.ControlPlane.DependencyInjection;

/// <summary>
/// Autofac registrations for the licensing control-plane.
/// </summary>
public sealed class ControlPlaneModule : Module
{
    /// <inheritdoc />
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<ApiKeyAuthorizer>()
            .As<IApiKeyAuthorizer>()
            .SingleInstance();

        builder.RegisterType<FileBackedRevocationRegistry>()
            .As<IRevocationRegistry>()
            .SingleInstance();

        builder.RegisterType<ControlPlaneLifecycleService>()
            .As<IHostedService>()
            .As<IHostedLifecycleService>()
            .SingleInstance();
    }
}
