using System.Text;
using AssembleAI.Platform.Knowledge;

namespace AssembleAI.Platform.SmokeTests;

public static class KnowledgeBrokerTests
{
    public static Task RunAsync()
    {
        var id=Guid.NewGuid(); var bytes=KnowledgeBrokerNotification.Encode(id);
        Check(bytes.Length<=128 && KnowledgeBrokerNotification.Decode(bytes)==id,"A notification must carry only a bounded version and job identifier.");
        foreach(var body in new[]{"null","{}","{\"schemaVersion\":1,\"jobId\":\""+id+"\",\"content\":\"private text\"}",
            "{\"schemaVersion\":2,\"jobId\":\""+id+"\"}","{\"schemaVersion\":\"1\",\"jobId\":\""+id+"\"}",
            "{\"schemaVersion\":1,\"schemaVersion\":1,\"jobId\":\""+id+"\"}","{\"schemaVersion\":1,\"jobId\":\"00000000-0000-0000-0000-000000000000\"}",new string('x',513)})
            Check(KnowledgeBrokerNotification.Decode(Encoding.UTF8.GetBytes(body)) is null,"Ambiguous or unbounded broker notifications must not reach the worker.");
        const string variable="ASSEMBLEAI_KNOWLEDGE_BROKER_TEST_URI"; var previous=Environment.GetEnvironmentVariable(variable);
        var options=new KnowledgeOptions {BrokerEnabled=true,BrokerConnectionEnvironmentVariable=variable,BrokerQueue="fixture.knowledge",AllowInsecureLoopback=true};
        try
        {
            Environment.SetEnvironmentVariable(variable,"amqp://fixture:private-password@127.0.0.1:5672/%2f");
            Check(KnowledgeBrokerSettings.Read(options).Queue=="fixture.knowledge","A controlled loopback deployment must work without Docker.");
            foreach(var value in new[]{"amqp://fixture:private-password@external.invalid/%2f","https://fixture:private-password@127.0.0.1/","amqp://127.0.0.1/","amqp://fixture:private-password@127.0.0.1/%2f?x=1"})
            {
                Environment.SetEnvironmentVariable(variable,value);
                try { KnowledgeBrokerSettings.Read(options); throw new InvalidOperationException("Unsafe broker configuration accepted."); }
                catch(KnowledgeBrokerUnavailableException error) { Check(error.InnerException is null && !error.ToString().Contains("private-password",StringComparison.Ordinal),"Broker errors must not expose connection credentials."); }
            }
        }
        finally { Environment.SetEnvironmentVariable(variable,previous); }
        Console.WriteLine("PASS: knowledge broker contract: bounded metadata notifications, strict schema and safe transport configuration.");
        return Task.CompletedTask;
    }
    private static void Check(bool value,string message) { if(!value) throw new InvalidOperationException(message); }
}
