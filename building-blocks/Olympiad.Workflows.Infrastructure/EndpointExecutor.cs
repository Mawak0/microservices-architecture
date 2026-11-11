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

public sealed class EndpointExecutor(IServiceScopeFactory scopes,EndpointDataSource endpoints,HttpWorkflowOptions options,ILogger<EndpointExecutor> logger) : ICommandExecutor
{
    public async Task<Execution> ExecuteAsync(ProcessSnapshot process,CancellationToken ct)
    {
        var invocation=process.Invocation ?? throw new InvalidOperationException("The active command has no saved arguments.");
        var endpoint=endpoints.Endpoints.OfType<RouteEndpoint>().SingleOrDefault(e => e.RoutePattern.RawText==invocation.Handler &&
            (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(invocation.Method) ?? false));
        if(endpoint?.RequestDelegate is null) return new(new(503,"application/json",[]),true,"HANDLER_UNAVAILABLE");
        await using var scope=scopes.CreateAsyncScope();
        var context=new DefaultHttpContext { RequestServices=scope.ServiceProvider };
        using var executionTimeout=CancellationTokenSource.CreateLinkedTokenSource(ct); executionTimeout.CancelAfter(options.Lease*0.8);
        context.RequestAborted=executionTimeout.Token;
        context.Request.Method=invocation.Method;
        var query=invocation.Path.IndexOf('?');
        context.Request.Path=query<0?invocation.Path:invocation.Path[..query];
        if(query>=0) context.Request.QueryString=new(invocation.Path[query..]);
        context.Request.Scheme="http"; context.Request.Host=new("workflow.local");
        context.Request.ContentType="application/json"; context.Request.ContentLength=invocation.Arguments.Length;
        context.Request.Body=new MemoryStream(invocation.Arguments,writable:false);
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new SavedBodyFeature(invocation.Arguments.Length > 0));
        foreach(var (name,value) in JsonSerializer.Deserialize<Dictionary<string,string>>(invocation.Headers)!) context.Request.Headers[name]=value;
        context.Request.RouteValues=new RouteValueDictionary(JsonSerializer.Deserialize<Dictionary<string,string?>>(invocation.RouteValues)!);
        context.SetEndpoint(endpoint); options.RestoreActor(context,invocation.Actor);
        context.Items[typeof(ProcessSnapshot)]=process;
        using var response=new MemoryStream(); context.Response.Body=response;
        try
        {
            await endpoint.RequestDelegate(context);
            var body=response.ToArray(); string? resource=null;
            if(context.Response.StatusCode<400 && body.Length>0)
            {
                using var json=JsonDocument.Parse(body);
                if(json.RootElement.ValueKind==JsonValueKind.Object && json.RootElement.TryGetProperty("id",out var id)) resource=id.GetString();
            }
            return new(new(context.Response.StatusCode,context.Response.ContentType??"application/json",body,resource),context.Response.StatusCode>=500,
                context.Response.StatusCode>=500?"DEPENDENCY_UNAVAILABLE":null);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
        catch(Exception exception)
        {
            var error=options.ClassifyError(exception);
            if(error.Status>=500) logger.LogWarning(exception,"Process {ProcessId}: execution failed with {Code}",process.Id,error.Code);
            return new(new(error.Status,"application/json",JsonSerializer.SerializeToUtf8Bytes(new { code=error.Code,message=error.Message }),error.CompensationResource),
                error.Status>=500,error.Code,error.CompensationResource is not null);
        }
        finally { context.Request.Body.Dispose(); }
    }
    private sealed record SavedBodyFeature(bool CanHaveBody) : IHttpRequestBodyDetectionFeature;
}
