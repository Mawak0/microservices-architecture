using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Olympiad.Persistence.Postgres;
using Olympiad.Workflows.Application;
using Olympiad.Workflows.Domain;

namespace Olympiad.Workflows.Infrastructure;

public sealed class SavedRequestMiddleware(RequestDelegate next,HttpWorkflowOptions options)
{
    public async Task InvokeAsync(HttpContext context,IProcessStore store,Coordinator coordinator,IProcessClock clock)
    {
        if(context.GetEndpoint() is not RouteEndpoint endpoint || !options.Track(context.Request.Method,context.Request.Path))
        { await next(context); return; }
        options.Authorize(context);
        var actor=options.CaptureActor(context);
        var key=context.Request.Headers["Idempotency-Key"].ToString();
        if(string.IsNullOrWhiteSpace(key)||key.Length>200) { await Error(context,422,"VALIDATION_ERROR"); return; }
        using var input=new MemoryStream();
        var buffer=new byte[64*1024]; int count;
        while((count=await context.Request.Body.ReadAsync(buffer,context.RequestAborted))>0)
        {
            if(input.Length+count>24*1024*1024) { await Error(context,413,"REQUEST_TOO_LARGE"); return; }
            await input.WriteAsync(buffer.AsMemory(0,count),context.RequestAborted);
        }
        var arguments=input.ToArray();
        try { if(arguments.Length>0) { using var format=JsonDocument.Parse(arguments); } }
        catch(JsonException) { await Error(context,422,"VALIDATION_ERROR"); return; }
        var headers=new Dictionary<string,string> { ["Idempotency-Key"]=key };
        if(context.Request.Headers.TryGetValue("Assignment-Token",out var assignment)) headers["Assignment-Token"]=assignment.ToString();
        var scope=context.Request.Method+":"+context.Request.Path+context.Request.QueryString;
        var fingerprint=Fingerprint(arguments,headers.GetValueOrDefault("Assignment-Token",""));
        var invocation=new Invocation(endpoint.RoutePattern.RawText!,context.Request.Method,context.Request.Path+context.Request.QueryString,
            JsonSerializer.Serialize(context.Request.RouteValues.ToDictionary(p=>p.Key,p=>p.Value?.ToString())),JsonSerializer.Serialize(headers),actor.Snapshot,arguments);
        ProcessSnapshot accepted;
        try { accepted=await store.AcceptAsync(Process.Accept(Guid.NewGuid().ToString(),actor.Id,scope,key,fingerprint,invocation,options.Checkpoint(context.Request.Path),clock.UtcNow),context.RequestAborted); }
        catch(ProcessKeyConflictException) { await Error(context,409,"IDEMPOTENCY_KEY_REUSED"); return; }
        var first=accepted.Status==ProcessStatus.Accepted && accepted.Attempts==0;
        await coordinator.RunAsync(accepted.Id,Guid.NewGuid().ToString(),context.RequestAborted);
        var current=await store.FindAsync(accepted.Id,actor.Id,context.RequestAborted) ?? throw new InvalidOperationException("Saved process disappeared.");
        context.Response.Headers["Operation-Id"]=current.Id;
        context.Response.Headers["Operation-Status"]=current.Status.ToString();
        context.Response.Headers["Operation-Location"]="/operations/"+current.Id;
        if(current.Result is { } result && (current.Status is ProcessStatus.Completed or ProcessStatus.Failed or ProcessStatus.AwaitingCheckpoint or ProcessStatus.Compensating))
        {
            context.Response.StatusCode=first?result.StatusCode:options.ReplayCode(context.Request.Path,result.StatusCode);
            context.Response.ContentType=result.ContentType; await context.Response.Body.WriteAsync(result.Body,context.RequestAborted);
        }
        else { context.Response.StatusCode=202; await context.Response.WriteAsJsonAsync(View(current),context.RequestAborted); }
    }
    internal static object View(ProcessSnapshot process) => new
    {
        operationId=process.Id,status=process.Status.ToString(),process.Attempts,process.NextAttempt,
        lastError=process.Error,resourceId=process.Result?.ResourceId,resultStatus=process.Result?.StatusCode,
        process.CreatedAt,process.Version
    };
    private static Task Error(HttpContext c,int status,string code) { c.Response.StatusCode=status; return c.Response.WriteAsJsonAsync(new {code,message=code},c.RequestAborted); }
    private static string Fingerprint(byte[] arguments,string assignment)
    {
        using var canonical=new MemoryStream();
        if(arguments.Length>0)
        {
            using var json=JsonDocument.Parse(arguments);
            using var writer=new Utf8JsonWriter(canonical); WriteCanonical(writer,json.RootElement);
        }
        canonical.WriteByte((byte)'\n');
        canonical.Write(System.Text.Encoding.UTF8.GetBytes(assignment));
        return Convert.ToHexString(SHA256.HashData(canonical.ToArray()));
    }
    private static void WriteCanonical(Utf8JsonWriter writer,JsonElement element)
    {
        if(element.ValueKind==JsonValueKind.Object)
        {
            writer.WriteStartObject(); foreach(var property in element.EnumerateObject().OrderBy(p=>p.Name,StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(writer,property.Value); } writer.WriteEndObject();
        }
        else if(element.ValueKind==JsonValueKind.Array) { writer.WriteStartArray(); foreach(var child in element.EnumerateArray()) WriteCanonical(writer,child); writer.WriteEndArray(); }
        else element.WriteTo(writer);
    }
}
