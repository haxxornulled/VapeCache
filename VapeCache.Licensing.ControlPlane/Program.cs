using Autofac;
using Autofac.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http.HttpResults;
using Serilog;
using VapeCache.Licensing.ControlPlane.DependencyInjection;
using VapeCache.Licensing.ControlPlane.Revocation;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
builder.Host.UseSerilog(static (context, services, loggerConfiguration) =>
{
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext();
});

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
builder.Services
    .AddOptions<RevocationControlPlaneOptions>()
    .Bind(builder.Configuration.GetSection(RevocationControlPlaneOptions.SectionName))
    .Validate(static o => !string.IsNullOrWhiteSpace(o.PersistencePath), "PersistencePath is required.")
    .Validate(static o => !o.RequireApiKey || !string.IsNullOrWhiteSpace(o.ApiKey), "ApiKey must be set when RequireApiKey is true.")
    .Validate(static o => !string.IsNullOrWhiteSpace(o.ApiKeyHeaderName), "ApiKeyHeaderName is required.")
    .ValidateOnStart();

builder.Host.ConfigureContainer<ContainerBuilder>(static container =>
{
    container.RegisterModule<ControlPlaneModule>();
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseSerilogRequestLogging();
app.UseMiddleware<VapeCache.Licensing.ControlPlane.Auth.ApiKeyAuthenticationMiddleware>();

app.MapGet("/", static () => TypedResults.Ok(new
{
    service = "VapeCache.Licensing.ControlPlane",
    status = "ok",
    utc = DateTimeOffset.UtcNow
})).ExcludeFromDescription();

var revocations = app.MapGroup("/api/v1/revocations").WithTags("Revocations");

revocations.MapGet("/status/{licenseId}", static Results<Ok<RevocationStatusResponse>, BadRequest<string>> (
    string licenseId,
    string? organizationId,
    string? keyId,
    IRevocationRegistry registry) =>
{
    if (IsInvalidIdentity(licenseId))
        return TypedResults.BadRequest("licenseId is required and must be <= 256 characters.");

    var decision = registry.Evaluate(licenseId, organizationId, keyId);
    return TypedResults.Ok(RevocationStatusResponse.From(licenseId, organizationId, keyId, decision));
})
.WithName("GetRevocationStatus")
.WithSummary("Gets effective revocation status for a license and optional org/key context.")
.WithDescription("Returns whether the license is currently active or blocked by license-level revoke, org kill-switch, or key-id revoke.");

revocations.MapGet("/snapshot", static Ok<RevocationSnapshotResponse> (IRevocationRegistry registry) =>
{
    return TypedResults.Ok(RevocationSnapshotResponse.From(registry.GetSnapshot()));
})
.WithName("GetRevocationSnapshot")
.WithSummary("Returns full in-memory revocation state for operator diagnostics.");

revocations.MapPost("/licenses/{licenseId}/revoke", static Results<Ok<RevocationMutationResponse>, BadRequest<string>> (
    string licenseId,
    RevocationMutationRequest request,
    HttpContext httpContext,
    IRevocationRegistry registry) =>
{
    if (IsInvalidIdentity(licenseId))
        return TypedResults.BadRequest("licenseId is required and must be <= 256 characters.");

    var actor = ResolveActor(request.Actor, httpContext);
    var reason = ResolveReason(request.Reason, "license-revoked");
    var result = registry.RevokeLicense(licenseId, reason, actor);
    return TypedResults.Ok(RevocationMutationResponse.From(result));
})
.WithName("RevokeLicense")
.WithSummary("Revokes a specific license id immediately.");

revocations.MapPost("/licenses/{licenseId}/activate", static Results<Ok<RevocationMutationResponse>, BadRequest<string>> (
    string licenseId,
    RevocationMutationRequest request,
    HttpContext httpContext,
    IRevocationRegistry registry) =>
{
    if (IsInvalidIdentity(licenseId))
        return TypedResults.BadRequest("licenseId is required and must be <= 256 characters.");

    var actor = ResolveActor(request.Actor, httpContext);
    var reason = ResolveReason(request.Reason, "license-reactivated");
    var result = registry.ActivateLicense(licenseId, reason, actor);
    return TypedResults.Ok(RevocationMutationResponse.From(result));
})
.WithName("ActivateLicense")
.WithSummary("Reactivates a previously revoked license id.");

revocations.MapPost("/organizations/{organizationId}/kill-switch", static Results<Ok<RevocationMutationResponse>, BadRequest<string>> (
    string organizationId,
    RevocationMutationRequest request,
    HttpContext httpContext,
    IRevocationRegistry registry) =>
{
    if (IsInvalidIdentity(organizationId))
        return TypedResults.BadRequest("organizationId is required and must be <= 256 characters.");

    var actor = ResolveActor(request.Actor, httpContext);
    var reason = ResolveReason(request.Reason, "organization-kill-switch-enabled");
    var result = registry.EnableOrganizationKillSwitch(organizationId, reason, actor);
    return TypedResults.Ok(RevocationMutationResponse.From(result));
})
.WithName("EnableOrganizationKillSwitch")
.WithSummary("Enables kill-switch for an entire organization id.");

revocations.MapPost("/organizations/{organizationId}/restore", static Results<Ok<RevocationMutationResponse>, BadRequest<string>> (
    string organizationId,
    RevocationMutationRequest request,
    HttpContext httpContext,
    IRevocationRegistry registry) =>
{
    if (IsInvalidIdentity(organizationId))
        return TypedResults.BadRequest("organizationId is required and must be <= 256 characters.");

    var actor = ResolveActor(request.Actor, httpContext);
    var reason = ResolveReason(request.Reason, "organization-kill-switch-disabled");
    var result = registry.DisableOrganizationKillSwitch(organizationId, reason, actor);
    return TypedResults.Ok(RevocationMutationResponse.From(result));
})
.WithName("DisableOrganizationKillSwitch")
.WithSummary("Disables kill-switch for an organization id.");

revocations.MapPost("/keyids/{keyId}/revoke", static Results<Ok<RevocationMutationResponse>, BadRequest<string>> (
    string keyId,
    RevocationMutationRequest request,
    HttpContext httpContext,
    IRevocationRegistry registry) =>
{
    if (IsInvalidIdentity(keyId))
        return TypedResults.BadRequest("keyId is required and must be <= 256 characters.");

    var actor = ResolveActor(request.Actor, httpContext);
    var reason = ResolveReason(request.Reason, "key-id-revoked");
    var result = registry.RevokeKeyId(keyId, reason, actor);
    return TypedResults.Ok(RevocationMutationResponse.From(result));
})
.WithName("RevokeKeyId")
.WithSummary("Revokes all licenses signed with the specified key id.");

revocations.MapPost("/keyids/{keyId}/activate", static Results<Ok<RevocationMutationResponse>, BadRequest<string>> (
    string keyId,
    RevocationMutationRequest request,
    HttpContext httpContext,
    IRevocationRegistry registry) =>
{
    if (IsInvalidIdentity(keyId))
        return TypedResults.BadRequest("keyId is required and must be <= 256 characters.");

    var actor = ResolveActor(request.Actor, httpContext);
    var reason = ResolveReason(request.Reason, "key-id-reactivated");
    var result = registry.ActivateKeyId(keyId, reason, actor);
    return TypedResults.Ok(RevocationMutationResponse.From(result));
})
.WithName("ActivateKeyId")
.WithSummary("Reactivates a previously revoked key id.");

app.MapHealthChecks("/health");

app.Run();

static bool IsInvalidIdentity(string? value)
    => string.IsNullOrWhiteSpace(value) || value.Trim().Length > 256;

static string ResolveActor(string? actor, HttpContext context)
{
    if (!string.IsNullOrWhiteSpace(actor))
        return actor.Trim();

    var remoteIp = context.Connection.RemoteIpAddress?.ToString();
    return string.IsNullOrWhiteSpace(remoteIp) ? "api:unknown" : $"api:{remoteIp}";
}

static string ResolveReason(string? reason, string fallback)
    => string.IsNullOrWhiteSpace(reason) ? fallback : reason.Trim();
