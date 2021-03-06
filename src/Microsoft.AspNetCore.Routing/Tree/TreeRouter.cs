// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Routing.Internal;
using Microsoft.AspNetCore.Routing.Logging;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;

namespace Microsoft.AspNetCore.Routing.Tree
{
    /// <summary>
    /// An <see cref="IRouter"/> implementation for attribute routing.
    /// </summary>
    public class TreeRouter : IRouter
    {
        // Key used by routing and action selection to match an attribute route entry to a
        // group of action descriptors.
        public static readonly string RouteGroupKey = "!__route_group";

        private readonly LinkGenerationDecisionTree _linkGenerationTree;
        private readonly UrlMatchingTree[] _trees;
        private readonly IDictionary<string, OutboundMatch> _namedEntries;

        private readonly ILogger _logger;
        private readonly ILogger _constraintLogger;

        /// <summary>
        /// Creates a new <see cref="TreeRouter"/>.
        /// </summary>
        /// <param name="trees">The list of <see cref="UrlMatchingTree"/> that contains the route entries.</param>
        /// <param name="linkGenerationEntries">The set of <see cref="OutboundRouteEntry"/>.</param>
        /// <param name="urlEncoder">The <see cref="UrlEncoder"/>.</param>
        /// <param name="objectPool">The <see cref="ObjectPool{T}"/>.</param>
        /// <param name="routeLogger">The <see cref="ILogger"/> instance.</param>
        /// <param name="constraintLogger">The <see cref="ILogger"/> instance used
        /// in <see cref="RouteConstraintMatcher"/>.</param>
        /// <param name="version">The version of this route.</param>
        public TreeRouter(
            UrlMatchingTree[] trees,
            IEnumerable<OutboundRouteEntry> linkGenerationEntries,
            UrlEncoder urlEncoder,
            ObjectPool<UriBuildingContext> objectPool,
            ILogger routeLogger,
            ILogger constraintLogger,
            int version)
        {
            if (trees == null)
            {
                throw new ArgumentNullException(nameof(trees));
            }

            if (linkGenerationEntries == null)
            {
                throw new ArgumentNullException(nameof(linkGenerationEntries));
            }

            if (urlEncoder == null)
            {
                throw new ArgumentNullException(nameof(urlEncoder));
            }

            if (objectPool == null)
            {
                throw new ArgumentNullException(nameof(objectPool));
            }

            if (routeLogger == null)
            {
                throw new ArgumentNullException(nameof(routeLogger));
            }

            if (constraintLogger == null)
            {
                throw new ArgumentNullException(nameof(constraintLogger));
            }

            _trees = trees;
            _logger = routeLogger;
            _constraintLogger = constraintLogger;

            _namedEntries = new Dictionary<string, OutboundMatch>(StringComparer.OrdinalIgnoreCase);

            var outboundMatches = new List<OutboundMatch>();

            foreach (var entry in linkGenerationEntries)
            {

                var binder = new TemplateBinder(urlEncoder, objectPool, entry.RouteTemplate, entry.Defaults);
                var outboundMatch = new OutboundMatch() { Entry = entry, TemplateBinder = binder };
                outboundMatches.Add(outboundMatch);

                // Skip unnamed entries
                if (entry.RouteName == null)
                {
                    continue;
                }

                // We only need to keep one OutboundMatch per route template
                // so in case two entries have the same name and the same template we only keep
                // the first entry.
                OutboundMatch namedMatch;
                if (_namedEntries.TryGetValue(entry.RouteName, out namedMatch) &&
                    !string.Equals(
                        namedMatch.Entry.RouteTemplate.TemplateText,
                        entry.RouteTemplate.TemplateText,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        Resources.FormatAttributeRoute_DifferentLinkGenerationEntries_SameName(entry.RouteName),
                        nameof(linkGenerationEntries));
                }
                else if (namedMatch == null)
                {
                    _namedEntries.Add(entry.RouteName, outboundMatch);
                }
            }

            // The decision tree will take care of ordering for these entries.
            _linkGenerationTree = new LinkGenerationDecisionTree(outboundMatches.ToArray());

            Version = version;
        }

        /// <summary>
        /// Gets the version of this route.
        /// </summary>
        public int Version { get; }

        internal IEnumerable<UrlMatchingTree> MatchingTrees => _trees;

        /// <inheritdoc />
        public VirtualPathData GetVirtualPath(VirtualPathContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            // If it's a named route we will try to generate a link directly and
            // if we can't, we will not try to generate it using an unnamed route.
            if (context.RouteName != null)
            {
                return GetVirtualPathForNamedRoute(context);
            }

            // The decision tree will give us back all entries that match the provided route data in the correct
            // order. We just need to iterate them and use the first one that can generate a link.
            var matches = _linkGenerationTree.GetMatches(context);

            if (matches == null)
            {
                return null;
            }

            for (var i = 0; i < matches.Count; i++)
            {
                var path = GenerateVirtualPath(context, matches[i].Match.Entry, matches[i].Match.TemplateBinder);
                if (path != null)
                {
                    return path;
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task RouteAsync(RouteContext context)
        {
            foreach (var tree in _trees)
            {
                var tokenizer = new PathTokenizer(context.HttpContext.Request.Path);
                var root = tree.Root;

                var treeEnumerator = new TreeEnumerator(root, tokenizer);

                // Create a snapshot before processing the route. We'll restore this snapshot before running each
                // to restore the state. This is likely an "empty" snapshot, which doesn't allocate.
                var snapshot = context.RouteData.PushState(router: null, values: null, dataTokens: null);

                while (treeEnumerator.MoveNext())
                {
                    var node = treeEnumerator.Current;
                    foreach (var item in node.Matches)
                    {
                        var entry = item.Entry;
                        var matcher = item.TemplateMatcher;
                        if (!matcher.TryMatch(context.HttpContext.Request.Path, context.RouteData.Values))
                        {
                            continue;
                        }

                        try
                        {
                            if (!RouteConstraintMatcher.Match(
                                entry.Constraints,
                                context.RouteData.Values,
                                context.HttpContext,
                                this,
                                RouteDirection.IncomingRequest,
                                _constraintLogger))
                            {
                                continue;
                            }

                            _logger.MatchedRoute(entry.RouteName, entry.RouteTemplate.TemplateText);
                            context.RouteData.Routers.Add(entry.Handler);

                            await entry.Handler.RouteAsync(context);
                            if (context.Handler != null)
                            {
                                return;
                            }
                        }
                        finally
                        {
                            if (context.Handler == null)
                            {
                                // Restore the original values to prevent polluting the route data.
                                snapshot.Restore();
                            }
                        }
                    }
                }
            }
        }

        private struct TreeEnumerator : IEnumerator<UrlMatchingNode>
        {
            private readonly Stack<UrlMatchingNode> _stack;
            private readonly PathTokenizer _tokenizer;

            public TreeEnumerator(UrlMatchingNode root, PathTokenizer tokenizer)
            {
                _stack = new Stack<UrlMatchingNode>();
                _tokenizer = tokenizer;
                Current = null;

                _stack.Push(root);
            }

            public UrlMatchingNode Current { get; private set; }

            object IEnumerator.Current => Current;

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                if (_stack == null)
                {
                    return false;
                }

                while (_stack.Count > 0)
                {
                    var next = _stack.Pop();

                    // In case of wild card segment, the request path segment length can be greater
                    // Example:
                    // Template:    a/{*path}
                    // Request Url: a/b/c/d
                    if (next.IsCatchAll && next.Matches.Count > 0)
                    {
                        Current = next;
                        return true;
                    }
                    // Next template has the same length as the url we are trying to match
                    // The only possible matching segments are either our current matches or
                    // any catch-all segment after this segment in which the catch all is empty.
                    else if (next.Depth == _tokenizer.Count)
                    {
                        if (next.Matches.Count > 0)
                        {
                            Current = next;
                            return true;
                        }
                        else
                        {
                            // We can stop looking as any other child node from this node will be
                            // either a literal, a constrained parameter or a parameter.
                            // (Catch alls and constrained catch alls will show up as candidate matches).
                            continue;
                        }
                    }

                    if (next.CatchAlls != null)
                    {
                        _stack.Push(next.CatchAlls);
                    }

                    if (next.ConstrainedCatchAlls != null)
                    {
                        _stack.Push(next.ConstrainedCatchAlls);
                    }

                    if (next.Parameters != null)
                    {
                        _stack.Push(next.Parameters);
                    }

                    if (next.ConstrainedParameters != null)
                    {
                        _stack.Push(next.ConstrainedParameters);
                    }

                    if (next.Literals.Count > 0)
                    {
                        UrlMatchingNode node;
                        Debug.Assert(next.Depth < _tokenizer.Count);
                        if (next.Literals.TryGetValue(_tokenizer[next.Depth].Value, out node))
                        {
                            _stack.Push(node);
                        }
                    }
                }

                return false;
            }

            public void Reset()
            {
                _stack.Clear();
                Current = null;
            }
        }

        private VirtualPathData GetVirtualPathForNamedRoute(VirtualPathContext context)
        {
            OutboundMatch match;
            if (_namedEntries.TryGetValue(context.RouteName, out match))
            {
                var path = GenerateVirtualPath(context, match.Entry, match.TemplateBinder);
                if (path != null)
                {
                    return path;
                }
            }
            return null;
        }

        private VirtualPathData GenerateVirtualPath(
            VirtualPathContext context,
            OutboundRouteEntry entry,
            TemplateBinder binder)
        {
            // In attribute the context includes the values that are used to select this entry - typically
            // these will be the standard 'action', 'controller' and maybe 'area' tokens. However, we don't
            // want to pass these to the link generation code, or else they will end up as query parameters.
            //
            // So, we need to exclude from here any values that are 'required link values', but aren't
            // parameters in the template.
            //
            // Ex:
            //      template: api/Products/{action}
            //      required values: { id = "5", action = "Buy", Controller = "CoolProducts" }
            //
            //      result: { id = "5", action = "Buy" }
            var inputValues = new RouteValueDictionary();
            foreach (var kvp in context.Values)
            {
                if (entry.RequiredLinkValues.ContainsKey(kvp.Key))
                {
                    var parameter = entry.RouteTemplate.GetParameter(kvp.Key);

                    if (parameter == null)
                    {
                        continue;
                    }
                }

                inputValues.Add(kvp.Key, kvp.Value);
            }

            var bindingResult = binder.GetValues(context.AmbientValues, inputValues);
            if (bindingResult == null)
            {
                // A required parameter in the template didn't get a value.
                return null;
            }

            var matched = RouteConstraintMatcher.Match(
                entry.Constraints,
                bindingResult.CombinedValues,
                context.HttpContext,
                this,
                RouteDirection.UrlGeneration,
                _constraintLogger);

            if (!matched)
            {
                // A constraint rejected this link.
                return null;
            }

            var pathData = entry.Handler.GetVirtualPath(context);
            if (pathData != null)
            {
                // If path is non-null then the target router short-circuited, we don't expect this
                // in typical MVC scenarios.
                return pathData;
            }

            var path = binder.BindValues(bindingResult.AcceptedValues);
            if (path == null)
            {
                return null;
            }

            return new VirtualPathData(this, path);
        }
    }
}
