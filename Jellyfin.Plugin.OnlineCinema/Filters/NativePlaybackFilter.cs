using Jellyfin.Plugin.OnlineCinema.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.OnlineCinema.Filters;

/// <summary>Redirect only explicitly registered temporary Online Cinema IDs.</summary>
public sealed class NativePlaybackFilter : IAsyncActionFilter
{
    private readonly NativePlaybackRegistry _items;
    public NativePlaybackFilter(NativePlaybackRegistry items) => _items = items;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var action = context.ActionDescriptor as ControllerActionDescriptor;
        if (action is null) { await next(); return; }
        if (!context.ActionArguments.TryGetValue("itemId", out var value) || value is not Guid id)
        { await next(); return; }
        var item = _items.Get(id);
        if (item is null) { await next(); return; }
        var req = context.HttpContext.Request;
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
