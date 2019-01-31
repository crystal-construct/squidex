﻿// ==========================================================================
//  Squidex Headless CMS
// ==========================================================================
//  Copyright (c) Squidex UG (haftungsbeschraenkt)
//  All rights reserved. Licensed under the MIT license.
// ==========================================================================

using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Squidex.Pipeline
{
    public class CleanupHostMiddleware
    {
        private readonly RequestDelegate next;

        public CleanupHostMiddleware(RequestDelegate next)
        {
            this.next = next;
        }

        public Task Invoke(HttpContext context)
        {
            var request = context.Request;

            if (request.Host.HasValue && (HasHttpsPort(request) || HasHttpPort(request)))
            {
                request.Host = new HostString(request.Host.Host);
            }

            return next(context);
        }

        private static bool HasHttpPort(HttpRequest request)
        {
            return request.Protocol == "http" && request.Host.Port == 80;
        }

        private static bool HasHttpsPort(HttpRequest request)
        {
            return request.Protocol == "https" && request.Host.Port == 443;
        }
    }
}