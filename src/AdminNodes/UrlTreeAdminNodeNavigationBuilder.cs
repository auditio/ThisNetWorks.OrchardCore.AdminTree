using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Fluid;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrchardCore.AdminMenu.Services;
using OrchardCore.Autoroute.Models;
using OrchardCore.ContentManagement;
using OrchardCore.ContentManagement.Metadata;
using OrchardCore.ContentManagement.Records;
using OrchardCore.ContentManagement.Routing;
using OrchardCore.Contents.Security;
using OrchardCore.Liquid;
using OrchardCore.Navigation;
using OrchardCore.Settings;
using YesSql;

namespace ThisNetWorks.OrchardCore.AdminTree.AdminNodes
{
    //TODO Breaks if you have two of these.
    public class UrlTreeAdminNodeNavigationBuilder : IAdminNodeNavigationBuilder
    {
        private readonly ISiteService _siteService;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IContentDefinitionManager _contentDefinitionManager;
        private readonly ILiquidTemplateManager _liquidTemplatemanager;
        private readonly IContentManager _contentManager;
        private readonly YesSql.ISession _session;
        private readonly ILogger<UrlTreeAdminNodeNavigationBuilder> _logger;

        //TODO implement and figure out how we might best handle
        private const int MaxItemsInNode = 100; // security check

        private readonly AutorouteOptions _options;
        private readonly IStringLocalizer<UrlTreeAdminNodeNavigationBuilder> T;
        public UrlTreeAdminNodeNavigationBuilder(
            ISiteService siteService,
            IHttpContextAccessor httpContextAccessor,
            IContentDefinitionManager contentDefinitionManager,
            ILiquidTemplateManager liquidTemplateManager,
            IContentManager contentManager,
            YesSql.ISession session,
            IOptions<AutorouteOptions> options,
            IStringLocalizer<UrlTreeAdminNodeNavigationBuilder> stringLocalizer,
            ILogger<UrlTreeAdminNodeNavigationBuilder> logger)
        {
            _siteService = siteService;
            _httpContextAccessor = httpContextAccessor;
            _contentDefinitionManager = contentDefinitionManager;
            _liquidTemplatemanager = liquidTemplateManager;
            _contentManager = contentManager;
            _session = session;
            _logger = logger;
            _options = options.Value;
            T = stringLocalizer;
        }

        public string Name => typeof(UrlTreeAdminNode).Name;

        public async Task BuildNavigationAsync(MenuItem menuItem, NavigationBuilder builder, IEnumerable<IAdminNodeNavigationBuilder> treeNodeBuilders)
        {
            var node = menuItem as UrlTreeAdminNode;

            if ((node == null) || (!node.Enabled))
            {
                return;
            }

            //TODO cache (actually cache the levels.)
            var contentItems = (await _session
                .Query<ContentItem>()
                .With<AutoroutePartIndex>(o => o.Published)
                .ListAsync()).ToList();


            var homeRoute = (await _siteService.GetSiteSettingsAsync()).HomeRoute;

            // Return on no homeroute (initially)
            if (homeRoute == null)
            {
                return;
            }

            var homeRouteContentItemId = homeRoute[_options.ContentItemIdKey]?.ToString();
            if (String.IsNullOrEmpty(homeRouteContentItemId))
            {
                return;
            }

            var homeRouteContentItem = await _contentManager.GetAsync(homeRouteContentItemId);
            if (homeRouteContentItem == null)
            {
                return;
            }

            var homeRouteMetadata = await _contentManager.PopulateAspectAsync<ContentItemMetadata>(homeRouteContentItem);
            if (!homeRouteMetadata.AdminRouteValues.Any())
            {
                return;
            }
            // In case of lists, which use display
            homeRouteMetadata.AdminRouteValues["Action"] = "Edit";

            var templateContext = new TemplateContext();
            templateContext.SetValue("ContentItem", homeRouteContentItem);

            var rootMenuText = await _liquidTemplatemanager.RenderAsync(node.TreeRootDisplayPattern, NullEncoder.Default, templateContext);

            // TODO Cache the levels, not the ContentItems.
            // Split the child menu levels
            var levels = new List<Level>();
            foreach (var ci in contentItems)
            {
                var part = ci.As<AutoroutePart>();
                var contentItemSegments = new ContentItemSegments
                {
                    Segments = part.Path.Contains("/") ? part.Path.Split('/') : new string [] { part.Path },
                    Path = part.Path,
                    ContentItem = ci
                };

                _logger.LogDebug("Building level for {Path} for content item {DisplayText}", contentItemSegments.Path, contentItemSegments.ContentItem.DisplayText);
                await BuildLevelAsync(levels, contentItemSegments, contentItems, node);
            }

            var segments = new List<ContentItemSegment2>();
            foreach(var ci in contentItems)
            {
                var part = ci.As<AutoroutePart>();
                var contentItemSegment = new ContentItemSegment2
                {
                    Segments = part.Path.Contains("/") ? part.Path.Split('/') : new string[] { part.Path },
                    Path = part.Path,
                    ContentItem = ci
                };
                segments.Add(contentItemSegment);
            }

            var levels2 = new List<Level2>();
            await BuildLevel2(levels2, segments, node, 0);

            //TODO Dynamic string localization not supported yet.
            await builder.AddAsync(new LocalizedString(rootMenuText, rootMenuText), async urlTreeRoot =>
            {
                var contentTypeDefinition = _contentDefinitionManager.GetTypeDefinition(homeRouteContentItem.ContentType);
                urlTreeRoot.Action(homeRouteMetadata.AdminRouteValues["Action"] as string, homeRouteMetadata.AdminRouteValues["Controller"] as string, homeRouteMetadata.AdminRouteValues);
                urlTreeRoot.Resource(homeRouteContentItem);
                urlTreeRoot.Priority(node.Priority);
                urlTreeRoot.Position(node.Position);
                urlTreeRoot.LocalNav();
                AddPrefixToClasses(node.IconForTree).ToList().ForEach(c => urlTreeRoot.AddClass(c));

                urlTreeRoot.Permission(ContentTypePermissions.CreateDynamicPermission(
                    ContentTypePermissions.PermissionTemplates[global::OrchardCore.Contents.Permissions.EditContent.Name], contentTypeDefinition));
                await BuildMenuLevels(urlTreeRoot, levels);
            });
        }

        private async Task BuildMenuLevels(NavigationItemBuilder urlTreeRoot, List<Level> levels)
        {
            foreach (var level in levels)
            {
                ContentItemMetadata cim = null;
                // Not all segments will have a content item associated with them.
                if (level.ContentItem != null)
                {
                    cim = await _contentManager.PopulateAspectAsync<ContentItemMetadata>(level.ContentItem);
                }
                // TODO fix for list, which by default uses display.
                if (cim != null)
                {
                    cim.AdminRouteValues["Action"] = "Edit";
                }

                await urlTreeRoot.AddAsync(level.DisplayText, level.DisplayText, async menuLevel =>
                {
                    if (level.ContentItem != null)
                    {
                        menuLevel.Action(cim.AdminRouteValues["Action"] as string, cim.AdminRouteValues["Controller"] as string, cim.AdminRouteValues);
                        menuLevel.Resource(level.ContentItem);
                        var contentTypeDefinition = _contentDefinitionManager.GetTypeDefinition(level.ContentItem.ContentType);
                        menuLevel.Permission(ContentTypePermissions.CreateDynamicPermission(
                            ContentTypePermissions.PermissionTemplates[global::OrchardCore.Contents.Permissions.EditContent.Name], contentTypeDefinition));
                    }
                    //menuLevel.Caption(T["test"]);
                    await BuildMenuLevels(menuLevel, level.SubLevels);
                });
            }
        }


        private async Task BuildLevel2(List<Level2> levels, List<ContentItemSegment2> contentItemSegments, UrlTreeAdminNode node, int index)
        {
            var segments = contentItemSegments
                .Where(x => x.Segments.Length > 0)
                .Select(x => x.Segments[0]).Distinct();

            foreach(var segment in segments)
            {
                // has no more segments. maybe null if no content item on that route.
                // this needs to match on path. substring maybe?

                var thisSegment = contentItemSegments
                    .FirstOrDefault(x => x.Segments.Length == 1 && x.Segments[0] == segment);

                var level = new Level2()
                {
                    Index = index,
                    Segment = segment,
                    Path = thisSegment?.Path,
                    ContentItem = thisSegment?.ContentItem
                };

                var children = contentItemSegments.Where(x => x != thisSegment &&
                    x.Segments.Length > 0 && x.Segments[0] == segment).ToList();

                if (children.Count > 0)
                {
                    foreach (var child in children)
                    {
                        child.Segments = child.Segments.Skip(1).ToArray();
                    }
                    await BuildLevel2(level.SubLevels, children, node, index + 1);
                    // This half works doing it here. need a better way to know if there is a blank / no ci route.
                    levels.Add(level);
                }
            }

        }
        private async Task BuildLevelAsync(List<Level> levels, ContentItemSegments contentItemSegments, List<ContentItem> contentItems, UrlTreeAdminNode node)
        {
            Level level = null;
            for (var i = 0; i < contentItemSegments.Segments.Length; i++)
            {
                // Assign
                if (level == null)
                {
                    level = levels.FirstOrDefault(x => x.Index == i && x.Segment == contentItemSegments.Segments[i]);
                }

                // If not there already, add it.
                if (level == null)
                {
                    level = new Level()
                    {
                        Index = i,
                        Segment = contentItemSegments.Segments[i],
                        Path = contentItemSegments.Path
                    };

                    _logger.LogDebug("Adding level {Level} for Segment {Segment}, DisplayText {DisplayText}", i, level.Segment, contentItemSegments.ContentItem.DisplayText);
                    levels.Add(level);
                }

                // If there is a next level, create it.
                if (i > level.Index)
                {
                    var newSegments = new string[contentItemSegments.Segments.Length - i];
                    Array.Copy(contentItemSegments.Segments, i, newSegments, 0, contentItemSegments.Segments.Length - i);
                    var subLevelSegments = new ContentItemSegments
                    {
                        Segments = newSegments,
                        Path = contentItemSegments.Path,
                        ContentItem = contentItemSegments.ContentItem
                    };

                    _logger.LogDebug("Building sublevel {Level} for {Path} for content item {DisplayText}", i, contentItemSegments.Path, contentItemSegments.ContentItem.DisplayText);
                    await BuildLevelAsync(level.SubLevels, subLevelSegments, contentItems, node);
                    break;
                }

                // If this is the last one in the segment
                if (i == contentItemSegments.Segments.Length - 1)
                {
                    level.ContentItem = contentItems.FirstOrDefault(x => x.As<AutoroutePart>().Path == level.Path);
                    //_logger.LogDebug("Assigning content item to path {Path}, for content item {DisplayText}", level.Path, lastCi.DisplayText);
                    if (node.UseItemSegmentForDisplay)
                    {
                        level.DisplayText = new LocalizedString(level.Segment, level.Segment);
                        _logger.LogDebug("Using segment {Segment} for level {Level} menu text for path {Path}, for content item {DisplayText}", i, level.Segment, level.Path, level.ContentItem.DisplayText);
                    }
                    else if (level.ContentItem == null)
                    {
                        _logger.LogDebug("Content item is null, using segment {Segment} forlevel {Level} menu text for path {Path}", i, level.Segment, level.Path);

                        level.DisplayText = new LocalizedString(level.Segment, level.Segment);
                    }
                    else
                    {
                        var templateContext = new TemplateContext();
                        templateContext.SetValue("ContentItem", level.ContentItem);

                        var display = await _liquidTemplatemanager.RenderAsync(node.ItemDisplayPattern, NullEncoder.Default, templateContext);
                        // In case of bad liquid
                        if (display == null)
                        {
                            _logger.LogDebug("Bad liquid setting display text for segment {Segment} on path {Path}", level.Segment, level.Path);
                            level.DisplayText = new LocalizedString(level.Segment, level.Segment);
                        }
                        else
                        {

                            level.DisplayText = new LocalizedString(display, display);
                        }
                    }
                }
                // this is the problem, it's not always finding a content item still
                // but this is not the fix
                if (String.IsNullOrEmpty(level.DisplayText))
                {
                    _logger.LogDebug("Did not find a final segment for this level {Level} segment {Segment}", i, level.Segment);
                    level.DisplayText = new LocalizedString(level.Segment, level.Segment);
                }
            }
        }

        public class Level
        {
            public int Index { get; set; }
            public string Segment { get; set; }
            public string Path { get; set; }
            public LocalizedString DisplayText { get; set; }
            public ContentItem ContentItem { get; set; }

            public List<Level> SubLevels { get; set; } = new List<Level>();
        }

        public class ContentItemSegments
        {
            public string[] Segments { get; set; }
            public string Path { get; set; }
            public ContentItem ContentItem { get; set; }
        }

        public class Level2
        {
            public int Index { get; set; }
            public string Segment { get; set; }
            public string Path { get; set; }
            public LocalizedString DisplayText { get; set; }
            public ContentItem ContentItem { get; set; }

            public List<Level2> SubLevels { get; set; } = new List<Level2>();
        }

        public class ContentItemSegment2
        {
            public string[] Segments { get; set; }
            public string Path { get; set; }
            public ContentItem ContentItem { get; set; }
        }

        private List<string> AddPrefixToClasses(string unprefixed)
        {
            return unprefixed?.Split(' ')
                .ToList()
                .Select(c => "icon-class-" + c)
                .ToList<string>()
                ?? new List<string>();
        }
    }
}
