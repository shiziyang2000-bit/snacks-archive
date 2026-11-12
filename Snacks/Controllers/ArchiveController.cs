using Microsoft.AspNetCore.Mvc;
using Snacks.Models;
using Snacks.Services;

namespace Snacks.Controllers;

[ApiController]
[Route("api/archive")]
public sealed class ArchiveController(ArchiveService archive, TranscodingService transcoding) : ControllerBase
{
    private async Task<IActionResult> Respond(Func<Task<object>> action)
    {
        try{return Ok(await action());}
        catch(ArchiveException e){return StatusCode(e.Code=="not_found"?404:409,new{code=e.Code,message=e.Message,details=e.Details});}
        catch(OperationCanceledException){return StatusCode(499,new{code="cancelled",message="操作已取消。",details=(object?)null});}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){return StatusCode(409,new{code="storage_unavailable",message=e.Message,details=(object?)null});}
    }
    [HttpGet("settings")]public IActionResult Settings()=>Ok(archive.GetSettings());
    [HttpPut("settings")]public Task<IActionResult> Settings([FromBody]ArchiveSettings settings)=>Respond(()=>Task.FromResult<object>(archive.SaveSettings(settings)));
    [HttpPost("folders/start")]public Task<IActionResult> StartFolder([FromBody]ArchiveFolderStartRequest request)=>Respond(()=>archive.StartFolderAsync(request,HttpContext.RequestAborted));
    [HttpGet("folders/status")]public Task<IActionResult> FolderStatus()=>Respond(()=>archive.FolderStatusAsync());
    [HttpPost("preview")]public Task<IActionResult> Preview([FromBody]ArchivePreviewRequest request)=>Respond(()=>archive.PreviewAsync(request,HttpContext.RequestAborted));
    [HttpPost("jobs")]public Task<IActionResult> Start([FromBody]ArchiveStartRequest request)=>Respond(()=>archive.StartAsync(request,HttpContext.RequestAborted));
    [HttpGet("jobs/{id}")]public Task<IActionResult> Job(string id)=>Respond(()=>archive.JobAsync(id));
    [HttpPost("jobs/{id}/pause")]public Task<IActionResult> Pause(string id)=>Respond(async()=>{await archive.PauseJobAsync(id,"用户暂停目录任务，请手动继续。");return new {jobId=id};});
    [HttpPost("jobs/{id}/continue")]public Task<IActionResult> Continue(string id)=>Respond(()=>archive.ContinueAsync(id));
    [HttpPut("jobs/{id}/pending-output")]public Task<IActionResult> PendingOutput(string id,[FromBody]ArchiveOutputPolicy choice)=>Respond(()=>archive.ApplyPendingOutputAsync(id,choice));
    [HttpGet("history")]public Task<IActionResult> History(string? jobId=null,string? status=null,int skip=0,int take=50)=>Respond(()=>archive.HistoryAsync(jobId,status,skip,take));
    [HttpPost("restore/preview")]public Task<IActionResult> RestorePreview([FromBody]ArchiveSelectionRequest request)=>Respond(()=>archive.RestorePreviewAsync(request));
    [HttpPost("restore")]public Task<IActionResult> Restore([FromBody]ArchiveRestoreRequest request)=>Respond(()=>archive.RestoreAsync(request));
    [HttpPost("rescore")]public Task<IActionResult> Rescore([FromBody]ArchiveSelectionRequest request)=>Respond(()=>archive.RescoreAsync(request,HttpContext.RequestAborted));
    [HttpGet("cache")]public Task<IActionResult> Cache()=>Respond(()=>archive.CacheInfoAsync());
    [HttpPost("cache/clear")]public Task<IActionResult> ClearCache()=>Respond(()=>archive.ClearCacheAsync());
    [HttpPost("shutdown")]public Task<IActionResult> Shutdown()=>Respond(async()=>{await transcoding.SafeShutdownAsync();return new{safeToQuit=true};});
}
