using System.Text.Json;
using System.Text.RegularExpressions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace AssembleAI.Platform.Knowledge;

public sealed class KnowledgeBrokerUnavailableException() : Exception("Knowledge task transport is unavailable.");
public sealed record KnowledgeBrokerSettings(Uri Endpoint,string Queue)
{
    public static KnowledgeBrokerSettings Read(KnowledgeOptions options)
    {
        if(!options.BrokerEnabled || options.BrokerConnectionEnvironmentVariable is null
            || !Regex.IsMatch(options.BrokerConnectionEnvironmentVariable,"\\A[A-Za-z_][A-Za-z0-9_]{0,127}\\z")
            || options.BrokerQueue is null || !Regex.IsMatch(options.BrokerQueue,"\\A[a-z][a-z0-9._-]{0,127}\\z")) throw new KnowledgeBrokerUnavailableException();
        var value=Environment.GetEnvironmentVariable(options.BrokerConnectionEnvironmentVariable);
        if(value is not {Length:>=1 and <=4096} || value.Any(char.IsControl) || !Uri.TryCreate(value,UriKind.Absolute,out var uri)
            || uri.Query.Length!=0 || uri.Fragment.Length!=0 || string.IsNullOrEmpty(uri.Host)
            || uri.UserInfo.Length<3 || uri.UserInfo.IndexOf(':') is <1 || uri.UserInfo.EndsWith(':')
            || !(uri.Scheme=="amqps" || uri.Scheme=="amqp" && uri.IsLoopback && options.AllowInsecureLoopback)) throw new KnowledgeBrokerUnavailableException();
        return new(uri,options.BrokerQueue);
    }
}
public static class KnowledgeBrokerNotification
{
    public static byte[] Encode(Guid jobId)
    {
        if(jobId==Guid.Empty) throw new KnowledgeBrokerUnavailableException();
        return JsonSerializer.SerializeToUtf8Bytes(new {schemaVersion=1,jobId});
    }
    public static Guid? Decode(ReadOnlyMemory<byte> value)
    {
        if(value.Length is <1 or >512) return null;
        try
        {
            using var json=JsonDocument.Parse(value,new(){MaxDepth=2}); var root=json.RootElement;
            if(!KnowledgeEmbeddingClient.Fields(root,"schemaVersion","jobId") || root.GetProperty("schemaVersion").ValueKind!=JsonValueKind.Number
                || !root.GetProperty("schemaVersion").TryGetInt32(out var version) || version!=1 || root.GetProperty("jobId").ValueKind!=JsonValueKind.String
                || !root.GetProperty("jobId").TryGetGuid(out var id) || id==Guid.Empty) return null;
            return id;
        }
        catch(Exception error) when(error is JsonException or InvalidOperationException or FormatException) { return null; }
    }
}

/// <summary>PostgreSQL is the durable outbox and job authority. Broker delivery is a replayable notification.</summary>
public sealed class KnowledgeBrokerWorker(KnowledgeOptions options,IKnowledgeJobStore jobs,IKnowledgeBrokerStore state,
    KnowledgeWorker processor,ILogger<KnowledgeBrokerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            try { await Connected(stoppingToken); }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { break; }
            catch(Exception error) when(Unavailable(error))
            { logger.LogWarning("Knowledge task transport is unavailable; durable jobs remain recoverable."); }
            try { await Task.Delay(TimeSpan.FromSeconds(2),stoppingToken); }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { break; }
        }
    }
    private async Task Connected(CancellationToken stoppingToken)
    {
        var settings=KnowledgeBrokerSettings.Read(options);
        var factory=new ConnectionFactory {Uri=settings.Endpoint,AutomaticRecoveryEnabled=false,ConsumerDispatchConcurrency=1,
            RequestedConnectionTimeout=TimeSpan.FromSeconds(3),HandshakeContinuationTimeout=TimeSpan.FromSeconds(3),
            ContinuationTimeout=TimeSpan.FromSeconds(3),RequestedHeartbeat=TimeSpan.FromSeconds(10),MaxInboundMessageBodySize=64*1024};
        using var session=CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var connect=CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); connect.CancelAfter(TimeSpan.FromSeconds(5));
        await using var connection=await factory.CreateConnectionAsync("assembleai-knowledge",connect.Token);
        connection.ConnectionShutdownAsync+=(_,_)=>{session.Cancel();return Task.CompletedTask;};
        var confirms=new CreateChannelOptions(publisherConfirmationsEnabled:true,publisherConfirmationTrackingEnabled:true);
        await using var publisher=await connection.CreateChannelAsync(confirms,session.Token);
        await using var consumerChannel=await connection.CreateChannelAsync(cancellationToken:session.Token);
        await using var deadletters=await connection.CreateChannelAsync(confirms,session.Token);
        publisher.ChannelShutdownAsync+=(_,_)=>{session.Cancel();return Task.CompletedTask;};
        consumerChannel.ChannelShutdownAsync+=(_,_)=>{session.Cancel();return Task.CompletedTask;};
        deadletters.ChannelShutdownAsync+=(_,_)=>{session.Cancel();return Task.CompletedTask;};
        try
        {
            var arguments=new Dictionary<string,object?> { ["x-max-length"]=1000,["x-max-length-bytes"]=512000,["x-overflow"]="reject-publish" };
            await publisher.QueueDeclareAsync(settings.Queue,durable:true,exclusive:false,autoDelete:false,arguments:arguments,cancellationToken:session.Token);
            await publisher.QueueDeclareAsync(settings.Queue+".dead",durable:true,exclusive:false,autoDelete:false,arguments:arguments,cancellationToken:session.Token);
            await consumerChannel.BasicQosAsync(0,1,false,session.Token);
            var consumer=new AsyncEventingBasicConsumer(consumerChannel);
            consumer.UnregisteredAsync+=(_,_)=>{session.Cancel();return Task.CompletedTask;};
            consumer.ReceivedAsync+=async(_,delivery)=>
            {
                // Decode before the first await: RabbitMQ owns the delivery memory after this callback returns.
                var id=KnowledgeBrokerNotification.Decode(delivery.Body);
                try
                {
                    if(id is {} jobId)
                    {
                        await processor.ProcessAsync(jobId,session.Token);
                        if(await state.ReadJobAsync(jobId,session.Token) is {Status:"failed"})
                        {
                            // A terminal job remains durable in PG. Confirm its metadata-only diagnostic before ACK.
                            while(true)
                            {
                                try { await Publish(deadletters,settings.Queue+".dead",jobId,session.Token); break; }
                                catch(PublishException) { await Task.Delay(TimeSpan.FromSeconds(2),session.Token); }
                            }
                        }
                    }
                    // Invalid or unknown identifiers have no work authority and are discarded without echoing their body.
                    await consumerChannel.BasicAckAsync(delivery.DeliveryTag,false,session.Token);
                }
                catch(OperationCanceledException) when(session.IsCancellationRequested) { }
                catch(Exception error) when(Unavailable(error))
                { await session.CancelAsync(); }
            };
            await consumerChannel.BasicConsumeAsync(settings.Queue,autoAck:false,consumer:consumer,cancellationToken:session.Token);
            while(!session.IsCancellationRequested)
            {
                foreach(var job in await jobs.PendingAsync(session.Token))
                {
                    try { await Publish(publisher,settings.Queue,job.JobId,session.Token); }
                    catch(PublishException) { break; } // Backpressure must not cancel the active consumer.
                }
                await Task.Delay(TimeSpan.FromSeconds(2),session.Token);
            }
        }
        finally { await session.CancelAsync(); }
    }
    private static async Task Publish(IChannel channel,string queue,Guid jobId,CancellationToken token)
    {
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromSeconds(3));
        var properties=new BasicProperties {Persistent=true,ContentType="application/json",MessageId=jobId.ToString("N")};
        await channel.BasicPublishAsync(exchange:"",routingKey:queue,mandatory:true,basicProperties:properties,
            body:KnowledgeBrokerNotification.Encode(jobId),cancellationToken:budget.Token);
    }
    private static bool Unavailable(Exception error)=>KnowledgeEndpoints.Unavailable(error)
        || error is KnowledgeBrokerUnavailableException or RabbitMQClientException or IOException or TimeoutException
            or ArgumentException or InvalidOperationException or OperationCanceledException;
}
