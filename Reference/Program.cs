using System;
using System.Threading;
using Autofac;
using LanguageExt;
using Microsoft.Extensions.Logging;
using Serilog;
using static LanguageExt.Prelude;
using Application.Common.Extensions;
using ResultDemo;
using ResultDemo.Examples;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateLogger();

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.ClearProviders();
    builder.AddSerilog(Log.Logger, dispose: true);
});

using var container = CompositionRoot.BuildContainer(loggerFactory);

try
{
    using var scope = container.BeginLifetimeScope();
    var userService = scope.Resolve<IUserService>();
    var controller = scope.Resolve<UsersController>();
    var queryService = scope.Resolve<IUserQueryService>();
    var handler = scope.Resolve<UserProvisioningHandler>();
    var profileRepo = scope.Resolve<InMemoryProfileRepository>();
    var facade = scope.Resolve<UserProfileFacade>();
    var lookupService = scope.Resolve<UserLookupService>();
    var cacheExamples = scope.Resolve<HybridCacheServiceExamples>();

    var aliceRequest = new CreateUserRequest("alice@example.com", "Alice the Smart");
    var bobRequest = new CreateUserRequest("bob@example.com", "Bob");
    var carolRequest = new CreateUserRequest("carol@example.com", "Carol");

    var aliceResult = userService.CreateUser(aliceRequest);
    var bobResult = userService.CreateUser(bobRequest);

    if (!aliceResult.TryGetValue(out var alice))
    {
        aliceResult.LogFailureMessage(loggerFactory.CreateLogger("Create"), "Alice create failed");
        return;
    }

    Log.Information("Happy path create: {Email}", alice.Email);
    var lookupResult = userService.GetById(alice.Id);
    if (lookupResult.TryGetValue(out var found))
    {
        Log.Information("Happy path lookup: {Email}", found.Email);
    }
    else
    {
        lookupResult.LogFailureMessage(loggerFactory.CreateLogger("Lookup"), "Lookup failed for {UserId}", alice.Id);
    }

    var syncResult = queryService.GetById(alice.Id);
    if (syncResult.TryGetValue(out var syncUser))
    {
        Log.Information("Sync query happy path: {Email}", syncUser.Email);
    }
    else
    {
        syncResult.LogFailureMessage(loggerFactory.CreateLogger("SyncLookup"), "Sync lookup failed for {UserId}", alice.Id);
    }

    var asyncResult = queryService.GetByIdAsync(alice.Id);
    if (!asyncResult.TryGetValue(out var valueTask))
    {
        asyncResult.LogFailureMessage(loggerFactory.CreateLogger("AsyncLookup"), "Async lookup failed for {UserId}", alice.Id);
        return;
    }

    var asyncUser = await valueTask;
    Log.Information("Async happy path: {Email}", asyncUser.Email);

    if (bobResult.TryGetValue(out var bob))
    {
        Log.Information("Happy path create: {Email}", bob.Email);
    }
    else
    {
        bobResult.LogFailureMessage(loggerFactory.CreateLogger("Create"), "Bob create failed");
    }

    var controllerCreate = controller.Create(carolRequest);
    Log.Information("Controller create result: {ResultType}", controllerCreate.GetType().Name);

    var second = controller.Create(carolRequest);
    Log.Information("Second controller create result: {ResultType}", second.GetType().Name);

    handler.Handle(new ProvisionUserMessage("msg-1", Option<Guid>.None));
    handler.Handle(new ProvisionUserMessage("msg-2", Some(Guid.NewGuid())));

    var profile = facade.GetProfileOrAnonymous(profileRepo.KnownUserId);
    Log.Information("Happy path profile: {Username}", profile.Username);
    var fallbackProfile = facade.GetProfileOrAnonymous(Guid.NewGuid());
    Log.Information("Fallback profile: {Username}", fallbackProfile.Username);

    var lookup = lookupService.TryLookup(Guid.NewGuid());
    Log.Information("Lookup result: {HasUser}", lookup is not null);

    await cacheExamples.RunAsync(CancellationToken.None);
}
catch (Exception ex)
{
    Log.Fatal(ex, "Unhandled exception");
}
finally
{
    Log.CloseAndFlush();
}
