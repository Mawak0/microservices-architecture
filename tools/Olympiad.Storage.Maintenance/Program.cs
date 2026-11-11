using System.Text.Json;
using Olympiad.Persistence.Postgres;
using Olympiad.Storage.Maintenance;
using Olympiad.Storage.S3;

if(args.Length<1||args[0] is not ("materials" or "intake")||args.Skip(1).Any(a=>a is not ("--apply" or "--services-stopped")))
    throw new ArgumentException("Usage: dotnet run --project tools/Olympiad.Storage.Maintenance -- materials|intake [--apply --services-stopped]");
var apply=args.Contains("--apply");
if(apply&&!args.Contains("--services-stopped")) throw new ArgumentException("Deletion requires --services-stopped after stopping every service replica.");
string Required(string key)=>Environment.GetEnvironmentVariable(key)??throw new InvalidOperationException("Set "+key);
var service=args[0];
var settings=new S3Options(Required("S3_ENDPOINT"),Environment.GetEnvironmentVariable("S3_REGION")??"us-east-1",Required("S3_ACCESS_KEY"),Required("S3_SECRET_KEY"),service=="materials"?"print-materials":"answer-images");
using var client=settings.CreateClient();
var minimumAge=Environment.GetEnvironmentVariable("MAINTENANCE_MINIMUM_AGE_HOURS") is { } hours?double.Parse(hours,System.Globalization.CultureInfo.InvariantCulture):24;
if(!double.IsFinite(minimumAge)||minimumAge<0) throw new ArgumentException("Minimum age must be a non-negative number of hours.");
var collector=new OrphanCollector(new PostgresStore(Required("MAINTENANCE_CONNECTION_STRING")),new S3Objects(client,settings.Bucket),service);
var keys=await collector.ReconcileAsync(DateTimeOffset.UtcNow.AddHours(-minimumAge),apply,default);
Console.WriteLine(JsonSerializer.Serialize(new {service,applied=apply,count=keys.Count,keys}));
