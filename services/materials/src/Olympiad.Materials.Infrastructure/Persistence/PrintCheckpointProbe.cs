using Olympiad.Workflows.Application;

namespace Olympiad.Materials.Infrastructure.Persistence;

/// <summary>Reconciles durable print results with their waiting processes, including after a crash.</summary>
public sealed class PrintCheckpointProbe(PostgresDatabase database,IProcessStore processes) : IProcessProbe
{
    public async Task PollAsync(CancellationToken ct)
    {
        var results=new List<(string Id,string Status,string? Error)>();
        await using(var connection=await database.OpenAsync(ct))
        await using(var command=connection.CreateCommand())
        {
            command.CommandText="""
                SELECT j.id,j.status,j.error_code FROM print_jobs j JOIN processes p
                  ON p.resource_id=j.id AND p.expected_checkpoint='PdfReady'
                WHERE p.status NOT IN ('Completed','Failed') AND
                  ((j.status='Completed' AND EXISTS(SELECT 1 FROM pdf_artifacts a WHERE a.print_job_id=j.id)) OR j.status='Failed')
                """;
            await using var reader=await command.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct)) results.Add((reader.GetString(0),reader.GetString(1),reader.IsDBNull(2)?null:reader.GetString(2)));
        }
        foreach(var result in results)
            if(result.Status=="Completed") await processes.ConfirmAsync("PdfReady",result.Id,ct);
            else await processes.RejectCheckpointAsync("PdfReady",result.Id,result.Error??"PDF_RENDER_FAILED",ct);
    }
}
