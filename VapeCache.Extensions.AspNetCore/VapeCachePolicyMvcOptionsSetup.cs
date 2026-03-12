using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Options;

namespace VapeCache.Extensions.AspNetCore;

internal sealed class VapeCachePolicyMvcOptionsSetup : IConfigureOptions<MvcOptions>
{
    public void Configure(MvcOptions options)
    {
        options.Conventions.Add(new VapeCachePolicyApplicationModelConvention());
    }
}

internal sealed class VapeCachePolicyApplicationModelConvention : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        foreach (var controller in application.Controllers)
        {
            ApplyAttributeMapping(controller.Attributes, controller.Selectors);

            foreach (var action in controller.Actions)
                ApplyAttributeMapping(action.Attributes, action.Selectors);
        }
    }

    private static void ApplyAttributeMapping(IReadOnlyList<object> attributes, IList<SelectorModel> selectors)
    {
        var policyAttribute = attributes.OfType<VapeCachePolicyAttribute>().FirstOrDefault();
        if (policyAttribute is null)
            return;

        foreach (var selector in selectors)
        {
            if (selector.EndpointMetadata.Any(static metadata => metadata is OutputCacheAttribute))
                continue;

            selector.EndpointMetadata.Add(policyAttribute.ToOutputCacheAttribute());
        }
    }
}
