# Agent Instructions

- Autofac-first: register services in Autofac modules and wire them via `As<T>()` on a single instance.
- Use Microsoft.Extensions.DependencyInjection only when a library API requires it; Autofac's service provider factory will consume those registrations.
- Avoid proxy/adapter services and avoid new service-locator helpers. If stuck with MS.DI, map shared singletons using factory registrations that return the same instance.
