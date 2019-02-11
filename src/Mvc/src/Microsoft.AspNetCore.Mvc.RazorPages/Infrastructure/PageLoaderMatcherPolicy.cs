// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;

namespace Microsoft.AspNetCore.Mvc.RazorPages.Infrastructure
{
    internal class PageLoaderMatcherPolicy : MatcherPolicy, IEndpointSelectorPolicy
    {
        private readonly IPageLoader _loader;
        private readonly MvcEndpointInvokerFactory _invokerFactory;

        public PageLoaderMatcherPolicy(IPageLoader loader, MvcEndpointInvokerFactory invokerFactory)
        {
            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            if (invokerFactory == null)
            {
                throw new ArgumentNullException(nameof(invokerFactory));
            }

            _loader = loader;
            _invokerFactory = invokerFactory;
        }

        public override int Order => int.MinValue + 100;

        public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints)
        {
            if (endpoints == null)
            {
                throw new ArgumentNullException(nameof(endpoints));
            }
            
            for (var i = 0; i < endpoints.Count; i++)
            {
                var page = endpoints[i].Metadata.GetMetadata<PageActionDescriptor>();
                if (page != null)
                {
                    // Found a page
                    return true;
                }
            }

            return false;
        }

        public Task ApplyAsync(HttpContext httpContext, EndpointSelectorContext context, CandidateSet candidates)
        {
            if (httpContext == null)
            {
                throw new ArgumentNullException(nameof(httpContext));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (candidates == null)
            {
                throw new ArgumentNullException(nameof(candidates));
            }

            for (var i = 0; i < candidates.Count; i++)
            {
                ref var candidate = ref candidates[i];
                var endpoint = (RouteEndpoint)candidate.Endpoint;

                var page = endpoint.Metadata.GetMetadata<PageActionDescriptor>();
                if (page != null)
                {
                    var compiled = _loader.Load(page);

                    // This is wrong - we don't want to allocate this per-request. #YOLO
                    RequestDelegate requestDelegate = (c) =>
                    {
                        var routeData = c.GetRouteData();

                        var actionContext = new ActionContext(c, routeData, compiled);

                        var invoker = _invokerFactory.CreateInvoker(actionContext);
                        return invoker.InvokeAsync();
                    };

                    var endpointBuilder = new RouteEndpointBuilder(requestDelegate, endpoint.RoutePattern, endpoint.Order)
                    {
                        DisplayName = endpoint.DisplayName,
                    };

                    // Add action metadata first so it has a low precedence
                    if (compiled.EndpointMetadata != null)
                    {
                        foreach (var d in compiled.EndpointMetadata)
                        {
                            endpointBuilder.Metadata.Add(d);
                        }
                    }

                    endpointBuilder.Metadata.Add(compiled);

                    // Add filter descriptors to endpoint metadata
                    if (compiled.FilterDescriptors != null && compiled.FilterDescriptors.Count > 0)
                    {
                        foreach (var filter in compiled.FilterDescriptors.OrderBy(f => f, FilterDescriptorOrderComparer.Comparer).Select(f => f.Filter))
                        {
                            endpointBuilder.Metadata.Add(filter);
                        }
                    }

                    if (compiled.ActionConstraints != null && compiled.ActionConstraints.Count > 0)
                    {
                        // We explicitly convert a few types of action constraints into MatcherPolicy+Metadata
                        // to better integrate with the DFA matcher.
                        //
                        // Other IActionConstraint data will trigger a back-compat path that can execute
                        // action constraints.
                        foreach (var actionConstraint in compiled.ActionConstraints)
                        {
                            if (actionConstraint is HttpMethodActionConstraint httpMethodActionConstraint &&
                                !endpointBuilder.Metadata.OfType<HttpMethodMetadata>().Any())
                            {
                                endpointBuilder.Metadata.Add(new HttpMethodMetadata(httpMethodActionConstraint.HttpMethods));
                            }
                            else if (actionConstraint is ConsumesAttribute consumesAttribute &&
                                !endpointBuilder.Metadata.OfType<ConsumesMetadata>().Any())
                            {
                                endpointBuilder.Metadata.Add(new ConsumesMetadata(consumesAttribute.ContentTypes.ToArray()));
                            }
                            else if (!endpointBuilder.Metadata.Contains(actionConstraint))
                            {
                                // The constraint might have been added earlier, e.g. it is also a filter descriptor
                                endpointBuilder.Metadata.Add(actionConstraint);
                            }
                        }
                    }

                    if (compiled.AttributeRouteInfo.SuppressLinkGeneration)
                    {
                        endpointBuilder.Metadata.Add(new SuppressLinkGenerationMetadata());
                    }

                    if (compiled.AttributeRouteInfo.SuppressPathMatching)
                    {
                        endpointBuilder.Metadata.Add(new SuppressMatchingMetadata());
                    }

                    candidates.ReplaceEndpoint(i, endpointBuilder.Build(), candidate.Values);
                }
            }

            return Task.CompletedTask;
        }
    }
}
