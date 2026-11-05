using System.Net;
using AssembleAI.Platform.Audit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace AssembleAI.Platform.SmokeTests;

public static class AuditTests
{
    public static async Task RunAsync()
    {
        var baseline=new Dictionary<string,StringValues> { ["scope"]="tenant",["from"]="2026-11-01T00:00:00Z",["to"]="2026-12-02T00:00:00.000Z" };
        var query=AuditQuery.Parse(new QueryCollection(baseline));
        Assert(query.PageSize==50 && query.To-query.From==TimeSpan.FromDays(31),"Audit time windows accept exactly 31 days and default to 50 records.");
        foreach(var (key,value) in new[]{("tenantId","another-tenant"),("pageSize","101"),("pageSize","0"),("pageSize","1.0"),("scope","all"),
            ("from","2026-11-01"),("to","2026-12-02T00:00:00.000001Z"),("source","unknown"),("status","completed"),("requestId","x&secret=true"),("appId","../x")})
        {
            var invalid=new Dictionary<string,StringValues>(baseline){[key]=value}; Reject(()=>AuditQuery.Parse(new QueryCollection(invalid)));
        }
        var repeated=new Dictionary<string,StringValues>(baseline){["source"]=new StringValues(["model","identity"])};
        Reject(()=>AuditQuery.Parse(new QueryCollection(repeated)));
        Reject(()=>AuditQuery.Parse(new QueryCollection(new Dictionary<string,StringValues>(baseline){["pageSize"]="50"}),true));
        var clock=new Clock(); var cursor=new AuditCursor(new EphemeralDataProtectionProvider(),clock);
        var position=new AuditPosition(DateTimeOffset.Parse("2026-11-05T00:00:00.123456Z"),"model",Guid.NewGuid().ToString("N"));
        var text=cursor.Encode("subject-tenant-filters-epoch",position);
        Assert(cursor.Decode(text,"subject-tenant-filters-epoch")==position,"Audit pagination retains exact microsecond timestamps and tie breakers.");
        Reject(()=>cursor.Decode(text,"different-tenant")); Reject(()=>cursor.Decode(text[..^4]+"xxxx","subject-tenant-filters-epoch"));
        clock.Now=clock.Now.AddMinutes(15); Reject(()=>cursor.Decode(text,"subject-tenant-filters-epoch"));
        await using var app=PlatformApplication.Build(["--environment","Development","--urls","http://127.0.0.1:0","--Authentication:Bff:Enabled","false"],b=>b.Logging.ClearProviders());
        await app.StartAsync();
        try {
            using var client=new HttpClient{BaseAddress=new(app.Urls.Single())};
            foreach(var path in new[]{"/api/platform/v1/audit/workspace","/api/platform/v1/audit/events","/api/platform/v1/audit/events/model/"+position.Id}) {
                using var response=await client.GetAsync(path); Assert(response.StatusCode==HttpStatusCode.Unauthorized && response.Headers.CacheControl?.NoStore==true,"Audit reads require real authentication without caching.");
            }
            using var export=await client.PostAsync("/api/platform/v1/audit/export",new StringContent("{}",System.Text.Encoding.UTF8,"application/json"));
            Assert(export.StatusCode==HttpStatusCode.Unauthorized,"Anonymous audit exports cannot reach history or create audit records.");
        } finally {await app.StopAsync();}
        Console.WriteLine("PASS: audit strict query/range/page boundaries, protected cursor tamper/scope/expiry and anonymous no-store read/export boundary.");
    }
    private sealed class Clock:TimeProvider {public DateTimeOffset Now=DateTimeOffset.Parse("2026-11-05T00:00:00Z"); public override DateTimeOffset GetUtcNow()=>Now;}
    private static void Reject(Action action) {try{action();throw new InvalidOperationException("Expected an invalid audit request.");}catch(AuditRequestException e){Assert(e.Status==400,"Invalid audit parameters/cursors must fail safely.");}}
    private static void Assert(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
