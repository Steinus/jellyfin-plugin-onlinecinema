using Jellyfin.Plugin.OnlineCinema.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.OnlineCinema.Filters;

/// <summary>Redirect only explicitly registered temporary Online Cinema IDs.</summary>
public sealed class NativePlaybackFilter : IAsyncActionFilter
{
    private readonly NativePlaybackRegistry _items;
    private readonly TmdbImageService _artwork;
    public NativePlaybackFilter(NativePlaybackRegistry items, TmdbImageService artwork)
    {
        _items = items;
        _artwork = artwork;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var action = context.ActionDescriptor as ControllerActionDescriptor;
        if (action is null) { await next(); return; }
        if (!context.ActionArguments.TryGetValue("itemId", out var value) || value is not Guid id)
        { await next(); return; }
        var req = context.HttpContext.Request;
        var catalog = _items.GetCatalog(id);
        if (catalog is not null)
        {
            if (action.ControllerName == "UserLibrary" && action.ActionName is "GetItem" or "GetItemLegacy")
            {
                context.Result = new OkObjectResult(_items.BuildCatalogItem(catalog));
                return;
            }
            if (action.ControllerName == "Image" && action.ActionName.StartsWith("GetItemImage", StringComparison.Ordinal)
                && context.ActionArguments.TryGetValue("imageType", out var kind) && kind is ImageType imageType)
            {
                var imagePath = _items.GetCatalogImage(id, imageType);
                if (!string.IsNullOrWhiteSpace(imagePath))
                {
                    var parts = imagePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 4 && parts[0] == "OnlineCinema" && parts[1] == "images")
                    {
                        try
                        {
                            var image = await _artwork.GetAsync(parts[2], parts[3], req.HttpContext.RequestAborted);
                            context.Result = new FileContentResult(image.Bytes, image.ContentType);
                            return;
                        }
                        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or KeyNotFoundException)
                        {
                            context.Result = new StatusCodeResult(502);
                            return;
                        }
                    }
                }
            }
            if (action.ControllerName == "MediaInfo" && action.ActionName is "GetPlaybackInfo" or "GetPostedPlaybackInfo")
            {
                context.Result = new OkObjectResult(new PlaybackInfoResponse { MediaSources = [] });
                return;
            }
        }
        var item = _items.Get(id);
        if (item is null) { await next(); return; }
        if ((action.ControllerName == "UserLibrary" && (action.ActionName == "GetItem" || action.ActionName == "GetItemLegacy"))
            || (action.ControllerName == "Items" && action.ActionName == "GetItem"))
        {
            context.Result = new OkObjectResult(_items.BuildItem(item, req)); return;
        }
        if (action.ControllerName == "MediaInfo" && (action.ActionName == "GetPlaybackInfo" || action.ActionName == "GetPostedPlaybackInfo"))
        {
            context.Result = new OkObjectResult(_items.BuildPlayback(item, req)); return;
        }
        if (action.ControllerName == "Videos" && (action.ActionName == "GetVideoStream" || action.ActionName == "GetVideoStreamByContainer"))
        {
            context.Result = new RedirectResult(_items.StreamRedirect(item, req)); return;
        }
        await next();
    }
}
