
using System.Text.Json;
using Flurl.Http;
using Kommander.Data;

namespace Kommander.Communication;

public class HttpCommunication : ICommunication
{
    public async Task<RequestVotesResponse> RequestVotes(RaftManager manager, RaftPartition partition, RaftNode node, RequestVotesRequest request)
    {
        string payload = JsonSerializer.Serialize(request, RaftJsonContext.Default.RequestVotesRequest);
        
        try
        {
            return await ("http://" + node.Endpoint)
                .WithOAuthBearerToken("xxx")
                .AppendPathSegments("v1/raft/request-vote")
                .WithHeader("Accept", "application/json")
                .WithHeader("Content-Type", "application/json")
                .WithTimeout(5)
                .WithSettings(o => o.HttpVersion = "2.0")
                .PostStringAsync(payload)
                .ReceiveJson<RequestVotesResponse>();
        }
        catch (Exception e)
        {
            manager.Logger.LogError("[{Endpoint}/{Partition}] {Message}", manager.LocalEndpoint, partition.PartitionId, e.Message);
        }
        
        return new();
    }

    public async Task<VoteResponse> Vote(RaftManager manager, RaftPartition partition, RaftNode node, VoteRequest request)
    {
        string payload = JsonSerializer.Serialize(request, RaftJsonContext.Default.VoteRequest);
        
        try
        {
            return await ("http://" + node.Endpoint)
                .WithOAuthBearerToken("xxx")
                .AppendPathSegments("v1/raft/vote")
                .WithHeader("Accept", "application/json")
                .WithHeader("Content-Type", "application/json")
                .WithTimeout(5)
                .WithSettings(o => o.HttpVersion = "2.0")
                .PostStringAsync(payload)
                .ReceiveJson<VoteResponse>();
        }
        catch (Exception e)
        {
            manager.Logger.LogError("[{Endpoint}/{Partition}] {Message}", manager.LocalEndpoint, partition.PartitionId, e.Message);
        }

        return new();
    }

    public async Task<AppendLogsResponse> AppendLogToNode(RaftManager manager, RaftPartition partition, RaftNode node, AppendLogsRequest request)
    {
        string payload = JsonSerializer.Serialize(request, RaftJsonContext.Default.AppendLogsRequest);
        
        try
        {
            AppendLogsResponse? response = await ("http://" + node.Endpoint)
                .WithOAuthBearerToken("x")
                .AppendPathSegments("v1/raft/append-logs")
                .WithHeader("Accept", "application/json")
                .WithHeader("Content-Type", "application/json")
                .WithTimeout(10)
                .WithSettings(o => o.HttpVersion = "2.0")
                .PostStringAsync(payload)
                .ReceiveJson<AppendLogsResponse>();
            
            if (request.Logs is not null && request.Logs.Count > 0)
                manager.Logger.LogInformation("[{Endpoint}/{Partition}] Logs replicated to {RemoteEndpoint}", manager.LocalEndpoint, partition.PartitionId, node.Endpoint);

            return response;
        }
        catch (Exception e)
        {
            manager.Logger.LogError("[{Endpoint}/{Partition}] {Message}", manager.LocalEndpoint, partition.PartitionId, e.Message);
        }

        return new(RaftOperationStatus.Errored, -1);
    }
}