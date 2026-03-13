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
            var controllerPolicy = FindPolicyAttribute(controller.Attributes);
            var controllerHasNativeOutputCache = HasNativeOutputCacheAttribute(controller.Attributes);

            foreach (var action in controller.Actions)
            {
                if (controllerHasNativeOutputCache || HasNativeOutputCacheAttribute(action.Attributes))
                    continue;

                var policy = FindPolicyAttribute(action.Attributes) ?? controllerPolicy;
                if (policy is null)
                    continue;

                ApplySelectorMetadata(policy, action.Selectors);
            }
        }
    }

    private static VapeCachePolicyAttribute? FindPolicyAttribute(IReadOnlyList<object> attributes)
    {
        for (var index = 0; index < attributes.Count; index++)
        {
            if (attributes[index] is VapeCachePolicyAttribute policyAttribute)
                return policyAttribute;
        }

        return null;
    }

    private static bool HasNativeOutputCacheAttribute(IReadOnlyList<object> attributes)
    {
        for (var index = 0; index < attributes.Count; index++)
        {
            if (attributes[index] is OutputCacheAttribute)
                return true;
        }

        return false;
    }

    private static void ApplySelectorMetadata(VapeCachePolicyAttribute policy, IList<SelectorModel> selectors)
    {
        for (var selectorIndex = 0; selectorIndex < selectors.Count; selectorIndex++)
        {
            var selector = selectors[selectorIndex];
            var endpointMetadata = selector.EndpointMetadata;
            var hasOutputCacheMetadata = false;

            for (var metadataIndex = 0; metadataIndex < endpointMetadata.Count; metadataIndex++)
            {
                if (endpointMetadata[metadataIndex] is not OutputCacheAttribute)
                    continue;

                hasOutputCacheMetadata = true;
                break;
            }

            if (hasOutputCacheMetadata)
                continue;

            endpointMetadata.Add(policy.ToOutputCacheAttribute());
        }
    }
}
